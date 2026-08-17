using OpenClaw.Shared.Inference;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// The nvidia-smi text formats are vendor-controlled and the only source of a
/// trustworthy VRAM number, so they are pinned against captured real output.
/// </summary>
public class NvidiaSmiParserTests
{
    [Fact]
    public void ParseQueryGpu_ReadsNameMemoryAndDriver()
    {
        const string stdout = "NVIDIA RTX 6000 Ada Generation, 49140, 570.86.10\n";

        var gpus = NvidiaSmiParser.ParseQueryGpu(stdout, cudaMajorVersion: 12);

        var gpu = Assert.Single(gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Equal("NVIDIA RTX 6000 Ada Generation", gpu.Name);
        Assert.Equal(49140L * 1024 * 1024, gpu.DedicatedMemoryBytes);
        Assert.Equal("570.86.10", gpu.DriverVersion);
        Assert.Equal(12, gpu.CudaMajorVersion);
    }

    [Fact]
    public void ParseQueryGpu_ReadsEveryGpuOnMultiGpuHosts()
    {
        const string stdout =
            "NVIDIA GeForce RTX 4090, 24564, 566.36\r\n" +
            "NVIDIA GeForce RTX 4090, 24564, 566.36\r\n";

        var gpus = NvidiaSmiParser.ParseQueryGpu(stdout);

        Assert.Equal(2, gpus.Count);
        Assert.All(gpus, g => Assert.Equal(24564L * 1024 * 1024, g.DedicatedMemoryBytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData(null)]
    public void ParseQueryGpu_ReturnsEmptyForNoOutput(string? stdout)
    {
        Assert.Empty(NvidiaSmiParser.ParseQueryGpu(stdout));
    }

    [Fact]
    public void ParseQueryGpu_LeavesMemoryNullWhenDriverReportsNotAvailable()
    {
        // Some virtualized adapters report [N/A]. Null must be distinguishable
        // from zero so the recommender treats it as "unknown", not "no VRAM".
        var gpus = NvidiaSmiParser.ParseQueryGpu("NVIDIA A100-SXM4-40GB, [N/A], 535.104.05");

        Assert.Null(Assert.Single(gpus).DedicatedMemoryBytes);
    }

    [Fact]
    public void ParseQueryGpu_ToleratesAUnitSuffixIfNounitsIsIgnored()
    {
        var gpus = NvidiaSmiParser.ParseQueryGpu("NVIDIA L40S, 46068 MiB, 550.54.15");

        Assert.Equal(46068L * 1024 * 1024, Assert.Single(gpus).DedicatedMemoryBytes);
    }

    [Fact]
    public void TryParseCudaMajorVersion_ReadsTheBannerLine()
    {
        const string banner =
            "Thu Aug 14 09:12:03 2026\n" +
            "+-----------------------------------------------------------------------------+\n" +
            "| NVIDIA-SMI 570.86.10    Driver Version: 570.86.10    CUDA Version: 12.8      |\n";

        Assert.Equal(12, NvidiaSmiParser.TryParseCudaMajorVersion(banner));
    }

    [Fact]
    public void TryParseCudaMajorVersion_HandlesAMajorOnlyVersion()
    {
        Assert.Equal(13, NvidiaSmiParser.TryParseCudaMajorVersion("CUDA Version: 13"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no such marker here")]
    [InlineData("CUDA Version: N/A")]
    public void TryParseCudaMajorVersion_ReturnsNullWhenUnavailable(string? stdout)
    {
        Assert.Null(NvidiaSmiParser.TryParseCudaMajorVersion(stdout));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia)]
    [InlineData("Quadro P2000", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuVendor.Amd)]
    [InlineData("Advanced Micro Devices, Inc. [AMD/ATI]", GpuVendor.Amd)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Display Adapter", GpuVendor.Unknown)]
    [InlineData("", GpuVendor.Unknown)]
    [InlineData(null, GpuVendor.Unknown)]
    public void ClassifyVendor_MapsKnownAdapterNames(string? name, GpuVendor expected)
    {
        Assert.Equal(expected, NvidiaSmiParser.ClassifyVendor(name));
    }
}
