using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// GPU vendor, as far as the probe could determine it. <see cref="Unknown"/>
/// means "we saw an adapter but could not classify it" and must be treated the
/// same as "no usable accelerator" by the backend selector.
/// </summary>
public enum GpuVendor
{
    Unknown = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
    Other = 4,
}

/// <summary>
/// One detected graphics adapter.
/// </summary>
/// <param name="Vendor">Classified vendor.</param>
/// <param name="Name">Adapter name as reported by the source (e.g. "NVIDIA RTX 6000 Ada Generation").</param>
/// <param name="DedicatedMemoryBytes">
/// Dedicated video memory in bytes, or null when unknown. Only ever populated
/// from a trustworthy source (nvidia-smi). WMI's <c>Win32_VideoController.AdapterRAM</c>
/// is a 32-bit field that wraps above 4 GB, so the WMI fallback deliberately
/// leaves this null rather than reporting a wrong number.
/// </param>
/// <param name="DriverVersion">Display driver version, when known.</param>
/// <param name="CudaMajorVersion">
/// Major version of the CUDA runtime the driver supports, when known. Drives the
/// choice between the CUDA 12.x and CUDA 13.x llama.cpp builds.
/// </param>
public sealed record GpuInfo(
    GpuVendor Vendor,
    string Name,
    long? DedicatedMemoryBytes = null,
    string? DriverVersion = null,
    int? CudaMajorVersion = null);

/// <summary>
/// Snapshot of the host's inference-relevant hardware. Every field is optional:
/// the probe never throws, and unknown values degrade to null so the backend
/// selector falls through to CPU rather than guessing.
/// </summary>
/// <param name="CpuArchitecture">OS architecture (x64 / Arm64 in practice).</param>
/// <param name="TotalPhysicalMemoryBytes">Installed system RAM, or null when the query failed.</param>
/// <param name="AvailablePhysicalMemoryBytes">Currently free system RAM, or null when the query failed.</param>
/// <param name="Gpus">All detected adapters, in the order the source reported them.</param>
/// <param name="VulkanAvailable">True when a Vulkan loader is present on the machine.</param>
public sealed record HostHardwareInfo(
    Architecture CpuArchitecture,
    long? TotalPhysicalMemoryBytes,
    long? AvailablePhysicalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus,
    bool VulkanAvailable)
{
    /// <summary>
    /// The "we learned nothing" result. Used when every probe path failed; the
    /// selector maps this to the CPU backend.
    /// </summary>
    public static HostHardwareInfo Unknown { get; } = new(
        RuntimeInformation.OSArchitecture,
        null,
        null,
        Array.Empty<GpuInfo>(),
        false);

    /// <summary>All adapters classified as NVIDIA.</summary>
    public IEnumerable<GpuInfo> NvidiaGpus => Gpus.Where(g => g.Vendor == GpuVendor.Nvidia);

    /// <summary>True when at least one NVIDIA adapter was detected.</summary>
    public bool HasNvidiaGpu => Gpus.Any(g => g.Vendor == GpuVendor.Nvidia);

    /// <summary>
    /// True when a non-NVIDIA adapter that a Vulkan build could drive was detected.
    /// <see cref="GpuVendor.Unknown"/> does not count: an unclassified adapter is
    /// not evidence that a Vulkan build will work.
    /// </summary>
    public bool HasNonNvidiaGpu =>
        Gpus.Any(g => g.Vendor is GpuVendor.Amd or GpuVendor.Intel or GpuVendor.Other);

    /// <summary>
    /// Combined dedicated VRAM across all NVIDIA adapters whose size is known, or
    /// null when no NVIDIA adapter reported a size. llama.cpp's default
    /// <c>--split-mode layer</c> spreads a model across every visible device, so
    /// the sum (not the maximum) is the capacity that matters for model fit.
    /// </summary>
    public long? TotalNvidiaVramBytes
    {
        get
        {
            long total = 0;
            var sawAny = false;
            foreach (var gpu in NvidiaGpus)
            {
                if (gpu.DedicatedMemoryBytes is not { } bytes || bytes <= 0) continue;
                total += bytes;
                sawAny = true;
            }
            return sawAny ? total : null;
        }
    }

    /// <summary>
    /// Highest CUDA major version reported by any NVIDIA adapter, or null when
    /// unknown. Null must be treated as "assume the older CUDA build".
    /// </summary>
    public int? MaxCudaMajorVersion
    {
        get
        {
            int? best = null;
            foreach (var gpu in NvidiaGpus)
            {
                if (gpu.CudaMajorVersion is not { } major) continue;
                if (best is null || major > best) best = major;
            }
            return best;
        }
    }
}
