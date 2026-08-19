using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Everything the local-inference settings page needs to render.
/// </summary>
/// <param name="Hardware">Detected hardware, or null before the first probe.</param>
/// <param name="BackendPlan">Selected backend plan for that hardware.</param>
/// <param name="Recommendation">Model fit assessment for that hardware.</param>
/// <param name="SelectedModel">Model the user chose, or the recommended one.</param>
/// <param name="ModelState">On-disk state of the selected model.</param>
/// <param name="RuntimeInstalled">Whether the selected backend is installed.</param>
/// <param name="UsingCustomRuntime">Whether a user-supplied build is configured.</param>
/// <param name="ServerStatus">Current server lifecycle status.</param>
public sealed record LocalInferenceSnapshot(
    HostHardwareInfo? Hardware,
    BackendPlan? BackendPlan,
    LocalModelRecommendation? Recommendation,
    LocalModelInfo? SelectedModel,
    LocalModelDownloadState? ModelState,
    bool RuntimeInstalled,
    bool UsingCustomRuntime,
    LlamaServerStatus ServerStatus);

/// <summary>
/// Orchestrates the local inference pipeline: probe hardware, select a backend,
/// resolve a runtime and model, and run the server.
///
/// <para>This is the seam the UI talks to. It holds no WinUI types so the whole
/// flow can be exercised in tests, and it deliberately owns no download or
/// process logic of its own beyond sequencing the components that do.</para>
/// </summary>
public sealed class LocalInferenceService : IAsyncDisposable
{
    private readonly HardwareProbe _hardwareProbe;
    private readonly LlamaRuntimeManager _runtimeManager;
    private readonly GgufModelManager _modelManager;
    private readonly LlamaServerProcess _server;
    private readonly IOpenClawLogger _logger;
    private readonly Func<SettingsData> _settingsAccessor;

    public LocalInferenceService(
        HardwareProbe hardwareProbe,
        LlamaRuntimeManager runtimeManager,
        GgufModelManager modelManager,
        LlamaServerProcess server,
        Func<SettingsData> settingsAccessor,
        IOpenClawLogger logger)
    {
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _settingsAccessor = settingsAccessor ?? throw new ArgumentNullException(nameof(settingsAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _server.StatusChanged += (_, status) => ServerStatusChanged?.Invoke(this, status);
    }

    /// <summary>Raised when the server's lifecycle status changes.</summary>
    public event EventHandler<LlamaServerStatus>? ServerStatusChanged;

    /// <summary>Current server status.</summary>
    public LlamaServerStatus ServerStatus => _server.Status;

    /// <summary>
    /// Build a full snapshot for the UI, probing hardware on first use.
    /// </summary>
    /// <param name="refreshHardware">Re-run detection instead of using the cache.</param>
    public async Task<LocalInferenceSnapshot> GetSnapshotAsync(
        bool refreshHardware = false,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsAccessor();

        var hardware = refreshHardware
            ? await _hardwareProbe.RefreshAsync(cancellationToken).ConfigureAwait(false)
            : await _hardwareProbe.GetAsync(cancellationToken).ConfigureAwait(false);

        var plan = BackendSelector.Select(hardware, ParseBackendOverride(settings.LocalInferenceBackendOverride));
        var recommendation = ModelRecommender.Recommend(hardware);
        var model = ResolveSelectedModel(settings, recommendation);

        var usingCustom = !string.IsNullOrWhiteSpace(settings.LocalInferenceCustomRuntimePath);
        var runtimeInstalled = usingCustom
            || (plan.Preferred is { } variant && _runtimeManager.IsInstalled(variant));

        return new LocalInferenceSnapshot(
            hardware,
            plan,
            recommendation,
            model,
            model is null ? null : _modelManager.GetState(model),
            runtimeInstalled,
            usingCustom,
            _server.Status);
    }

    /// <summary>
    /// The model the user selected, falling back to the recommendation. Returns
    /// null when neither is available, which the UI renders as "nothing to run".
    /// </summary>
    public LocalModelInfo? ResolveSelectedModel(SettingsData settings, LocalModelRecommendation? recommendation) =>
        LocalModelCatalog.Find(settings.LocalInferenceModelId) ?? recommendation?.Recommended;

    /// <summary>
    /// Install the backend runtime for the current hardware, unless a custom
    /// build is configured.
    /// </summary>
    public async Task<LlamaRuntime> EnsureRuntimeAsync(
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsAccessor();

        if (!string.IsNullOrWhiteSpace(settings.LocalInferenceCustomRuntimePath))
            return _runtimeManager.ResolveCustomBuild(settings.LocalInferenceCustomRuntimePath!);

        var hardware = await _hardwareProbe.GetAsync(cancellationToken).ConfigureAwait(false);
        var plan = BackendSelector.Select(hardware, ParseBackendOverride(settings.LocalInferenceBackendOverride));

        if (plan.Preferred is null)
            throw new InvalidOperationException($"No llama.cpp build is available for this host. {plan.Reason}");

        return await _runtimeManager
            .EnsureInstalledAsync(plan.Preferred, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Download the selected model's weights.</summary>
    public async Task EnsureModelAsync(
        LocalModelInfo model,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _modelManager.DownloadAsync(model, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Start the server for the selected model, installing the runtime first if
    /// needed. The model must already be downloaded: starting a multi-hour
    /// download from a Start button would be a surprising amount of work to
    /// trigger by accident.
    /// </summary>
    public async Task<LlamaServerStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsAccessor();
        var hardware = await _hardwareProbe.GetAsync(cancellationToken).ConfigureAwait(false);
        var model = ResolveSelectedModel(settings, ModelRecommender.Recommend(hardware));

        if (model is null)
            throw new InvalidOperationException("No local model is selected and none is recommended for this host.");

        if (!_modelManager.IsDownloaded(model))
            throw new InvalidOperationException($"Model '{model.DisplayName}' is not downloaded yet.");

        var modelPath = _modelManager.GetPrimaryShardPath(model)
            ?? throw new InvalidOperationException($"Model '{model.DisplayName}' has no checkpoint file.");

        var runtime = await EnsureRuntimeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return await _server.StartAsync(
            runtime,
            modelPath,
            model.RecipeArgs,
            settings.LocalInferencePort,
            settings.LocalInferenceBindBeyondLoopback,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stop the server if it is running.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _server.StopAsync(cancellationToken);

    /// <summary>
    /// Parse the persisted backend override. An unrecognized value is treated as
    /// "no override" rather than an error: a settings file edited by hand should
    /// degrade to automatic selection, not break the page.
    /// </summary>
    public static LlamaBackend? ParseBackendOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<LlamaBackend>(value.Trim(), ignoreCase: true, out var parsed) ? parsed : null;
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync().ConfigureAwait(false);
}
