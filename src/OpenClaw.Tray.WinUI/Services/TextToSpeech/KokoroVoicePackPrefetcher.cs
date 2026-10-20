using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using Windows.Networking.Connectivity;

namespace OpenClawTray.Services;

/// <summary>
/// Downloads the selected Kokoro voice pack in the background so the default
/// provider can speak without a manual Voice Settings download. Evaluated at
/// startup and after every settings save (setup enabling TTS, provider or voice
/// changes, the Download/Delete buttons). A failed or skipped download is not
/// retried until the next launch or save. The transfer itself is shared with
/// Voice Settings through <see cref="KokoroModelManager"/>'s single-flight.
/// </summary>
internal sealed class KokoroVoicePackPrefetcher
{
    private readonly SettingsManager _settings;
    private readonly KokoroModelManager _models;
    private readonly IOpenClawLogger _logger;

    public KokoroVoicePackPrefetcher(SettingsManager settings, IOpenClawLogger logger)
    {
        _settings = settings;
        // Same directory TextToSpeechService and Voice Settings read packs from.
        _models = new KokoroModelManager(SettingsManager.SettingsDirectoryPath, logger);
        _logger = logger;
    }

    public void Start()
    {
        _settings.Saved += (_, _) => Evaluate();
        Evaluate();
    }

    private void Evaluate()
    {
        string? packId;
        try
        {
            packId = KokoroPrefetchPolicy.SelectPackToPrefetch(
                _settings.TtsProvider,
                _settings.TtsKokoroVoiceId,
                _settings.NodeTtsEnabled || _settings.VoiceTtsEnabled,
                _settings.TtsKokoroAutoDownload,
                _models.IsPackDownloaded,
                IsMeteredConnection);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Kokoro voice pack prefetch check failed: {ex.Message}");
            return;
        }
        if (packId is null) return;

        _logger.Info($"Prefetching Kokoro voice pack '{packId}' in the background");
        // Not Task.Run: the single-flight entry is registered synchronously, so a Voice
        // Settings save that triggered this sees IsDownloadInProgress and joins it.
        _ = DownloadAsync(packId);
    }

    private async Task DownloadAsync(string packId)
    {
        try
        {
            await _models.DownloadPackAsync(packId, progress: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Kokoro voice pack prefetch failed ({VoicePackDownloadFailure.Classify(ex)}): {ex.Message}");
        }
    }

    /// <summary>Metered, roaming, over-limit, or offline connections skip the ~380 MB download.</summary>
    private static bool IsMeteredConnection()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) return true;
        var cost = profile.GetConnectionCost();
        return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
            || cost.Roaming
            || cost.OverDataLimit;
    }
}
