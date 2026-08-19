using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// Covers the guard paths that must not require a real llama-server: bad inputs,
/// and a child that exits during startup. Readiness against a genuine server is
/// manual proof (see docs/LOCAL_INFERENCE_PLAN.md); what is pinned here is that
/// every failure is prompt and explained rather than a silent ten-minute hang.
/// </summary>
public class LlamaServerProcessTests
{
    [Fact]
    public void StartsOutStopped()
    {
        using var temp = new TempDirectory();
        var server = new LlamaServerProcess(NullLogger.Instance);

        Assert.Equal(LlamaServerState.Stopped, server.Status.State);
        Assert.False(server.IsRunning);
        Assert.Null(server.Status.LoopbackBaseUrl);
    }

    [Fact]
    public async Task FailsWhenTheServerExecutableIsMissing()
    {
        using var temp = new TempDirectory();
        var modelPath = temp.Combine("model.gguf");
        await File.WriteAllTextAsync(modelPath, "weights");

        await using var server = new LlamaServerProcess(NullLogger.Instance);
        var runtime = new LlamaRuntime(temp.Combine("does-not-exist.exe"), LlamaRuntimeSource.Catalog, null);

        var status = await server.StartAsync(runtime, modelPath, []);

        Assert.Equal(LlamaServerState.Failed, status.State);
        Assert.Contains("not found", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailsWhenTheModelIsNotDownloaded()
    {
        using var temp = new TempDirectory();
        var exe = temp.Combine("llama-server.exe");
        await File.WriteAllTextAsync(exe, "stub");

        await using var server = new LlamaServerProcess(NullLogger.Instance);
        var runtime = new LlamaRuntime(exe, LlamaRuntimeSource.Catalog, null);

        var status = await server.StartAsync(runtime, temp.Combine("missing.gguf"), []);

        Assert.Equal(LlamaServerState.Failed, status.State);
        Assert.Contains("not downloaded", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsPromptlyWhenTheChildExitsDuringStartup()
    {
        // A recipe flag an older build rejects makes llama-server exit at once.
        // The health poll must notice the exit instead of waiting out the full
        // ready timeout, and the failure must carry the captured output.
        var stub = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.True(File.Exists(stub), "This test needs ping.exe from System32 as a fast-exiting stub.");

        using var temp = new TempDirectory();
        var modelPath = temp.Combine("model.gguf");
        await File.WriteAllTextAsync(modelPath, "weights");

        await using var server = new LlamaServerProcess(NullLogger.Instance);
        var runtime = new LlamaRuntime(stub, LlamaRuntimeSource.Catalog, null);

        var stopwatch = Stopwatch.StartNew();
        var status = await server.StartAsync(
            runtime, modelPath, [], readyTimeout: TimeSpan.FromMinutes(5));
        stopwatch.Stop();

        Assert.Equal(LlamaServerState.Failed, status.State);
        Assert.Contains("exited", status.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Startup failure took {stopwatch.Elapsed}, so the exited-child check did not short-circuit the poll.");
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task RaisesStatusChangedForEachTransition()
    {
        using var temp = new TempDirectory();
        var modelPath = temp.Combine("model.gguf");
        await File.WriteAllTextAsync(modelPath, "weights");

        await using var server = new LlamaServerProcess(NullLogger.Instance);
        var states = new System.Collections.Generic.List<LlamaServerState>();
        server.StatusChanged += (_, s) => states.Add(s.State);

        await server.StartAsync(
            new LlamaRuntime(temp.Combine("missing.exe"), LlamaRuntimeSource.Catalog, null),
            modelPath,
            []);

        Assert.Contains(LlamaServerState.Failed, states);
    }

    [Fact]
    public async Task StopIsSafeWhenNothingIsRunning()
    {
        await using var server = new LlamaServerProcess(NullLogger.Instance);

        await server.StopAsync();
        await server.StopAsync();

        Assert.Equal(LlamaServerState.Stopped, server.Status.State);
    }

    [Fact]
    public void LoopbackBaseUrlIsOnlyExposedWhenReady()
    {
        Assert.Null(new LlamaServerStatus(LlamaServerState.Starting, 8080).LoopbackBaseUrl);
        Assert.Null(new LlamaServerStatus(LlamaServerState.Failed, 8080).LoopbackBaseUrl);
        Assert.Equal(
            "http://127.0.0.1:8080/v1",
            new LlamaServerStatus(LlamaServerState.Ready, 8080).LoopbackBaseUrl);
    }

    [Theory]
    // A crash while serving is the only case worth reporting as a failure.
    [InlineData(LlamaServerState.Ready, false, true)]
    // A deliberate stop kills the child; reporting that as "stopped unexpectedly"
    // made the UI flash a failure on every normal Stop. Observed on a real run.
    [InlineData(LlamaServerState.Ready, true, false)]
    [InlineData(LlamaServerState.Starting, false, false)]
    [InlineData(LlamaServerState.Stopped, false, false)]
    [InlineData(LlamaServerState.Failed, false, false)]
    public void ShouldReportUnexpectedExit_OnlyForAnUnrequestedExitWhileServing(
        LlamaServerState state, bool stopRequested, bool expected)
    {
        Assert.Equal(expected, LlamaServerProcess.ShouldReportUnexpectedExit(state, stopRequested));
    }

    [Fact]
    public void JobObjectIsCreatableOnThisHost()
    {
        // If this ever fails, llama-server can outlive an abrupt app shutdown
        // while holding tens of gigabytes of VRAM.
        using var job = new ProcessJobObject();

        Assert.True(job.IsValid);
    }

    [Fact]
    public async Task JobObjectTerminatesAnAssignedProcessWhenDisposed()
    {
        var stub = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.True(File.Exists(stub), "This test needs ping.exe from System32.");

        // -t pings forever, so the process only ends because the job ends.
        using var process = Process.Start(new ProcessStartInfo(stub)
        {
            ArgumentList = { "-t", "127.0.0.1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        try
        {
            var job = new ProcessJobObject();
            Assert.True(job.TryAssign(process.Handle));

            job.Dispose();

            await process.WaitForExitAsync(new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not mask the assertion above.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
