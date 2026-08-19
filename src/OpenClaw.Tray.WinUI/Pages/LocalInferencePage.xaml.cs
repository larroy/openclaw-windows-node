using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// Settings surface for running a model locally with llama.cpp: detected
/// hardware, model download, server lifecycle, and the advanced overrides.
///
/// <para>All orchestration lives in <see cref="LocalInferenceService"/>; this page
/// only projects a snapshot onto controls and forwards user intent.</para>
/// </summary>
public sealed partial class LocalInferencePage : Page
{
    /// <summary>
    /// Minimum gap between progress repaints. The downloader streams in 80 KB
    /// chunks, so a 22 GB model produces roughly 280,000 callbacks. Repainting
    /// on each one saturates the dispatcher and the window stops responding.
    /// </summary>
    private static readonly TimeSpan ProgressRepaintInterval = TimeSpan.FromMilliseconds(150);

    private static App CurrentApp => (App)Application.Current!;
    private static string L(string key) => LocalizationHelper.GetString(key);
    private static string Lf(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationHelper.GetString(key), args);

    private LocalInferenceService? _service;
    private LocalInferenceSnapshot? _snapshot;
    private CancellationTokenSource? _downloadCts;
    private bool _suppressEvents = true;
    private bool _serverBusy;
    private Action? _pendingConfirmation;

    public LocalInferencePage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_service is not null) _service.ServerStatusChanged -= OnServerStatusChanged;
            _downloadCts?.Cancel();
        };
    }

    public void Initialize(LocalInferenceService? service)
    {
        if (_service is not null) _service.ServerStatusChanged -= OnServerStatusChanged;

        _service = service;
        if (_service is not null) _service.ServerStatusChanged += OnServerStatusChanged;

        PopulateStaticChoices();
        LoadSettings();
        RefreshSnapshot(refreshHardware: false);
    }

    // ─── Population ───

    private void PopulateStaticChoices()
    {
        _suppressEvents = true;
        try
        {
            ModelCombo.Items.Clear();
            foreach (var model in LocalModelCatalog.Models)
            {
                ModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = model.IsDownloadable
                        ? $"{model.DisplayName} ({FormatBytes(model.TotalSizeBytes)})"
                        : Lf("LocalInferencePage_ModelUnavailableSuffix", model.DisplayName),
                    Tag = model.Id,
                    IsEnabled = model.IsDownloadable,
                });
            }

            BackendCombo.Items.Clear();
            BackendCombo.Items.Add(new ComboBoxItem
            {
                Content = L("LocalInferencePage_BackendAutomatic"),
                Tag = string.Empty,
            });
            foreach (var backend in Enum.GetValues<LlamaBackend>())
            {
                BackendCombo.Items.Add(new ComboBoxItem
                {
                    Content = backend.ToString(),
                    Tag = backend.ToString(),
                });
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void LoadSettings()
    {
        var settings = CurrentApp.Settings;
        _suppressEvents = true;
        try
        {
            EnabledToggle.IsOn = settings.LocalInferenceEnabled;
            AutoStartCheck.IsChecked = settings.LocalInferenceAutoStart;
            RegisterCheck.IsChecked = settings.LocalInferenceRegisterWithGateway;
            BindBeyondLoopbackCheck.IsChecked = settings.LocalInferenceBindBeyondLoopback;
            CustomRuntimeBox.Text = settings.LocalInferenceCustomRuntimePath ?? string.Empty;

            SelectByTag(ModelCombo, settings.LocalInferenceModelId);
            SelectByTag(BackendCombo, settings.LocalInferenceBackendOverride ?? string.Empty);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static void SelectByTag(ComboBox combo, string? tag)
    {
        var match = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = match;
    }

    // ─── Snapshot ───

    private void RefreshSnapshot(bool refreshHardware) =>
        AsyncEventHandlerGuard.Run(
            () => RefreshSnapshotAsync(refreshHardware),
            new AppLogger(),
            nameof(RefreshSnapshot));

    private async Task RefreshSnapshotAsync(bool refreshHardware)
    {
        if (_service is null)
        {
            HardwareSummaryText.Text = L("LocalInferencePage_HardwareUnavailable");
            return;
        }

        if (refreshHardware)
        {
            RedetectButton.IsEnabled = false;
            HardwareSummaryText.Text = L("LocalInferencePage_HardwareDetecting");
        }

        try
        {
            _snapshot = await _service.GetSnapshotAsync(refreshHardware);
            ApplySnapshot(_snapshot);
        }
        catch (Exception ex)
        {
            // Privacy: the exception can carry paths and URLs. Log it, show a
            // neutral message.
            Logger.Error($"[LocalInferencePage] Snapshot failed: {ex}");
            HardwareSummaryText.Text = L("LocalInferencePage_HardwareUnavailable");
        }
        finally
        {
            RedetectButton.IsEnabled = true;
        }
    }

    private void ApplySnapshot(LocalInferenceSnapshot snapshot)
    {
        HardwareSummaryText.Text = DescribeHardware(snapshot.Hardware);
        BackendSummaryText.Text = snapshot.BackendPlan?.Reason ?? string.Empty;

        CustomRuntimeNotice.Visibility = snapshot.UsingCustomRuntime ? Visibility.Visible : Visibility.Collapsed;

        var model = snapshot.SelectedModel;
        if (model is null)
        {
            ModelFitText.Text = snapshot.Recommendation?.Summary ?? string.Empty;
            ModelStatusText.Text = string.Empty;
            LargeDownloadNotice.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (ModelCombo.SelectedItem is null) SelectByTag(ModelCombo, model.Id);

            var assessment = snapshot.Recommendation?.Assessments
                .FirstOrDefault(a => a.Model.Id == model.Id);
            ModelFitText.Text = assessment?.Reason ?? string.Empty;

            // A download measured in hundreds of gigabytes must never start
            // without the size stated first.
            LargeDownloadNotice.Visibility = model.RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
            LargeDownloadNoticeText.Text = Lf(
                "LocalInferencePage_LargeDownloadNotice",
                FormatBytes(model.TotalSizeBytes));

            ModelStatusText.Text = snapshot.ModelState is { IsComplete: true }
                ? L("LocalInferencePage_ModelReady")
                : snapshot.ModelState is { ShardsPresent: > 0 } partial
                    ? Lf("LocalInferencePage_ModelPartial", partial.ShardsPresent, partial.ShardCount)
                    : L("LocalInferencePage_ModelNotDownloaded");
        }

        UpdateActionStates(snapshot);
        ApplyServerStatus(snapshot.ServerStatus);
    }

    private void UpdateActionStates(LocalInferenceSnapshot snapshot)
    {
        var downloading = _downloadCts is not null;
        var model = snapshot.SelectedModel;
        var modelReady = snapshot.ModelState is { IsComplete: true };

        DownloadButton.IsEnabled = !downloading && model is { IsDownloadable: true };
        DeleteModelButton.IsEnabled = !downloading && snapshot.ModelState is { ShardsPresent: > 0 };
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;

        var running = snapshot.ServerStatus.State is LlamaServerState.Ready or LlamaServerState.Starting;
        StartButton.IsEnabled = !_serverBusy && !running && modelReady && EnabledToggle.IsOn;
        StopButton.IsEnabled = !_serverBusy && running;
    }

    private void ApplyServerStatus(LlamaServerStatus status)
    {
        ServerStatusText.Text = status.State switch
        {
            LlamaServerState.Ready => Lf("LocalInferencePage_ServerReady", status.Port),
            LlamaServerState.Starting => L("LocalInferencePage_ServerStarting"),
            LlamaServerState.Failed => L("LocalInferencePage_ServerFailed"),
            _ => L("LocalInferencePage_ServerStopped"),
        };

        ServerBusyRing.IsActive = status.State == LlamaServerState.Starting || _serverBusy;

        var endpoint = status.LoopbackBaseUrl;
        EndpointPanel.Visibility = endpoint is null ? Visibility.Collapsed : Visibility.Visible;
        EndpointText.Text = endpoint ?? string.Empty;

        // Deliberate exception to the "no raw error text in the UI" rule: this is
        // captured child-process stderr, shown on the user's own machine, and it
        // is the only place a rejected recipe flag or a CUDA initialization
        // failure is ever explained. It is not logged at Info level and is not
        // part of any diagnostics export.
        var hasDetail = status.State == LlamaServerState.Failed && !string.IsNullOrWhiteSpace(status.Detail);
        ServerErrorNotice.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        ServerErrorText.Text = hasDetail ? status.Detail! : string.Empty;
    }

    private void OnServerStatusChanged(object? sender, LlamaServerStatus status) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyServerStatus(status);
            if (_snapshot is not null) UpdateActionStates(_snapshot with { ServerStatus = status });
        });

    // ─── Handlers ───

    private void OnRedetectClick(object sender, RoutedEventArgs e) => RefreshSnapshot(refreshHardware: true);

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        CurrentApp.Settings.LocalInferenceModelId = SelectedTag(ModelCombo);
        CurrentApp.Settings.Save();
        RefreshSnapshot(refreshHardware: false);
    }

    private void OnBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var tag = SelectedTag(BackendCombo);
        CurrentApp.Settings.LocalInferenceBackendOverride = string.IsNullOrEmpty(tag) ? null : tag;
        CurrentApp.Settings.Save();
        RefreshSnapshot(refreshHardware: false);
    }

    private void OnCustomRuntimeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var path = CustomRuntimeBox.Text?.Trim();
        CurrentApp.Settings.LocalInferenceCustomRuntimePath = string.IsNullOrEmpty(path) ? null : path;
        CurrentApp.Settings.Save();
        RefreshSnapshot(refreshHardware: false);
    }

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        CurrentApp.Settings.LocalInferenceEnabled = EnabledToggle.IsOn;
        CurrentApp.Settings.Save();
        if (_snapshot is not null) UpdateActionStates(_snapshot);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        CurrentApp.Settings.LocalInferenceAutoStart = AutoStartCheck.IsChecked == true;
        CurrentApp.Settings.Save();
    }

    private void OnRegisterChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        CurrentApp.Settings.LocalInferenceRegisterWithGateway = RegisterCheck.IsChecked == true;
        CurrentApp.Settings.Save();
    }

    private void OnBindBeyondLoopbackChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        CurrentApp.Settings.LocalInferenceBindBeyondLoopback = BindBeyondLoopbackCheck.IsChecked == true;
        CurrentApp.Settings.Save();
    }

    private void OnCopyEndpointClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(EndpointText.Text))
            ClipboardHelper.CopyText(EndpointText.Text);
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_service is null || _snapshot?.SelectedModel is not { } model) return;

        // A download measured in hundreds of gigabytes is never started by a
        // single click.
        if (model.RequiresConfirmation)
        {
            ShowConfirmation(
                Lf("LocalInferencePage_ConfirmDownloadBody", model.DisplayName, FormatBytes(model.TotalSizeBytes)),
                L("LocalInferencePage_ConfirmDownloadPrimary"),
                () => StartDownload(model));
            return;
        }

        StartDownload(model);
    }

    private void StartDownload(LocalModelInfo model) =>
        AsyncEventHandlerGuard.Run(() => DownloadAsync(model), new AppLogger(), nameof(OnDownloadClick));

    private async Task DownloadAsync(LocalModelInfo model)
    {
        if (_service is null) return;

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ModelStatusText.Text = L("LocalInferencePage_StatusPreparing");
        if (_snapshot is not null) UpdateActionStates(_snapshot);

        try
        {
            // The runtime is fetched first: without it a completed model download
            // still cannot start a server, and the runtime is the smaller of the
            // two, so a failure surfaces in seconds rather than hours.
            await _service.EnsureRuntimeAsync(MakeProgress("LocalInferencePage_StatusRuntimePct"), _downloadCts.Token);
            await _service.EnsureModelAsync(model, MakeProgress("LocalInferencePage_StatusModelPct"), _downloadCts.Token);

            ModelStatusText.Text = L("LocalInferencePage_ModelReady");
        }
        catch (OperationCanceledException)
        {
            ModelStatusText.Text = L("LocalInferencePage_StatusDownloadCanceled");
        }
        catch (Exception ex)
        {
            // Privacy: the message can carry URLs, paths, and hash digests.
            Logger.Error($"[LocalInferencePage] Download failed: {ex}");
            ModelStatusText.Text = L("LocalInferencePage_StatusDownloadError");
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadProgress.Visibility = Visibility.Collapsed;
            RefreshSnapshot(refreshHardware: false);
        }
    }

    /// <summary>
    /// Progress reporter that repaints at most once per
    /// <see cref="ProgressRepaintInterval"/>, always painting the final 100 so
    /// the bar never sticks just short of complete.
    /// </summary>
    private IProgress<(long downloaded, long total)> MakeProgress(string statusKey)
    {
        var lastRepaint = DateTime.MinValue;
        return new Progress<(long downloaded, long total)>(p =>
        {
            var isFinal = p.total > 0 && p.downloaded >= p.total;
            var now = DateTime.UtcNow;
            if (!isFinal && now - lastRepaint < ProgressRepaintInterval) return;
            lastRepaint = now;

            if (p.total <= 0) return;
            var percent = (double)p.downloaded / p.total * 100;
            DownloadProgress.Value = percent;
            ModelStatusText.Text = Lf(statusKey, $"{percent:F0}");
        });
    }

    // ─── Inline confirmation ───

    /// <summary>
    /// Show the in-page confirmation bar and run <paramref name="onConfirm"/> if
    /// the user accepts. Used instead of a ContentDialog so the size being
    /// confirmed stays visible behind the prompt.
    /// </summary>
    private void ShowConfirmation(string message, string primaryLabel, Action onConfirm)
    {
        ConfirmText.Text = message;
        ConfirmPrimaryButtonText.Text = primaryLabel;
        _pendingConfirmation = onConfirm;
        ConfirmBar.Visibility = Visibility.Visible;
    }

    private void HideConfirmation()
    {
        _pendingConfirmation = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmPrimaryClick(object sender, RoutedEventArgs e)
    {
        var action = _pendingConfirmation;
        HideConfirmation();
        action?.Invoke();
    }

    private void OnConfirmCancelClick(object sender, RoutedEventArgs e) => HideConfirmation();

    private void OnDeleteModelClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.SelectedModel is not { } model) return;

        ShowConfirmation(
            Lf("LocalInferencePage_ConfirmDeleteBody", model.DisplayName, FormatBytes(model.TotalSizeBytes)),
            L("LocalInferencePage_ConfirmDeletePrimary"),
            () => AsyncEventHandlerGuard.Run(
                () => DeleteModelAsync(model), new AppLogger(), nameof(OnDeleteModelClick)));
    }

    private Task DeleteModelAsync(LocalModelInfo model)
    {
        try
        {
            new GgufModelManager(SettingsManager.SettingsDirectoryPath, new AppLogger()).Delete(model);
        }
        catch (Exception ex)
        {
            // Privacy: the message carries on-disk paths. Log it, keep the UI neutral.
            Logger.Error($"[LocalInferencePage] Model delete failed: {ex}");
            ModelStatusText.Text = L("LocalInferencePage_StatusDeleteError");
        }

        RefreshSnapshot(refreshHardware: false);
        return Task.CompletedTask;
    }

    private void OnStartClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OnStartClickAsync, new AppLogger(), nameof(OnStartClick));

    private async Task OnStartClickAsync()
    {
        if (_service is null) return;

        _serverBusy = true;
        if (_snapshot is not null) UpdateActionStates(_snapshot);
        ServerBusyRing.IsActive = true;

        try
        {
            await _service.StartAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"[LocalInferencePage] Server start failed: {ex}");
            ServerStatusText.Text = L("LocalInferencePage_ServerFailed");
        }
        finally
        {
            _serverBusy = false;
            RefreshSnapshot(refreshHardware: false);
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OnStopClickAsync, new AppLogger(), nameof(OnStopClick));

    private async Task OnStopClickAsync()
    {
        if (_service is null) return;

        _serverBusy = true;
        if (_snapshot is not null) UpdateActionStates(_snapshot);

        try
        {
            await _service.StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"[LocalInferencePage] Server stop failed: {ex}");
        }
        finally
        {
            _serverBusy = false;
            RefreshSnapshot(refreshHardware: false);
        }
    }

    // ─── Formatting ───

    private static string? SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private string DescribeHardware(HostHardwareInfo? hardware)
    {
        if (hardware is null) return L("LocalInferencePage_HardwareUnavailable");

        var parts = new System.Collections.Generic.List<string>
        {
            hardware.CpuArchitecture.ToString(),
        };

        if (hardware.TotalPhysicalMemoryBytes is { } ram)
            parts.Add(Lf("LocalInferencePage_HardwareRam", FormatBytes(ram)));

        var gpu = hardware.Gpus.FirstOrDefault();
        if (gpu is null)
        {
            parts.Add(L("LocalInferencePage_HardwareNoGpu"));
        }
        else
        {
            parts.Add(hardware.TotalNvidiaVramBytes is { } vram
                ? $"{gpu.Name} ({FormatBytes(vram)})"
                : gpu.Name);
        }

        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        var gib = bytes / (1024.0 * 1024 * 1024);
        return gib >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{gib:F1} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F0} MB");
    }
}
