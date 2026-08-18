using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// On-disk state of a catalog model.
/// </summary>
/// <param name="IsComplete">Every shard is present at its final path.</param>
/// <param name="BytesOnDisk">Total bytes of the shards that are present.</param>
/// <param name="TotalBytes">Total size of the model when complete.</param>
/// <param name="ShardsPresent">How many shards are fully downloaded.</param>
/// <param name="ShardCount">How many shards the model has.</param>
public sealed record LocalModelDownloadState(
    bool IsComplete,
    long BytesOnDisk,
    long TotalBytes,
    int ShardsPresent,
    int ShardCount)
{
    /// <summary>Bytes still to fetch. Zero when complete.</summary>
    public long RemainingBytes => Math.Max(0, TotalBytes - BytesOnDisk);
}

/// <summary>
/// Downloads and manages GGUF checkpoints.
///
/// <para>Layout: <c>&lt;data&gt;\llama\models\&lt;model-id&gt;\</c>, with every
/// shard keeping its upstream file name. llama.cpp is launched against shard one
/// and discovers the rest by name, so the names and the directory grouping are
/// load-bearing rather than cosmetic.</para>
///
/// <para>Same fail-closed contract as the audio managers: no pinned hash, no
/// download; verification happens before a shard reaches its final name; a
/// failure leaves no partial file behind.</para>
/// </summary>
public sealed class GgufModelManager
{
    /// <summary>
    /// Slack required on top of the model size before a download starts. Filling
    /// a disk to the last byte breaks the whole machine, and a 155 GB download
    /// that dies at 90 percent on a full disk is the worst possible outcome.
    /// </summary>
    private const long FreeSpaceMarginBytes = 2L * 1024 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlightDownloads =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _modelsDirectory;
    private readonly IOpenClawLogger _logger;
    private readonly VerifiedFileDownloader _downloader;
    private readonly Func<string, long?> _freeSpaceProbe;

    /// <param name="dataDirectory">Tray data directory; models go under <c>llama\models</c>.</param>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="downloader">Optional override so tests can inject a fake transport.</param>
    /// <param name="freeSpaceProbe">
    /// Optional override returning free bytes for a path, or null when unknown.
    /// Injectable so the precheck can be exercised without filling a real disk.
    /// </param>
    public GgufModelManager(
        string dataDirectory,
        IOpenClawLogger logger,
        VerifiedFileDownloader? downloader = null,
        Func<string, long?>? freeSpaceProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelsDirectory = Path.Combine(dataDirectory, "llama", "models");
        _downloader = downloader ?? new VerifiedFileDownloader(logger);
        _freeSpaceProbe = freeSpaceProbe ?? DefaultFreeSpaceProbe;
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <summary>Directory a model downloads into, whether or not it exists yet.</summary>
    public string GetModelDirectory(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(_modelsDirectory, model.Id);
    }

    /// <summary>
    /// Path llama-server should be launched against: the model's first shard.
    /// Returns null when the model has no shards (an unpublished checkpoint).
    /// </summary>
    public string? GetPrimaryShardPath(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.PrimaryShard is { } shard
            ? Path.Combine(GetModelDirectory(model), shard.FileName)
            : null;
    }

    /// <summary>Inspect what is on disk for a model.</summary>
    public LocalModelDownloadState GetState(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var directory = GetModelDirectory(model);
        var present = 0;
        var bytes = 0L;

        foreach (var shard in model.Shards)
        {
            var path = Path.Combine(directory, shard.FileName);
            if (!File.Exists(path)) continue;

            present++;
            // The stored size, not the on-disk length: a shard only reaches its
            // final name after passing both the length and hash checks, so the
            // two agree and the catalog value avoids a stat per shard.
            bytes += shard.SizeBytes;
        }

        return new LocalModelDownloadState(
            IsComplete: model.Shards.Count > 0 && present == model.Shards.Count,
            BytesOnDisk: bytes,
            TotalBytes: model.TotalSizeBytes,
            ShardsPresent: present,
            ShardCount: model.Shards.Count);
    }

    /// <summary>True when every shard of the model is present.</summary>
    public bool IsDownloaded(LocalModelInfo model) => GetState(model).IsComplete;

    /// <summary>
    /// Download every missing shard of a model, verifying each one.
    /// Concurrent calls for the same model share a single download.
    /// </summary>
    /// <param name="model">Catalog entry.</param>
    /// <param name="progress">Aggregate bytes downloaded and total across all shards.</param>
    public Task DownloadAsync(
        LocalModelInfo model,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (IsDownloaded(model))
        {
            _logger.Info($"[Inference] Model '{model.Id}' already downloaded");
            progress?.Report((model.TotalSizeBytes, model.TotalSizeBytes));
            return Task.CompletedTask;
        }

        return SingleFlightDownload.RunAsync(
            InFlightDownloads,
            model.Id,
            token => DownloadCoreAsync(model, progress, token),
            cancellationToken);
    }

    private async Task DownloadCoreAsync(
        LocalModelInfo model,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        // SECURITY: fail closed. An unpublished checkpoint has no shards and an
        // unpinned one has no hash; neither may be fetched.
        if (!model.IsDownloadable)
        {
            throw new InvalidOperationException(
                model.Shards.Count == 0
                    ? $"Model '{model.Id}' has no published checkpoint yet; nothing to download."
                    : $"Model '{model.Id}' has shards without a pinned SHA-256; refusing to download.");
        }

        var directory = GetModelDirectory(model);
        Directory.CreateDirectory(directory);

        EnsureEnoughFreeSpace(model, directory);

        // Shards already on disk count toward progress from the start, so a
        // resumed multi-shard download does not appear to restart at zero.
        var completedBytes = model.Shards
            .Where(s => File.Exists(Path.Combine(directory, s.FileName)))
            .Sum(s => s.SizeBytes);

        progress?.Report((completedBytes, model.TotalSizeBytes));

        foreach (var shard in model.Shards)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(directory, shard.FileName);
            if (File.Exists(destination)) continue;

            var baseBytes = completedBytes;
            var shardProgress = progress is null
                ? null
                : new InlineProgress<long>(bytes => progress.Report((baseBytes + bytes, model.TotalSizeBytes)));

            await _downloader.DownloadAsync(
                new VerifiedDownloadRequest(
                    shard.DownloadUrl,
                    destination,
                    shard.Sha256,
                    shard.SizeBytes,
                    // Shards run to tens of gigabytes; losing one to a dropped
                    // connection has to be recoverable.
                    AllowResume: true,
                    DisplayName: $"{model.Id}/{shard.FileName}"),
                shardProgress,
                cancellationToken).ConfigureAwait(false);

            completedBytes += shard.SizeBytes;
            progress?.Report((completedBytes, model.TotalSizeBytes));
        }

        _logger.Info($"[Inference] Model '{model.Id}' downloaded and verified ({model.Shards.Count} shard(s))");
    }

    /// <summary>
    /// Refuse to start when the volume cannot hold what is left to download.
    /// Only the missing shards are counted, so resuming a mostly-complete model
    /// is not blocked by the full model size.
    /// </summary>
    private void EnsureEnoughFreeSpace(LocalModelInfo model, string directory)
    {
        var required = model.Shards
            .Where(s => !File.Exists(Path.Combine(directory, s.FileName)))
            .Sum(s => s.SizeBytes) + FreeSpaceMarginBytes;

        var available = _freeSpaceProbe(directory);
        if (available is null)
        {
            // Unknown free space is not a reason to refuse; the download will
            // fail loudly on a full disk instead.
            _logger.Debug($"[Inference] Free space for '{directory}' is unknown; skipping the precheck");
            return;
        }

        if (available < required)
        {
            throw new IOException(
                $"Model '{model.Id}' needs about {FormatGib(required)} free on the target volume " +
                $"but only {FormatGib(available.Value)} is available.");
        }
    }

    /// <summary>Delete every downloaded shard of a model. Returns false when nothing was present.</summary>
    public bool Delete(LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var directory = GetModelDirectory(model);
        if (!Directory.Exists(directory)) return false;

        Directory.Delete(directory, recursive: true);
        _logger.Info($"[Inference] Deleted model '{model.Id}'");
        return true;
    }

    private static long? DefaultFreeSpaceProbe(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Network paths and unusual mounts can throw here. Unknown is a valid
            // answer; the caller skips the precheck rather than failing.
            return null;
        }
    }

    private static string FormatGib(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
