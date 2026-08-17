using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Detects the inference-relevant hardware on this host: CPU architecture,
/// installed RAM, and graphics adapters (with NVIDIA VRAM and CUDA version).
///
/// <para><b>Contract: this probe never throws.</b> Every source is best-effort and
/// every failure degrades to a null/empty field. A host we cannot classify must
/// end up on the CPU backend, not crash the settings page.</para>
///
/// <para>NVIDIA detection goes through <c>nvidia-smi</c>, which is installed into
/// System32 by the display driver. It is the only source that reports a correct
/// VRAM size: <c>Win32_VideoController.AdapterRAM</c> is a signed 32-bit field
/// that wraps above 4 GB, which is exactly the range we care about.</para>
/// </summary>
public sealed class HardwareProbe
{
    /// <summary>nvidia-smi is slow to start on some systems but never this slow.</summary>
    private const int NvidiaSmiTimeoutMs = 10_000;

    private readonly ICommandRunner _commandRunner;
    private readonly IOpenClawLogger _logger;
    private readonly Func<IReadOnlyList<GpuInfo>> _fallbackGpuEnumerator;
    private readonly Func<bool> _vulkanLoaderProbe;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private HostHardwareInfo? _cached;

    /// <param name="commandRunner">Used to invoke <c>nvidia-smi</c>. Injectable so tests need no GPU.</param>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="fallbackGpuEnumerator">
    /// Optional platform-specific adapter enumeration used when <c>nvidia-smi</c>
    /// is absent. Lives outside this assembly because reading the display-adapter
    /// registry class requires a Windows-targeted TFM. Defaults to "no adapters".
    /// </param>
    /// <param name="vulkanLoaderProbe">
    /// Optional override for Vulkan loader detection. Defaults to checking for
    /// <c>vulkan-1.dll</c> in the system directory.
    /// </param>
    public HardwareProbe(
        ICommandRunner commandRunner,
        IOpenClawLogger logger,
        Func<IReadOnlyList<GpuInfo>>? fallbackGpuEnumerator = null,
        Func<bool>? vulkanLoaderProbe = null)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallbackGpuEnumerator = fallbackGpuEnumerator ?? (static () => Array.Empty<GpuInfo>());
        _vulkanLoaderProbe = vulkanLoaderProbe ?? DefaultVulkanLoaderProbe;
    }

    /// <summary>
    /// The most recent probe result, or null if <see cref="GetAsync"/> has never
    /// completed. Non-blocking; for UI that wants to render before the first probe.
    /// </summary>
    public HostHardwareInfo? Cached => _cached;

    /// <summary>
    /// Returns the cached hardware snapshot, probing once on first call.
    /// Concurrent callers share a single probe.
    /// </summary>
    public async Task<HostHardwareInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached) return cached;
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-runs detection and replaces the cache. Backs the settings page's
    /// "Re-detect" action, which exists because a driver install or an eGPU
    /// hotplug changes the answer without an app restart.
    /// </summary>
    public async Task<HostHardwareInfo> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
            _cached = info;
            return info;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<HostHardwareInfo> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        var memory = PhysicalMemoryProbe.TryRead();
        var gpus = await ProbeGpusAsync(cancellationToken).ConfigureAwait(false);

        bool vulkan;
        // slopwatch-ignore: SW003 Probe is best-effort by contract; an unreadable system directory means "no Vulkan".
        try { vulkan = _vulkanLoaderProbe(); } catch { vulkan = false; }

        var info = new HostHardwareInfo(
            RuntimeInformation.OSArchitecture,
            memory?.TotalBytes,
            memory?.AvailableBytes,
            gpus,
            vulkan);

        _logger.Info(
            $"[HardwareProbe] arch={info.CpuArchitecture} ram={FormatGib(info.TotalPhysicalMemoryBytes)} " +
            $"gpus={info.Gpus.Count} nvidiaVram={FormatGib(info.TotalNvidiaVramBytes)} " +
            $"cuda={info.MaxCudaMajorVersion?.ToString() ?? "unknown"} vulkan={vulkan}");

        return info;
    }

    private async Task<IReadOnlyList<GpuInfo>> ProbeGpusAsync(CancellationToken cancellationToken)
    {
        // Pass 1: plain `nvidia-smi` for the CUDA version banner. Its absence is the
        // normal, expected result on a machine with no NVIDIA driver, so a failure
        // here is logged at debug volume, not as a warning.
        var banner = await RunNvidiaSmiAsync(Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
        var cudaMajor = NvidiaSmiParser.TryParseCudaMajorVersion(banner);

        // Pass 2: the machine-readable per-GPU query.
        var query = await RunNvidiaSmiAsync(NvidiaSmiParser.QueryGpuArgs, cancellationToken).ConfigureAwait(false);
        var nvidiaGpus = NvidiaSmiParser.ParseQueryGpu(query, cudaMajor);

        if (nvidiaGpus.Count > 0) return nvidiaGpus;

        // No NVIDIA driver. Fall back to platform adapter enumeration so we can
        // still tell "AMD/Intel GPU present, try Vulkan" from "no GPU at all".
        try
        {
            return _fallbackGpuEnumerator();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[HardwareProbe] Adapter fallback enumeration failed: {ex.Message}");
            return Array.Empty<GpuInfo>();
        }
    }

    /// <summary>
    /// Invoke nvidia-smi and return stdout, or null when it is missing or failed.
    /// Tries PATH first, then the System32 copy the driver installs, because a
    /// PATH-less service context is a real deployment shape.
    /// </summary>
    private async Task<string?> RunNvidiaSmiAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        foreach (var executable in EnumerateNvidiaSmiCandidates())
        {
            var argv = new List<string>(args.Count + 1) { executable };
            argv.AddRange(args);

            CommandResult result;
            try
            {
                result = await _commandRunner.RunAsync(
                    new CommandRequest { Argv = argv, TimeoutMs = NvidiaSmiTimeoutMs },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Info($"[HardwareProbe] nvidia-smi ({executable}) not usable: {ex.Message}");
                continue;
            }

            if (result.TimedOut)
            {
                _logger.Warn($"[HardwareProbe] nvidia-smi ({executable}) timed out after {NvidiaSmiTimeoutMs} ms");
                continue;
            }

            if (result.ExitCode != 0)
            {
                _logger.Info($"[HardwareProbe] nvidia-smi ({executable}) exited {result.ExitCode}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Stdout)) return result.Stdout;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateNvidiaSmiCandidates()
    {
        yield return "nvidia-smi";

        string? system32 = null;
        // slopwatch-ignore: SW003 Best-effort path resolution; the PATH candidate above already covers the normal case.
        try { system32 = Environment.SystemDirectory; } catch { /* ignore */ }

        if (!string.IsNullOrWhiteSpace(system32))
            yield return Path.Combine(system32, "nvidia-smi.exe");
    }

    private static bool DefaultVulkanLoaderProbe()
    {
        var system32 = Environment.SystemDirectory;
        return !string.IsNullOrWhiteSpace(system32)
            && File.Exists(Path.Combine(system32, "vulkan-1.dll"));
    }

    private static string FormatGib(long? bytes) =>
        bytes is { } value && value > 0
            ? $"{value / (1024.0 * 1024 * 1024):F1}GiB"
            : "unknown";
}
