using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

public class GgufModelManagerTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public async Task DownloadsEveryShardOfAMultiShardModel()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [500, 700, 300]);
        var manager = NewManager(temp, transport);

        await manager.DownloadAsync(model);

        Assert.True(manager.IsDownloaded(model));
        var directory = manager.GetModelDirectory(model);
        foreach (var shard in model.Shards)
            Assert.True(File.Exists(Path.Combine(directory, shard.FileName)), $"{shard.FileName} missing");
    }

    [Fact]
    public async Task KeepsUpstreamShardFileNames()
    {
        // llama.cpp finds the remaining shards by name from the first one, so
        // renaming them on disk would break loading.
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [100, 100]);
        var manager = NewManager(temp, transport);

        await manager.DownloadAsync(model);

        var onDisk = Directory.GetFiles(manager.GetModelDirectory(model)).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(model.Shards.Select(s => s.FileName).Order().ToArray(), onDisk);
    }

    [Fact]
    public async Task PrimaryShardPathPointsAtShardOne()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [100, 100, 100]);
        var manager = NewManager(temp, transport);
        await manager.DownloadAsync(model);

        var primary = manager.GetPrimaryShardPath(model);

        Assert.NotNull(primary);
        Assert.Equal(model.Shards[0].FileName, Path.GetFileName(primary));
        Assert.True(File.Exists(primary));
    }

    [Fact]
    public async Task ResumesAfterAPartiallyDownloadedModelAndSkipsPresentShards()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [200, 300, 400]);
        var manager = NewManager(temp, transport);

        // Pretend shard 1 landed during an earlier run.
        var directory = manager.GetModelDirectory(model);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, model.Shards[0].FileName), new byte[200]);

        var state = manager.GetState(model);
        Assert.False(state.IsComplete);
        Assert.Equal(1, state.ShardsPresent);
        Assert.Equal(200, state.BytesOnDisk);
        Assert.Equal(700, state.RemainingBytes);

        await manager.DownloadAsync(model);

        Assert.True(manager.IsDownloaded(model));
        // Only the two missing shards were fetched.
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task RefusesAModelWithNoPublishedCheckpoint()
    {
        using var temp = new TempDirectory();
        var transport = new FakeHttpTransport();
        var unpublished = new LocalModelInfo("pending-refuses-download", "Pending model", [], ["--temp", "1.0"], 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewManager(temp, transport).DownloadAsync(unpublished));

        Assert.Contains("no published checkpoint", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RefusesAModelWithAnUnpinnedShard()
    {
        using var temp = new TempDirectory();
        var transport = new FakeHttpTransport();
        var unpinned = new LocalModelInfo(
            "unpinned",
            "Unpinned model",
            [new GgufShard("a.gguf", "https://example.test/a.gguf", 10, null)],
            ["--temp", "1.0"],
            1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewManager(temp, transport).DownloadAsync(unpinned));

        Assert.Contains("pinned SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RefusesToStartWhenTheVolumeCannotHoldTheModel()
    {
        // A 155 GB download that dies at 90 percent on a full disk is the worst
        // possible outcome, so the check happens before any bytes move.
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [1000, 1000]);

        var manager = new GgufModelManager(
            temp.Path,
            NullLogger.Instance,
            new VerifiedFileDownloader(NullLogger.Instance, transport.ClientFactory),
            freeSpaceProbe: _ => 100);

        var ex = await Assert.ThrowsAsync<IOException>(() => manager.DownloadAsync(model));

        Assert.Contains("free", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task OnlyCountsMissingShardsInTheFreeSpaceCheck()
    {
        // Resuming a mostly-complete model must not be blocked by the full model
        // size. Free space here is deliberately set below the whole model but
        // above what the one missing shard still needs.
        using var temp = new TempDirectory();
        var transport = new FakeHttpTransport();

        var (smallBody, smallHash) = FakeHttpTransport.MakeBody(512, seed: 42);
        const string smallUrl = "https://example.test/big-model-00002-of-00002.gguf";
        transport.Add(smallUrl, smallBody);

        // Shard one is declared huge but is already on disk, so it is never
        // fetched and no body needs to exist for it.
        var model = new LocalModelInfo(
            "big-model",
            "Big model",
            [
                new GgufShard("big-model-00001-of-00002.gguf", "https://example.test/never-fetched.gguf", 8 * Gib, new string('a', 64)),
                new GgufShard("big-model-00002-of-00002.gguf", smallUrl, smallBody.Length, smallHash),
            ],
            ["--temp", "1.0"],
            MinimumRecommendedMemoryBytes: 8 * Gib);

        var manager = new GgufModelManager(
            temp.Path,
            NullLogger.Instance,
            new VerifiedFileDownloader(NullLogger.Instance, transport.ClientFactory),
            // Below the 8 GB model plus margin, above the 512 bytes plus margin.
            freeSpaceProbe: _ => 3 * Gib);

        var directory = manager.GetModelDirectory(model);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, model.Shards[0].FileName), new byte[16]);

        await manager.DownloadAsync(model);

        Assert.True(manager.IsDownloaded(model));
        Assert.Equal(smallUrl, Assert.Single(transport.Requests));
    }

    [Fact]
    public async Task ProceedsWhenFreeSpaceCannotBeDetermined()
    {
        // Network paths and unusual mounts cannot answer. Unknown must not be
        // treated as "no space"; the download fails loudly later if it matters.
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [100]);

        var manager = new GgufModelManager(
            temp.Path,
            NullLogger.Instance,
            new VerifiedFileDownloader(NullLogger.Instance, transport.ClientFactory),
            freeSpaceProbe: _ => null);

        await manager.DownloadAsync(model);

        Assert.True(manager.IsDownloaded(model));
    }

    [Fact]
    public async Task RejectsATamperedShardAndLeavesTheModelIncomplete()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [100, 100]);

        // Serve the wrong bytes for the second shard.
        transport.Add(model.Shards[1].DownloadUrl, new byte[100]);

        var manager = NewManager(temp, transport);
        await Assert.ThrowsAsync<System.Security.SecurityException>(() => manager.DownloadAsync(model));

        Assert.False(manager.IsDownloaded(model));
        var directory = manager.GetModelDirectory(model);
        Assert.True(File.Exists(Path.Combine(directory, model.Shards[0].FileName)));
        Assert.False(File.Exists(Path.Combine(directory, model.Shards[1].FileName)));
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
    }

    [Fact]
    public async Task SecondDownloadOfACompleteModelFetchesNothing()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [100, 100]);
        var manager = NewManager(temp, transport);

        await manager.DownloadAsync(model);
        var afterFirst = transport.Requests.Count;

        await manager.DownloadAsync(model);

        Assert.Equal(afterFirst, transport.Requests.Count);
    }

    [Fact]
    public async Task ReportsAggregateProgressAcrossShardsEndingAtTheTotal()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [1000, 2000, 500]);

        var reports = new List<(long downloaded, long total)>();
        var progress = new InlineProgress<(long downloaded, long total)>(reports.Add);

        await NewManager(temp, transport).DownloadAsync(model, progress);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(3500, r.total));
        Assert.Equal(3500, reports[^1].downloaded);
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].downloaded >= reports[i - 1].downloaded, "Aggregate progress went backwards.");
    }

    [Fact]
    public async Task DeleteRemovesEveryShard()
    {
        using var temp = new TempDirectory();
        var (model, transport) = MakeModel(shardSizes: [100, 100]);
        var manager = NewManager(temp, transport);
        await manager.DownloadAsync(model);

        Assert.True(manager.Delete(model));
        Assert.False(manager.IsDownloaded(model));
        Assert.False(manager.Delete(model));
    }

    [Fact]
    public void PrimaryShardPathIsNullForAnUnpublishedModel()
    {
        using var temp = new TempDirectory();
        var unpublished = new LocalModelInfo("pending-primary-shard", "Pending", [], ["--temp", "1.0"], 1);

        Assert.Null(NewManager(temp, new FakeHttpTransport()).GetPrimaryShardPath(unpublished));
    }

    /// <summary>
    /// Build a synthetic multi-shard model plus a transport that serves it, with
    /// upstream-style shard names so the ordering invariants are exercised.
    ///
    /// <para>The model id is unique per call. GgufModelManager coalesces
    /// concurrent downloads of the same id through a process-wide single-flight
    /// gate whose completed entries are evicted by an asynchronous continuation,
    /// so a shared id lets one test latch onto the previous test's finished task
    /// and skip its own download.</para>
    /// </summary>
    private static (LocalModelInfo Model, FakeHttpTransport Transport) MakeModel(
        int[] shardSizes,
        [CallerMemberName] string caller = "")
    {
        var transport = new FakeHttpTransport();
        var shards = new List<GgufShard>();
        var id = $"test-model-{caller}";

        for (var i = 0; i < shardSizes.Length; i++)
        {
            var name = $"{id}-{i + 1:00000}-of-{shardSizes.Length:00000}.gguf";
            var url = $"https://example.test/{name}";
            var (body, hash) = FakeHttpTransport.MakeBody(shardSizes[i], seed: 100 + i);
            transport.Add(url, body);
            shards.Add(new GgufShard(name, url, body.Length, hash));
        }

        var model = new LocalModelInfo(
            id,
            "Test model",
            shards,
            ["--temp", "1.0"],
            MinimumRecommendedMemoryBytes: shardSizes.Sum());

        return (model, transport);
    }

    private static GgufModelManager NewManager(TempDirectory temp, FakeHttpTransport transport) =>
        new(temp.Path,
            NullLogger.Instance,
            new VerifiedFileDownloader(NullLogger.Instance, transport.ClientFactory),
            freeSpaceProbe: _ => 100 * Gib);

    /// <summary>Reports on the calling thread so assertions see every value.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
