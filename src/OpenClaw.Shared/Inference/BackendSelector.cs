using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// The backend plan for a host: the preferred variant plus the ordered
/// fallbacks to try if it fails to launch.
/// </summary>
/// <param name="Preferred">
/// Best variant for this hardware, or null when the pinned release has no build
/// for this architecture at all.
/// </param>
/// <param name="Fallbacks">
/// Variants to try, in order, after <see cref="Preferred"/> fails. A CUDA build
/// can fail at launch for reasons the probe cannot see (driver too old for the
/// CUDA runtime, GPU in TCC mode, VRAM already claimed), so having a real
/// degradation path matters more here than in most download flows.
/// </param>
/// <param name="Reason">Short PII-free explanation of the choice, shown in the UI.</param>
public sealed record BackendPlan(
    LlamaBackendVariant? Preferred,
    IReadOnlyList<LlamaBackendVariant> Fallbacks,
    string Reason)
{
    /// <summary>Preferred first, then each fallback. Empty when nothing is available.</summary>
    public IEnumerable<LlamaBackendVariant> InPreferenceOrder
    {
        get
        {
            if (Preferred is not null) yield return Preferred;
            foreach (var fallback in Fallbacks) yield return fallback;
        }
    }
}

/// <summary>
/// Maps detected hardware onto a llama.cpp backend variant from
/// <see cref="LlamaBackendCatalog"/>.
///
/// <para>Pure and total: no I/O, no throwing, and every input shape produces a
/// plan (possibly an empty one). This is the piece that decides whether a user
/// gets GPU inference at all, so it is exhaustively unit tested rather than
/// discovered in the field.</para>
/// </summary>
public static class BackendSelector
{
    /// <summary>
    /// Choose the backend plan for <paramref name="hardware"/>.
    /// </summary>
    /// <param name="hardware">Probe result. <see cref="HostHardwareInfo.Unknown"/> yields the CPU plan.</param>
    /// <param name="userOverride">
    /// Explicit user choice from settings. When it names a variant that exists for
    /// this architecture it wins outright, and the auto-selected plan becomes the
    /// fallback chain. An override naming a build this release does not have for
    /// this architecture is ignored rather than honored into a dead end.
    /// </param>
    public static BackendPlan Select(HostHardwareInfo hardware, LlamaBackend? userOverride = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        var arch = NormalizeArchitecture(hardware.CpuArchitecture);
        var auto = SelectAutomatic(hardware, arch);

        if (userOverride is not { } requested) return auto;

        var overrideVariant = LlamaBackendCatalog.Find(requested, arch);
        if (overrideVariant is null) return auto with { Reason = $"{auto.Reason} (override {requested} unavailable for {arch})" };

        var fallbacks = auto.InPreferenceOrder
            .Where(v => !ReferenceEquals(v, overrideVariant))
            .ToArray();

        return new BackendPlan(overrideVariant, fallbacks, $"Backend overridden to {overrideVariant.DisplayName}.");
    }

    private static BackendPlan SelectAutomatic(HostHardwareInfo hardware, Architecture arch)
    {
        var cpu = LlamaBackendCatalog.Find(LlamaBackend.Cpu, arch);
        var vulkan = LlamaBackendCatalog.Find(LlamaBackend.Vulkan, arch);

        if (hardware.HasNvidiaGpu)
        {
            // CUDA 13 builds require a driver new enough for the CUDA 13 runtime.
            // When nvidia-smi did not report a version we take the CUDA 12 build:
            // a CUDA 12 runtime is forward-compatible with newer drivers, while the
            // reverse is not true, so the unknown case must degrade downward.
            var wantsCuda13 = hardware.MaxCudaMajorVersion is >= 13;
            var primary = LlamaBackendCatalog.Find(wantsCuda13 ? LlamaBackend.Cuda13 : LlamaBackend.Cuda12, arch);
            var secondary = LlamaBackendCatalog.Find(wantsCuda13 ? LlamaBackend.Cuda12 : LlamaBackend.Cuda13, arch);

            var chosen = primary ?? secondary;
            if (chosen is not null)
            {
                var chain = new List<LlamaBackendVariant>();
                if (secondary is not null && !ReferenceEquals(secondary, chosen)) chain.Add(secondary);
                if (vulkan is not null && hardware.VulkanAvailable) chain.Add(vulkan);
                if (cpu is not null) chain.Add(cpu);

                var cudaLabel = hardware.MaxCudaMajorVersion is { } major ? $"CUDA {major}.x" : "CUDA version unknown";
                var reason = $"NVIDIA GPU detected ({cudaLabel}); using {chosen.DisplayName}.";

                // The preferred build can be unavailable for this architecture:
                // the pinned release ships no CUDA 12 ARM64 build, so an ARM64
                // host with a CUDA 12 driver lands on the CUDA 13 build. That may
                // fail to load, and the fallback chain will handle it, but the
                // user is told rather than left to read a driver error.
                if (!wantsCuda13
                    && chosen.Backend == LlamaBackend.Cuda13
                    && hardware.MaxCudaMajorVersion is { } reported)
                {
                    reason += $" This release has no CUDA {reported} build for {arch}, " +
                              "so a newer CUDA runtime is used and may not load with the installed driver.";
                }

                return new BackendPlan(chosen, chain, reason);
            }

            // NVIDIA hardware but no CUDA build for this architecture.
            return BuildNonCudaPlan(hardware, vulkan, cpu, "NVIDIA GPU detected but this release has no CUDA build for " + arch + ".");
        }

        if (hardware.HasNonNvidiaGpu)
        {
            return BuildNonCudaPlan(
                hardware,
                vulkan,
                cpu,
                hardware.VulkanAvailable
                    ? "Non-NVIDIA GPU detected with a Vulkan loader present."
                    : "Non-NVIDIA GPU detected but no Vulkan loader is installed.");
        }

        return cpu is null
            ? new BackendPlan(null, Array.Empty<LlamaBackendVariant>(), $"No llama.cpp build is available for {arch}.")
            : new BackendPlan(cpu, Array.Empty<LlamaBackendVariant>(), "No supported GPU detected; using the CPU build.");
    }

    private static BackendPlan BuildNonCudaPlan(
        HostHardwareInfo hardware,
        LlamaBackendVariant? vulkan,
        LlamaBackendVariant? cpu,
        string reason)
    {
        // Vulkan is only preferred when a loader is actually installed. Shipping a
        // Vulkan build to a machine without vulkan-1.dll just moves the failure
        // from "no GPU acceleration" to "server will not start".
        if (vulkan is not null && hardware.VulkanAvailable)
        {
            IReadOnlyList<LlamaBackendVariant> fallbacks = cpu is null ? [] : [cpu];
            return new BackendPlan(vulkan, fallbacks, $"{reason} Using {vulkan.DisplayName}.");
        }

        return cpu is null
            ? new BackendPlan(null, Array.Empty<LlamaBackendVariant>(), reason)
            : new BackendPlan(cpu, Array.Empty<LlamaBackendVariant>(), $"{reason} Falling back to the CPU build.");
    }

    /// <summary>
    /// Collapse the architectures we do not ship builds for onto the ones we do.
    /// x86 hosts run the x64 build under WOW64 emulation rather than getting
    /// nothing, and any unrecognized architecture is treated as x64.
    /// </summary>
    private static Architecture NormalizeArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => Architecture.Arm64,
        _ => Architecture.X64,
    };
}
