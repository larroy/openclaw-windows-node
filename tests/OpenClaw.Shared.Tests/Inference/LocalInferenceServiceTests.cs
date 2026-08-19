using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

public class LocalInferenceServiceTests
{
    [Theory]
    [InlineData("Cuda12", LlamaBackend.Cuda12)]
    [InlineData("cuda13", LlamaBackend.Cuda13)]
    [InlineData("  Vulkan  ", LlamaBackend.Vulkan)]
    [InlineData("Cpu", LlamaBackend.Cpu)]
    public void ParsesAKnownBackendOverride(string value, LlamaBackend expected)
    {
        Assert.Equal(expected, LocalInferenceService.ParseBackendOverride(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Rocm")]
    [InlineData("nonsense")]
    public void TreatsAnUnknownBackendOverrideAsAutomatic(string? value)
    {
        // A hand-edited settings file must degrade to automatic selection rather
        // than breaking the page.
        Assert.Null(LocalInferenceService.ParseBackendOverride(value));
    }

    [Fact]
    public async Task SnapshotReportsHardwareBackendAndRecommendation()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsData();
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var snapshot = await service.GetSnapshotAsync();

        Assert.NotNull(snapshot.Hardware);
        Assert.True(snapshot.Hardware!.HasNvidiaGpu);

        // Architecture-dependent on purpose: this feature's dev host is ARM64
        // with an NVIDIA GPU, where the pinned release ships only a CUDA 13
        // build, so asserting a specific CUDA major here would fail on one of
        // the two architectures we support.
        var preferred = snapshot.BackendPlan!.Preferred!;
        Assert.Contains(preferred.Backend, new[] { LlamaBackend.Cuda12, LlamaBackend.Cuda13 });
        Assert.Equal(snapshot.Hardware.CpuArchitecture, preferred.Architecture);

        Assert.False(snapshot.RuntimeInstalled);
        Assert.False(snapshot.UsingCustomRuntime);
        Assert.Equal(LlamaServerState.Stopped, snapshot.ServerStatus.State);
    }

    [Fact]
    public async Task AnExplicitModelChoiceOverridesTheRecommendation()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsData { LocalInferenceModelId = LocalModelCatalog.DeepSeekV4FlashId };
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(LocalModelCatalog.DeepSeekV4FlashId, snapshot.SelectedModel!.Id);
    }

    [Fact]
    public async Task ABackendOverrideIsHonoredInTheSnapshot()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsData { LocalInferenceBackendOverride = "Cpu" };
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(LlamaBackend.Cpu, snapshot.BackendPlan!.Preferred!.Backend);
    }

    [Fact]
    public async Task AConfiguredCustomRuntimeIsReportedInTheSnapshot()
    {
        // The page relies on this to show the "not integrity verified" notice.
        using var temp = new TempDirectory();
        var settings = new SettingsData { LocalInferenceCustomRuntimePath = temp.Combine("build") };
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var snapshot = await service.GetSnapshotAsync();

        Assert.True(snapshot.UsingCustomRuntime);
        Assert.True(snapshot.RuntimeInstalled);
    }

    [Fact]
    public async Task StartRefusesWhenTheModelIsNotDownloaded()
    {
        // Start must not silently kick off a multi-hour download.
        using var temp = new TempDirectory();
        var settings = new SettingsData();
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());

        Assert.Contains("not downloaded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRefusesAnUnpublishedCheckpoint()
    {
        // Deterministic regardless of host memory: this checkpoint has no shards
        // at all, so there is nothing on disk to launch against.
        using var temp = new TempDirectory();
        var settings = new SettingsData { LocalInferenceModelId = LocalModelCatalog.Qwen27BId };
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());

        Assert.Contains("not downloaded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureRuntimeResolvesTheCustomBuildWithoutDownloading()
    {
        using var temp = new TempDirectory();
        var buildDir = temp.Combine("mybuild");
        System.IO.Directory.CreateDirectory(buildDir);
        var exe = System.IO.Path.Combine(buildDir, LlamaBackendCatalog.ServerExecutableName);
        await System.IO.File.WriteAllTextAsync(exe, "custom");

        var settings = new SettingsData { LocalInferenceCustomRuntimePath = buildDir };
        var transport = new FakeHttpTransport();
        await using var service = NewService(temp, () => settings, NvidiaHost(), transport);

        var runtime = await service.EnsureRuntimeAsync();

        Assert.Equal(LlamaRuntimeSource.CustomBuild, runtime.Source);
        Assert.True(runtime.IsUnverified);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ResolveSelectedModelFallsBackToTheRecommendation()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsData();
        await using var service = NewService(temp, () => settings, NvidiaHost());
        var snapshot = await service.GetSnapshotAsync();

        var resolved = service.ResolveSelectedModel(settings, snapshot.Recommendation);

        Assert.Equal(snapshot.Recommendation!.Recommended!.Id, resolved!.Id);
    }

    [Fact]
    public async Task ServerStatusChangesAreForwardedToSubscribers()
    {
        // The page renders from this event rather than polling.
        using var temp = new TempDirectory();
        var settings = new SettingsData();
        await using var service = NewService(temp, () => settings, NvidiaHost());

        var seen = new List<LlamaServerState>();
        service.ServerStatusChanged += (_, s) => seen.Add(s.State);

        await service.StopAsync();

        Assert.Contains(LlamaServerState.Stopped, seen);
    }

    private static LocalInferenceService NewService(
        TempDirectory temp,
        Func<SettingsData> settings,
        Func<IReadOnlyList<string>, CommandResult> nvidiaSmi,
        FakeHttpTransport? transport = null)
    {
        transport ??= new FakeHttpTransport();
        var logger = NullLogger.Instance;
        var downloader = new VerifiedFileDownloader(logger, transport.ClientFactory);

        return new LocalInferenceService(
            new HardwareProbe(new ScriptedRunner(nvidiaSmi), logger, vulkanLoaderProbe: () => false),
            new LlamaRuntimeManager(temp.Path, logger, downloader),
            new GgufModelManager(temp.Path, logger, downloader, freeSpaceProbe: _ => 512L * 1024 * 1024 * 1024),
            new LlamaServerProcess(logger),
            settings,
            logger);
    }

    /// <summary>A workstation with a large NVIDIA GPU and plenty of RAM.</summary>
    private static Func<IReadOnlyList<string>, CommandResult> NvidiaHost() => argv =>
        argv.Any(a => a.StartsWith("--query-gpu", StringComparison.Ordinal))
            ? new CommandResult { ExitCode = 0, Stdout = "NVIDIA RTX 6000 Ada Generation, 49140, 570.86.10" }
            : new CommandResult { ExitCode = 0, Stdout = "CUDA Version: 12.8" };

    /// <summary>A machine with no GPU, where nothing in the catalog fits.</summary>
    private static Func<IReadOnlyList<string>, CommandResult> TinyHost() =>
        _ => new CommandResult { ExitCode = 1, Stdout = "" };

    private sealed class ScriptedRunner(Func<IReadOnlyList<string>, CommandResult> respond) : ICommandRunner
    {
        public string Name => "scripted";

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default) =>
            Task.FromResult(respond(request.Argv ?? []));
    }
}
