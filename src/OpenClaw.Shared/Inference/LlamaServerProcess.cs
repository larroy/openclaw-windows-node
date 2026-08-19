using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Inference;

/// <summary>Lifecycle state of the local inference server.</summary>
public enum LlamaServerState
{
    Stopped = 0,
    /// <summary>Process launched; waiting for the health endpoint to answer.</summary>
    Starting = 1,
    /// <summary>Health endpoint is answering; the server can take requests.</summary>
    Ready = 2,
    /// <summary>Startup or the process itself failed. <see cref="LlamaServerStatus.Detail"/> says why.</summary>
    Failed = 3,
}

/// <summary>
/// Observable status of the server.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Port">Port it is listening on, or null when stopped.</param>
/// <param name="Detail">
/// PII-free explanation for the current state. On failure this carries the tail
/// of the server's stderr, which is the only place a bad recipe flag or a CUDA
/// initialization failure is explained.
/// </param>
public sealed record LlamaServerStatus(
    LlamaServerState State,
    int? Port = null,
    string? Detail = null)
{
    public static LlamaServerStatus Stopped { get; } = new(LlamaServerState.Stopped);

    /// <summary>Base URL for a client on this machine, or null when not ready.</summary>
    public string? LoopbackBaseUrl => State == LlamaServerState.Ready && Port is { } port
        ? LlamaServerArguments.BuildBaseUrl(LlamaServerArguments.LoopbackHost, port)
        : null;
}

/// <summary>
/// Launches and supervises a single <c>llama-server</c> process.
///
/// <para>One instance owns at most one running server. Starting while a server is
/// already running stops the old one first: two servers with the same weights
/// would each claim the GPU and the second would fail on allocation.</para>
///
/// <para>The child is placed in a <see cref="ProcessJobObject"/> so it cannot
/// outlive the app even on an abrupt termination, and its stderr is tailed so a
/// startup failure can be explained rather than reported as a bare timeout.</para>
/// </summary>
public sealed class LlamaServerProcess : IAsyncDisposable
{
    /// <summary>
    /// Lines of stderr retained for diagnostics. Enough to carry a CUDA error and
    /// its context; small enough that a chatty server cannot grow memory.
    /// </summary>
    private const int StderrTailLines = 40;

    /// <summary>Grace period for a polite shutdown before the process is killed.</summary>
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(10);

    private readonly IOpenClawLogger _logger;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Process? _process;
    private ProcessJobObject? _job;
    private ConcurrentQueue<string> _stderrTail = new();
    private LlamaServerStatus _status = LlamaServerStatus.Stopped;

    /// <summary>Set while a deliberate stop is in progress, so the resulting child exit is not reported as a crash.</summary>
    private volatile bool _stopRequested;

    public LlamaServerProcess(IOpenClawLogger logger, Func<HttpClient>? httpClientFactory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? (static () => new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
    }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<LlamaServerStatus>? StatusChanged;

    /// <summary>Current status. Safe to read from any thread.</summary>
    public LlamaServerStatus Status => _status;

    /// <summary>True when a child process is currently alive.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Start a server for the given runtime and model, waiting until its health
    /// endpoint answers.
    /// </summary>
    /// <param name="runtime">Resolved runtime from <see cref="LlamaRuntimeManager"/>.</param>
    /// <param name="modelPath">Checkpoint path, or the first shard of a sharded model.</param>
    /// <param name="recipeArgs">The checkpoint's tuned arguments.</param>
    /// <param name="port">Port to listen on, or null to allocate a free one.</param>
    /// <param name="bindBeyondLoopback">
    /// Bind all interfaces instead of loopback. Exposes an unauthenticated
    /// endpoint to the network, so the caller must have obtained explicit consent.
    /// </param>
    /// <param name="readyTimeout">
    /// How long to wait for the health endpoint. Loading tens of gigabytes of
    /// weights from a cold page cache genuinely takes minutes, so this is
    /// generous by default.
    /// </param>
    public async Task<LlamaServerStatus> StartAsync(
        LlamaRuntime runtime,
        string modelPath,
        IReadOnlyList<string> recipeArgs,
        int? port = null,
        bool bindBeyondLoopback = false,
        TimeSpan? readyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            if (!File.Exists(runtime.ServerExecutablePath))
                return Fail($"Server executable not found at '{runtime.ServerExecutablePath}'.");

            if (!File.Exists(modelPath))
                return Fail("The selected model is not downloaded.");

            var resolvedPort = port ?? AllocateFreePort();
            var argv = LlamaServerArguments.Build(modelPath, resolvedPort, recipeArgs, bindBeyondLoopback);

            if (bindBeyondLoopback)
            {
                _logger.Warn(
                    "[Inference] Binding the local inference server beyond loopback. " +
                    "The endpoint is unauthenticated and reachable from the network.");
            }

            _stopRequested = false;
            SetStatus(new LlamaServerStatus(LlamaServerState.Starting, resolvedPort));

            if (!TryLaunch(runtime, argv, out var launchError))
                return Fail(launchError!);

            var ready = await WaitForHealthAsync(
                resolvedPort,
                readyTimeout ?? TimeSpan.FromMinutes(10),
                cancellationToken).ConfigureAwait(false);

            if (!ready)
            {
                var detail = _process is { HasExited: true }
                    ? $"The server exited during startup. {DescribeStderrTail()}"
                    : $"The server did not become ready in time. {DescribeStderrTail()}";
                await StopCoreAsync().ConfigureAwait(false);
                return Fail(detail);
            }

            _logger.Info($"[Inference] Server ready on port {resolvedPort}");
            SetStatus(new LlamaServerStatus(LlamaServerState.Ready, resolvedPort));
            return _status;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Stop the running server, if any. Safe to call when stopped.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(LlamaServerStatus.Stopped);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private bool TryLaunch(LlamaRuntime runtime, IReadOnlyList<string> argv, out string? error)
    {
        error = null;
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.ServerExecutablePath,
            // The runtime directory holds the native dependencies (ggml, CUDA),
            // so the process has to start there for the loader to find them.
            WorkingDirectory = runtime.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var arg in argv) startInfo.ArgumentList.Add(arg);

        // The command line embeds the model and runtime paths, which contain the
        // user's profile directory. Log the shape, not the values.
        _logger.Info($"[Inference] Launching llama-server with {argv.Count} arguments");

        _stderrTail = new ConcurrentQueue<string>();

        try
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.ErrorDataReceived += OnStderr;
            process.OutputDataReceived += OnStdout;
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                error = "The server process could not be started.";
                return false;
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _process = process;

            AttachJobObject(process);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to launch the server process: {ex.Message}";
            _logger.Error("[Inference] llama-server launch failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Put the child in a kill-on-close job. A failure here is logged and
    /// tolerated: losing the crash-cleanup guarantee is worse than nothing, but
    /// refusing to run at all would be worse still.
    /// </summary>
    private void AttachJobObject(Process process)
    {
        try
        {
            _job = new ProcessJobObject();
            if (!_job.TryAssign(process.Handle))
            {
                _logger.Warn("[Inference] Could not assign llama-server to a job object; " +
                             "it may survive an abrupt shutdown of this app.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Inference] Job object unavailable: {ex.Message}. " +
                         "llama-server may survive an abrupt shutdown of this app.");
            _job = null;
        }
    }

    private async Task<bool> WaitForHealthAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var url = LlamaServerArguments.BuildHealthUrl(port);
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var httpClient = _httpClientFactory();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A process that already exited will never become healthy; failing
            // fast here is what turns a ten-minute timeout into a prompt,
            // explainable error.
            if (_process is { HasExited: true }) return false;

            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Connection refused while the server is still loading weights is
                // the expected case, not an error worth logging on every poll.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        _process = null;

        if (process is not null)
        {
            // Detach the exit handler BEFORE killing. Otherwise a deliberate stop
            // raises Exited while the status is still Ready and gets reported as
            // "the server stopped unexpectedly", so the UI flashes a failure on
            // every normal Stop. Observed on a real run before this was fixed.
            process.Exited -= OnProcessExited;
            _stopRequested = true;

            try
            {
                if (!process.HasExited)
                {
                    _logger.Info("[Inference] Stopping llama-server");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(new CancellationTokenSource(GracefulStopTimeout).Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Inference] Could not stop llama-server cleanly: {ex.Message}");
            }
            finally
            {
                process.ErrorDataReceived -= OnStderr;
                process.OutputDataReceived -= OnStdout;
                process.Dispose();
            }
        }

        // Disposing the job terminates anything still in it, which covers a child
        // that ignored the kill or spawned its own descendants.
        _job?.Dispose();
        _job = null;
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        _stderrTail.Enqueue(e.Data);
        while (_stderrTail.Count > StderrTailLines) _stderrTail.TryDequeue(out _);
    }

    private void OnStdout(object sender, DataReceivedEventArgs e)
    {
        // Drained so the pipe buffer cannot fill and block the child, but not
        // retained: llama-server's stdout is request logging, which would put
        // prompt content into our diagnostics.
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (!ShouldReportUnexpectedExit(_status.State, _stopRequested)) return;

        SetStatus(new LlamaServerStatus(
            LlamaServerState.Failed,
            _status.Port,
            $"The server stopped unexpectedly. {DescribeStderrTail()}"));
    }

    /// <summary>
    /// Whether a child exit should be surfaced as a crash.
    /// </summary>
    /// <remarks>
    /// Only an exit we did not ask for, while we believed the server was serving,
    /// is a crash. An exit during a deliberate stop is the expected outcome, and
    /// reporting it as a failure makes every normal Stop look like an error.
    /// </remarks>
    internal static bool ShouldReportUnexpectedExit(LlamaServerState state, bool stopRequested) =>
        !stopRequested && state is LlamaServerState.Ready;

    private string DescribeStderrTail()
    {
        var lines = _stderrTail.ToArray();
        return lines.Length == 0
            ? "No error output was captured."
            : "Last output: " + string.Join(" | ", lines.TakeLast(5));
    }

    private LlamaServerStatus Fail(string detail)
    {
        _logger.Warn($"[Inference] {detail}");
        SetStatus(new LlamaServerStatus(LlamaServerState.Failed, _status.Port, detail));
        return _status;
    }

    private void SetStatus(LlamaServerStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// Ask the OS for an unused loopback port. Inherently racy, so a launch that
    /// still hits a conflict fails with the server's own bind error rather than
    /// this method pretending to guarantee availability.
    /// </summary>
    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }
}
