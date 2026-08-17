using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OpenClaw.Shared.Inference;
using Xunit;

// Disambiguates from the OpenClaw.Shared.Tests.Architecture namespace.
using Arch = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// Recommending a model that does not fit costs the user a multi-gigabyte
/// download followed by a server that will not start, so the fit rules are
/// pinned across the range of hosts we expect.
/// </summary>
public class ModelRecommenderTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private static HostHardwareInfo Host(long ramGib, long? vramGib, int cudaMajor = 12)
    {
        IReadOnlyList<GpuInfo> gpus = vramGib is { } vram
            ? [new GpuInfo(GpuVendor.Nvidia, "NVIDIA Test GPU", vram * Gib, "999.99", cudaMajor)]
            : Array.Empty<GpuInfo>();

        return new HostHardwareInfo(Arch.X64, ramGib * Gib, ramGib * Gib, gpus, false);
    }

    [Fact]
    public void LargeVramWorkstation_RecommendsQwen35B()
    {
        var result = ModelRecommender.Recommend(Host(ramGib: 256, vramGib: 96));

        Assert.NotNull(result.Recommended);
        Assert.Equal(LocalModelCatalog.Qwen35BId, result.Recommended!.Id);
        Assert.Equal(
            ModelFit.Fits,
            result.Assessments.Single(a => a.Model.Id == LocalModelCatalog.Qwen35BId).Fit);
    }

    [Fact]
    public void SmallGpuWithLargeRam_FallsBackToASlowRamBackedRun()
    {
        var result = ModelRecommender.Recommend(Host(ramGib: 64, vramGib: 8));

        Assert.Equal(LocalModelCatalog.Qwen35BId, result.Recommended!.Id);

        var assessment = result.Assessments.Single(a => a.Model.Id == LocalModelCatalog.Qwen35BId);
        Assert.Equal(ModelFit.Tight, assessment.Fit);
        Assert.Contains("slow", assessment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slow", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MachineWithNoGpuButAmpleRam_StillGetsARecommendation()
    {
        var result = ModelRecommender.Recommend(Host(ramGib: 64, vramGib: null));

        Assert.Equal(LocalModelCatalog.Qwen35BId, result.Recommended!.Id);
        Assert.Contains(
            "No NVIDIA VRAM",
            result.Assessments.Single(a => a.Model.Id == LocalModelCatalog.Qwen35BId).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SmallLaptop_GetsNoRecommendation()
    {
        var result = ModelRecommender.Recommend(Host(ramGib: 16, vramGib: null));

        Assert.Null(result.Recommended);
        Assert.All(result.Assessments, a => Assert.Equal(ModelFit.WontFit, a.Fit));
    }

    [Fact]
    public void UndetectableHardware_GetsNoRecommendationRatherThanAGuess()
    {
        var result = ModelRecommender.Recommend(HostHardwareInfo.Unknown);

        Assert.Null(result.Recommended);
        Assert.Contains("could not be detected", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeepSeek_IsNeverAutoSelectedEvenOnAHostThatCouldRunIt()
    {
        // A 155 GB download must be an explicit choice, never a default.
        var result = ModelRecommender.Recommend(Host(ramGib: 1024, vramGib: 512));

        Assert.Equal(LocalModelCatalog.Qwen35BId, result.Recommended!.Id);

        var deepSeek = result.Assessments.Single(a => a.Model.Id == LocalModelCatalog.DeepSeekV4FlashId);
        Assert.Equal(ModelFit.Fits, deepSeek.Fit);
        Assert.False(deepSeek.IsEligibleForAutoSelection);
    }

    [Fact]
    public void UnpublishedCheckpoint_IsReportedAsPendingAndNotSelected()
    {
        var result = ModelRecommender.Recommend(Host(ramGib: 256, vramGib: 96));

        var pending = result.Assessments.Single(a => a.Model.Id == LocalModelCatalog.Qwen27BId);
        Assert.Equal(ModelFit.WontFit, pending.Fit);
        Assert.False(pending.IsEligibleForAutoSelection);
        Assert.Contains("not published", pending.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryCatalogEntryIsAssessed()
    {
        var result = ModelRecommender.Recommend(Host(ramGib: 64, vramGib: 24));

        Assert.Equal(LocalModelCatalog.Models.Count, result.Assessments.Count);
        Assert.Equal(
            LocalModelCatalog.Models.Select(m => m.Id),
            result.Assessments.Select(a => a.Model.Id));
    }

    [Fact]
    public void EveryDownloadableShardHasAPinnedSha256AndHttpsUrl()
    {
        // Same fail-closed contract as the audio catalogs: an entry we are willing
        // to download must be verifiable. See docs/AUDIO_MODEL_ASSETS.md.
        foreach (var model in LocalModelCatalog.Models.Where(m => m.IsDownloadable))
        {
            Assert.NotEmpty(model.Shards);
            foreach (var shard in model.Shards)
            {
                Assert.Matches(Sha256Hex, shard.Sha256!);
                Assert.StartsWith("https://", shard.DownloadUrl, StringComparison.Ordinal);
                Assert.True(shard.SizeBytes > 0, $"Shard '{shard.FileName}' has no size.");
            }
        }
    }

    [Fact]
    public void ModelsWithoutPinnedHashesAreNotDownloadable()
    {
        foreach (var model in LocalModelCatalog.Models)
        {
            var allPinned = model.Shards.Count > 0
                && model.Shards.All(s => !string.IsNullOrWhiteSpace(s.Sha256));
            Assert.Equal(allPinned, model.IsDownloadable);
        }
    }

    [Fact]
    public void RecipesDoNotSetArgumentsTheLauncherOwns()
    {
        // -m / --host / --port are set by the process launcher from runtime state.
        // A recipe that also sets them would silently win or duplicate.
        string[] reserved = ["-m", "--model", "--host", "--port"];

        foreach (var model in LocalModelCatalog.Models)
        {
            Assert.DoesNotContain(model.RecipeArgs, arg => reserved.Contains(arg, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void MultiShardModelsAreOrderedWithShardOneFirst()
    {
        // llama-server is launched against the first shard and discovers the rest
        // by name, so ordering is load-bearing, not cosmetic.
        foreach (var model in LocalModelCatalog.Models.Where(m => m.Shards.Count > 1))
        {
            Assert.Contains("00001-of-", model.PrimaryShard!.FileName, StringComparison.Ordinal);
            Assert.Equal(
                model.Shards.Select(s => s.FileName).OrderBy(f => f, StringComparer.Ordinal),
                model.Shards.Select(s => s.FileName));
        }
    }

    [Fact]
    public void CatalogIdsAreUniqueAndPathSafe()
    {
        var ids = LocalModelCatalog.Models.Select(m => m.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.Equal(-1, id.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())));
    }
}
