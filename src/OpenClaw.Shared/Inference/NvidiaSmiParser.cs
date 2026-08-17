using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Pure parser for the two <c>nvidia-smi</c> queries the hardware probe runs.
/// Kept separate from the probe so the (fiddly, vendor-controlled) text formats
/// can be unit tested against captured real output without spawning a process.
/// </summary>
public static class NvidiaSmiParser
{
    /// <summary>
    /// Arguments for the per-GPU query. CSV with no header and no units keeps the
    /// output machine-readable across driver versions.
    /// </summary>
    public static readonly string[] QueryGpuArgs =
    [
        "--query-gpu=name,memory.total,driver_version",
        "--format=csv,noheader,nounits",
    ];

    /// <summary>
    /// Parse <c>--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits</c>
    /// output into one <see cref="GpuInfo"/> per line.
    /// </summary>
    /// <param name="stdout">Raw stdout. Null/empty yields an empty list.</param>
    /// <param name="cudaMajorVersion">
    /// CUDA major version to stamp on every returned adapter, from
    /// <see cref="TryParseCudaMajorVersion"/>. Null when unknown.
    /// </param>
    /// <remarks>
    /// <c>memory.total</c> is reported in MiB. A row whose memory field is absent,
    /// non-numeric, or the literal <c>[N/A]</c> (which the driver emits for some
    /// virtualized adapters) yields a null size rather than a zero, so the caller
    /// can tell "no VRAM" apart from "unknown VRAM".
    /// </remarks>
    public static IReadOnlyList<GpuInfo> ParseQueryGpu(string? stdout, int? cudaMajorVersion = null)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<GpuInfo>();

        var results = new List<GpuInfo>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var fields = line.Split(',');
            if (fields.Length < 1) continue;

            var name = fields[0].Trim();
            if (name.Length == 0) continue;

            var memoryBytes = fields.Length > 1 ? ParseMebibytes(fields[1]) : null;
            var driver = fields.Length > 2 ? NullIfBlank(fields[2]) : null;

            results.Add(new GpuInfo(
                GpuVendor.Nvidia,
                name,
                memoryBytes,
                driver,
                cudaMajorVersion));
        }

        return results;
    }

    /// <summary>
    /// Extract the CUDA major version from plain <c>nvidia-smi</c> output, whose
    /// header line reads e.g.
    /// <c>| NVIDIA-SMI 570.86.10  Driver Version: 570.86.10  CUDA Version: 12.8 |</c>.
    /// Returns null when the marker is missing or unparseable.
    /// </summary>
    public static int? TryParseCudaMajorVersion(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        const string marker = "CUDA Version:";
        var index = stdout.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var rest = stdout.AsSpan(index + marker.Length).TrimStart();

        // Take the leading digit run; "12.8" and "13" both yield the major part.
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        if (end == 0) return null;

        return int.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }

    /// <summary>
    /// Classify an adapter name reported by a non-NVIDIA source (WMI) into a vendor.
    /// Deliberately conservative: anything unrecognized stays
    /// <see cref="GpuVendor.Unknown"/> so the selector does not assume a Vulkan
    /// build will drive it.
    /// </summary>
    public static GpuVendor ClassifyVendor(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return GpuVendor.Unknown;

        var name = adapterName.Trim();
        if (Contains(name, "nvidia") || Contains(name, "geforce") || Contains(name, "quadro") || Contains(name, "tesla"))
            return GpuVendor.Nvidia;
        if (Contains(name, "amd") || Contains(name, "radeon") || Contains(name, "advanced micro devices"))
            return GpuVendor.Amd;
        if (Contains(name, "intel") || Contains(name, "arc(tm)"))
            return GpuVendor.Intel;

        return GpuVendor.Unknown;

        static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static long? ParseMebibytes(string field)
    {
        var text = field.Trim();
        if (text.Length == 0 || text.Equals("[N/A]", StringComparison.OrdinalIgnoreCase)) return null;

        // Tolerate a stray unit suffix if a future driver stops honoring `nounits`.
        if (text.EndsWith("MiB", StringComparison.OrdinalIgnoreCase))
            text = text[..^3].Trim();

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) || mib <= 0)
            return null;

        return mib * 1024L * 1024L;
    }

    private static string? NullIfBlank(string field)
    {
        var trimmed = field.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
