using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Shared.Inference;
using Xunit;

// Disambiguates from the OpenClaw.Shared.Tests.Architecture namespace (the
// architecture-guard test folder), which otherwise shadows the enum.
using Arch = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// The selector decides whether a user gets GPU inference at all, and its inputs
/// (driver versions, adapter vendors, architectures) are impossible to cover on
/// one CI machine. Every branch is therefore pinned here against synthetic
/// <see cref="HostHardwareInfo"/> values.
/// </summary>
public class BackendSelectorTests
{
    private static HostHardwareInfo Host(
        Arch arch = Arch.X64,
        long? ram = 64L * 1024 * 1024 * 1024,
        IReadOnlyList<GpuInfo>? gpus = null,
        bool vulkan = false) =>
        new(arch, ram, ram, gpus ?? Array.Empty<GpuInfo>(), vulkan);

    private static GpuInfo Nvidia(long vramGib = 24, int? cudaMajor = 12) =>
        new(GpuVendor.Nvidia, "NVIDIA Test GPU", vramGib * 1024 * 1024 * 1024, "999.99", cudaMajor);

    [Fact]
    public void NvidiaWithCuda13_PrefersTheCuda13Build()
    {
        var plan = BackendSelector.Select(Host(gpus: [Nvidia(cudaMajor: 13)]));

        Assert.Equal(LlamaBackend.Cuda13, plan.Preferred!.Backend);
        Assert.Equal(Arch.X64, plan.Preferred.Architecture);
    }

    [Fact]
    public void NvidiaWithCuda12_PrefersTheCuda12Build()
    {
        var plan = BackendSelector.Select(Host(gpus: [Nvidia(cudaMajor: 12)]));

        Assert.Equal(LlamaBackend.Cuda12, plan.Preferred!.Backend);
    }

    [Fact]
    public void NvidiaWithUnknownCudaVersion_DegradesToTheCuda12Build()
    {
        // A CUDA 12 runtime works on newer drivers; a CUDA 13 runtime does not
        // work on older ones. The unknown case must degrade downward.
        var plan = BackendSelector.Select(Host(gpus: [Nvidia(cudaMajor: null)]));

        Assert.Equal(LlamaBackend.Cuda12, plan.Preferred!.Backend);
        Assert.Contains("CUDA version unknown", plan.Reason);
    }

    [Fact]
    public void CudaVariantsAlwaysCarryTheirCudartArchive()
    {
        // Missing cudart is the classic "llama-server.exe won't start and the
        // error names a DLL" failure, so the pairing is asserted structurally.
        foreach (var variant in LlamaBackendCatalog.Variants
                     .Where(v => v.Backend is LlamaBackend.Cuda12 or LlamaBackend.Cuda13))
        {
            Assert.Equal(2, variant.Assets.Count);
            Assert.Contains(variant.Assets, a => a.FileName.StartsWith("cudart-", StringComparison.Ordinal));
            Assert.Contains(variant.Assets, a => a.FileName.StartsWith("llama-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NvidiaOnArm64_SelectsTheArm64CudaBuild()
    {
        var plan = BackendSelector.Select(Host(Arch.Arm64, gpus: [Nvidia(cudaMajor: 13)]));

        Assert.Equal(LlamaBackend.Cuda13, plan.Preferred!.Backend);
        Assert.Equal(Arch.Arm64, plan.Preferred.Architecture);
    }

    [Fact]
    public void NvidiaPlan_FallsBackThroughTheOtherCudaBuildThenCpu()
    {
        var plan = BackendSelector.Select(Host(gpus: [Nvidia(cudaMajor: 13)], vulkan: true));

        var order = plan.InPreferenceOrder.Select(v => v.Backend).ToArray();
        Assert.Equal(
            [LlamaBackend.Cuda13, LlamaBackend.Cuda12, LlamaBackend.Vulkan, LlamaBackend.Cpu],
            order);
    }

    [Fact]
    public void AmdGpuWithVulkanLoader_SelectsVulkan()
    {
        var gpus = new[] { new GpuInfo(GpuVendor.Amd, "AMD Radeon RX 7900 XTX") };

        var plan = BackendSelector.Select(Host(gpus: gpus, vulkan: true));

        Assert.Equal(LlamaBackend.Vulkan, plan.Preferred!.Backend);
        Assert.Equal(LlamaBackend.Cpu, Assert.Single(plan.Fallbacks).Backend);
    }

    [Fact]
    public void AmdGpuWithoutVulkanLoader_FallsBackToCpu()
    {
        // Shipping a Vulkan build to a host with no loader turns "no acceleration"
        // into "the server refuses to start", which is strictly worse.
        var gpus = new[] { new GpuInfo(GpuVendor.Amd, "AMD Radeon RX 7900 XTX") };

        var plan = BackendSelector.Select(Host(gpus: gpus, vulkan: false));

        Assert.Equal(LlamaBackend.Cpu, plan.Preferred!.Backend);
    }

    [Fact]
    public void UnclassifiedAdapter_IsNotTreatedAsVulkanCapable()
    {
        var gpus = new[] { new GpuInfo(GpuVendor.Unknown, "Microsoft Basic Display Adapter") };

        var plan = BackendSelector.Select(Host(gpus: gpus, vulkan: true));

        Assert.Equal(LlamaBackend.Cpu, plan.Preferred!.Backend);
    }

    [Fact]
    public void NoGpu_SelectsCpuForTheHostArchitecture()
    {
        Assert.Equal(LlamaBackend.Cpu, BackendSelector.Select(Host()).Preferred!.Backend);

        var arm = BackendSelector.Select(Host(Arch.Arm64));
        Assert.Equal(LlamaBackend.Cpu, arm.Preferred!.Backend);
        Assert.Equal(Arch.Arm64, arm.Preferred.Architecture);
    }

    [Fact]
    public void UnknownHardware_SelectsCpu()
    {
        var plan = BackendSelector.Select(HostHardwareInfo.Unknown);

        Assert.Equal(LlamaBackend.Cpu, plan.Preferred!.Backend);
    }

    [Fact]
    public void X86Host_IsCollapsedOntoTheX64Build()
    {
        var plan = BackendSelector.Select(Host(Arch.X86));

        Assert.Equal(Arch.X64, plan.Preferred!.Architecture);
    }

    [Fact]
    public void UserOverride_WinsAndKeepsTheAutoPlanAsFallback()
    {
        var plan = BackendSelector.Select(Host(gpus: [Nvidia()]), LlamaBackend.Cpu);

        Assert.Equal(LlamaBackend.Cpu, plan.Preferred!.Backend);
        Assert.Contains(plan.Fallbacks, v => v.Backend == LlamaBackend.Cuda12);
        Assert.DoesNotContain(plan.Fallbacks, v => ReferenceEquals(v, plan.Preferred));
    }

    [Fact]
    public void UserOverride_IsIgnoredWhenTheReleaseHasNoSuchBuildForTheArchitecture()
    {
        // This release ships no ARM64 Vulkan build. Honoring the override would
        // produce a plan that can never launch.
        var plan = BackendSelector.Select(Host(Arch.Arm64), LlamaBackend.Vulkan);

        Assert.Equal(LlamaBackend.Cpu, plan.Preferred!.Backend);
        Assert.Contains("override", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryCatalogAssetUsesAnHttpsUrlUnderThePinnedReleaseTag()
    {
        Assert.NotEmpty(LlamaBackendCatalog.Variants);
        foreach (var asset in LlamaBackendCatalog.Variants.SelectMany(v => v.Assets))
        {
            Assert.StartsWith("https://", asset.DownloadUrl, StringComparison.Ordinal);
            Assert.Contains($"/{LlamaBackendCatalog.ReleaseTag}/", asset.DownloadUrl, StringComparison.Ordinal);
            Assert.EndsWith(".zip", asset.FileName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VariantsWithoutAPinnedHashAreNotDownloadable()
    {
        // Fail-closed guard, mirroring the audio catalogs: an entry that has not
        // been hashed yet must be visible but never installable.
        foreach (var variant in LlamaBackendCatalog.Variants)
        {
            var allPinned = variant.Assets.All(a => !string.IsNullOrWhiteSpace(a.Sha256));
            Assert.Equal(allPinned, variant.IsDownloadable);
        }
    }

    [Fact]
    public void RuntimeKeysAreUniqueAndPathSafe()
    {
        var keys = LlamaBackendCatalog.Variants.Select(v => v.RuntimeKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.Equal(-1, k.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())));
    }
}
