using System.Linq;
using System.Text.RegularExpressions;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Inference;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Pre-GA security guard. Every shipped Whisper model and Piper voice MUST
/// have a pinned SHA-256 hash so the runtime can refuse tampered downloads.
/// New entries that forget the hash will fail this test loudly instead of
/// quietly being installable from a compromised source.
///
/// See WhisperModelManager.AvailableModels, PiperVoiceManager.AvailableVoices,
/// and docs/AUDIO_MODEL_ASSETS.md.
/// </summary>
public class AssetHashPinningTests
{
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    [Fact]
    public void EveryWhisperModel_HasPinnedSha256()
    {
        Assert.NotEmpty(WhisperModelManager.AvailableModels);
        foreach (var m in WhisperModelManager.AvailableModels)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Sha256),
                $"Whisper model '{m.Name}' is missing a pinned SHA-256 hash. Add one to AvailableModels.");
            Assert.Matches(Sha256Hex, m.Sha256!);
        }
    }

    [Fact]
    public void EveryPiperVoice_HasPinnedSha256()
    {
        Assert.NotEmpty(PiperVoiceManager.AvailableVoices);
        foreach (var v in PiperVoiceManager.AvailableVoices)
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Sha256),
                $"Piper voice '{v.VoiceId}' is missing a pinned SHA-256 hash. Add one to AvailableVoices.");
            Assert.Matches(Sha256Hex, v.Sha256!);
        }
    }

    [Fact]
    public void EveryWhisperModel_UsesHttpsDownloadUrl()
    {
        foreach (var m in WhisperModelManager.AvailableModels)
        {
            Assert.StartsWith("https://", m.DownloadUrl);
        }
    }

    [Fact]
    public void EveryPiperVoice_UsesHttpsDownloadUrl()
    {
        foreach (var v in PiperVoiceManager.AvailableVoices)
        {
            Assert.StartsWith("https://", v.DownloadUrl);
        }
    }

    [Fact]
    public void SileroVadModel_HasPinnedSha256()
    {
        Assert.False(string.IsNullOrWhiteSpace(SileroVadModelManifest.Sha256),
            "Silero VAD model is missing a pinned SHA-256 hash. Add one to SileroVadModelManifest.");
        Assert.Matches(Sha256Hex, SileroVadModelManifest.Sha256);
        Assert.StartsWith("https://", SileroVadModelManifest.DownloadUrl);
    }

    [Fact]
    public void EveryLlamaBackendAsset_HasPinnedSha256AndSize()
    {
        Assert.NotEmpty(LlamaBackendCatalog.Variants);
        foreach (var variant in LlamaBackendCatalog.Variants)
        {
            Assert.True(variant.IsDownloadable,
                $"llama.cpp variant '{variant.RuntimeKey}' is not downloadable. " +
                "Every shipped variant must have a pinned SHA-256 on all of its assets.");

            foreach (var asset in variant.Assets)
            {
                Assert.False(string.IsNullOrWhiteSpace(asset.Sha256),
                    $"llama.cpp asset '{asset.FileName}' is missing a pinned SHA-256 hash.");
                Assert.Matches(Sha256Hex, asset.Sha256!);
                Assert.StartsWith("https://", asset.DownloadUrl);
                Assert.True(asset.ApproximateSizeBytes > 0,
                    $"llama.cpp asset '{asset.FileName}' is missing its size. " +
                    "The size is cross-checked against the release API when pinning the hash.");
            }
        }
    }

    [Fact]
    public void EveryPublishedLocalModel_HasPinnedSha256()
    {
        // Unpublished checkpoints legitimately carry no shards; they are not
        // downloadable and cannot be selected. Anything with shards must be pinned.
        var published = LocalModelCatalog.Models.Where(m => m.Shards.Count > 0).ToArray();
        Assert.NotEmpty(published);

        foreach (var model in published)
        {
            foreach (var shard in model.Shards)
            {
                Assert.False(string.IsNullOrWhiteSpace(shard.Sha256),
                    $"GGUF shard '{shard.FileName}' of model '{model.Id}' is missing a pinned SHA-256 hash.");
                Assert.Matches(Sha256Hex, shard.Sha256!);
                Assert.StartsWith("https://", shard.DownloadUrl);
                Assert.True(shard.SizeBytes > 0, $"GGUF shard '{shard.FileName}' is missing its size.");
            }

            Assert.True(model.IsDownloadable);
        }
    }
}
