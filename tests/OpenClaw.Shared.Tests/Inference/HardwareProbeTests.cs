using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// The probe's contract is that it never throws and always degrades to
/// "unknown", because a machine we cannot classify has to land on the CPU
/// backend rather than break the settings page.
/// </summary>
public class HardwareProbeTests
{
    private const string Banner =
        "| NVIDIA-SMI 570.86.10    Driver Version: 570.86.10    CUDA Version: 13.0      |";
    private const string QueryOutput = "NVIDIA RTX 6000 Ada Generation, 49140, 570.86.10";

    [Fact]
    public async Task DetectsNvidiaGpuWithVramAndCudaVersion()
    {
        var runner = new ScriptedRunner(argv =>
            argv.Any(a => a.StartsWith("--query-gpu", StringComparison.Ordinal))
                ? Ok(QueryOutput)
                : Ok(Banner));

        var info = await new HardwareProbe(runner, NullLogger.Instance, vulkanLoaderProbe: () => false).GetAsync();

        var gpu = Assert.Single(info.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Equal(49140L * 1024 * 1024, gpu.DedicatedMemoryBytes);
        Assert.Equal(13, info.MaxCudaMajorVersion);
        Assert.True(info.HasNvidiaGpu);
    }

    [Fact]
    public async Task SumsVramAcrossMultipleNvidiaGpus()
    {
        // llama.cpp's default split-mode spreads a model over every visible
        // device, so total VRAM (not the largest single card) is the capacity.
        var runner = new ScriptedRunner(argv =>
            argv.Any(a => a.StartsWith("--query-gpu", StringComparison.Ordinal))
                ? Ok("NVIDIA GeForce RTX 4090, 24564, 566.36\nNVIDIA GeForce RTX 4090, 24564, 566.36")
                : Ok(Banner));

        var info = await new HardwareProbe(runner, NullLogger.Instance, vulkanLoaderProbe: () => false).GetAsync();

        Assert.Equal(2 * 24564L * 1024 * 1024, info.TotalNvidiaVramBytes);
    }

    [Fact]
    public async Task FallsBackToAdapterEnumerationWhenNvidiaSmiIsAbsent()
    {
        var runner = new ScriptedRunner(_ => throw new InvalidOperationException("nvidia-smi not found"));
        IReadOnlyList<GpuInfo> fallback = [new(GpuVendor.Amd, "AMD Radeon RX 7900 XTX")];

        var info = await new HardwareProbe(
            runner,
            NullLogger.Instance,
            fallbackGpuEnumerator: () => fallback,
            vulkanLoaderProbe: () => true).GetAsync();

        Assert.False(info.HasNvidiaGpu);
        Assert.True(info.HasNonNvidiaGpu);
        Assert.True(info.VulkanAvailable);
        Assert.Null(info.TotalNvidiaVramBytes);
    }

    [Fact]
    public async Task ReportsNoGpusWhenEverySourceFails()
    {
        var runner = new ScriptedRunner(_ => throw new InvalidOperationException("boom"));

        var info = await new HardwareProbe(
            runner,
            NullLogger.Instance,
            fallbackGpuEnumerator: () => throw new InvalidOperationException("also boom"),
            vulkanLoaderProbe: () => throw new InvalidOperationException("and boom")).GetAsync();

        Assert.Empty(info.Gpus);
        Assert.False(info.VulkanAvailable);
        Assert.Null(info.MaxCudaMajorVersion);
    }

    [Fact]
    public async Task TreatsANonZeroExitAsNoNvidiaGpu()
    {
        var runner = new ScriptedRunner(_ => new CommandResult { ExitCode = 9, Stdout = "" });

        var info = await new HardwareProbe(runner, NullLogger.Instance, vulkanLoaderProbe: () => false).GetAsync();

        Assert.Empty(info.Gpus);
    }

    [Fact]
    public async Task TreatsATimeoutAsNoNvidiaGpu()
    {
        var runner = new ScriptedRunner(_ => new CommandResult { TimedOut = true, Stdout = QueryOutput });

        var info = await new HardwareProbe(runner, NullLogger.Instance, vulkanLoaderProbe: () => false).GetAsync();

        Assert.Empty(info.Gpus);
    }

    [Fact]
    public async Task CachesTheResultAndRefreshOnDemandReprobes()
    {
        var runner = new ScriptedRunner(argv =>
            argv.Any(a => a.StartsWith("--query-gpu", StringComparison.Ordinal))
                ? Ok(QueryOutput)
                : Ok(Banner));
        var probe = new HardwareProbe(runner, NullLogger.Instance, vulkanLoaderProbe: () => false);

        Assert.Null(probe.Cached);

        await probe.GetAsync();
        var callsAfterFirst = runner.CallCount;
        Assert.NotNull(probe.Cached);

        await probe.GetAsync();
        Assert.Equal(callsAfterFirst, runner.CallCount);

        await probe.RefreshAsync();
        Assert.True(runner.CallCount > callsAfterFirst);
    }

    [Fact]
    public async Task FallsBackToTheSystem32CopyWhenThePathLookupFails()
    {
        // A PATH-less service context is a real deployment shape; the driver
        // installs nvidia-smi.exe into System32 regardless.
        var runner = new ScriptedRunner(argv =>
        {
            if (argv[0] == "nvidia-smi") throw new InvalidOperationException("not on PATH");
            return argv.Any(a => a.StartsWith("--query-gpu", StringComparison.Ordinal)) ? Ok(QueryOutput) : Ok(Banner);
        });

        var info = await new HardwareProbe(runner, NullLogger.Instance, vulkanLoaderProbe: () => false).GetAsync();

        Assert.True(info.HasNvidiaGpu);
        Assert.Contains(runner.Invocations, argv => argv[0].EndsWith("nvidia-smi.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static CommandResult Ok(string stdout) => new() { ExitCode = 0, Stdout = stdout };

    private sealed class ScriptedRunner : ICommandRunner
    {
        private readonly Func<IReadOnlyList<string>, CommandResult> _respond;
        private readonly List<IReadOnlyList<string>> _invocations = [];

        public ScriptedRunner(Func<IReadOnlyList<string>, CommandResult> respond) => _respond = respond;

        public string Name => "scripted";
        public int CallCount => _invocations.Count;
        public IReadOnlyList<IReadOnlyList<string>> Invocations => _invocations;

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            var argv = request.Argv ?? throw new InvalidOperationException("Probe must use direct argv.");
            _invocations.Add(argv);
            return Task.FromResult(_respond(argv));
        }
    }
}
