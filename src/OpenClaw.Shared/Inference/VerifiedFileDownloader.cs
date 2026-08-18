using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// One file to fetch and verify.
/// </summary>
/// <param name="Url">HTTPS source.</param>
/// <param name="DestinationPath">Final path. Written only after verification.</param>
/// <param name="Sha256">
/// Pinned lowercase hex SHA-256. Required: a null or blank value is a hard
/// failure, so an unpinned asset can never install.
/// </param>
/// <param name="ExpectedSizeBytes">Exact expected length, or 0 when unknown.</param>
/// <param name="AllowResume">
/// Whether a partial <c>.part</c> file may be continued with a range request.
/// Worth it for the multi-gigabyte GGUF shards, pointless for small archives.
/// </param>
/// <param name="DisplayName">Name used in log and error messages.</param>
public sealed record VerifiedDownloadRequest(
    string Url,
    string DestinationPath,
    string? Sha256,
    long ExpectedSizeBytes = 0,
    bool AllowResume = false,
    string? DisplayName = null)
{
    /// <summary>Label for diagnostics; falls back to the destination file name.</summary>
    public string Label => DisplayName ?? Path.GetFileName(DestinationPath);
}

/// <summary>
/// Downloads a file to a temporary <c>.part</c> path, verifies its SHA-256, and
/// only then moves it into place.
///
/// <para><b>Fail closed.</b> No pinned hash means no download. A hash mismatch
/// deletes the partial file and throws; nothing unverified ever reaches the
/// destination path. The error deliberately does not echo the computed hash,
/// which would hand an attacker a confirmation oracle. This mirrors the audio
/// asset managers; see <c>docs/LOCAL_INFERENCE_ASSETS.md</c>.</para>
///
/// <para><b>Resume.</b> With <see cref="VerifiedDownloadRequest.AllowResume"/>,
/// an existing <c>.part</c> shorter than the expected size is continued with a
/// <c>Range</c> request. Restarting a 50 GB shard from zero after a dropped
/// connection is not an acceptable failure mode. A server that ignores the range
/// (answers 200 instead of 206) restarts the file rather than corrupting it, and
/// a resumed file whose bytes turn out to be bad still fails the hash check and
/// is deleted, so a retry starts clean instead of looping.</para>
/// </summary>
public sealed class VerifiedFileDownloader
{
    private const int BufferSize = 81920;

    private readonly IOpenClawLogger _logger;
    private readonly Func<HttpClient> _httpClientFactory;

    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="httpClientFactory">
    /// Optional override so tests can inject a fake handler. Each call gets a
    /// client that the downloader disposes.
    /// </param>
    public VerifiedFileDownloader(IOpenClawLogger logger, Func<HttpClient>? httpClientFactory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? CreateDefaultClient;
    }

    private static HttpClient CreateDefaultClient() =>
        // Large weights over a slow link legitimately take hours. The timeout
        // that matters is enforced by the caller's cancellation token, not here.
        new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Fetch and verify one file. Does nothing if the destination already exists.
    /// </summary>
    /// <param name="request">What to fetch.</param>
    /// <param name="bytesCompleted">
    /// Optional progress reporting total bytes present for this file so far,
    /// including any resumed prefix. Callers aggregating several files can sum
    /// these directly.
    /// </param>
    public async Task DownloadAsync(
        VerifiedDownloadRequest request,
        IProgress<long>? bytesCompleted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // SECURITY: refuse anything without a pinned hash before touching the
        // network. This is the single gate that keeps an unverifiable catalog
        // entry from installing.
        if (string.IsNullOrWhiteSpace(request.Sha256))
        {
            throw new InvalidOperationException(
                $"Asset '{request.Label}' has no pinned SHA-256; refusing to download. " +
                "Add a verified hash to the catalog first.");
        }

        if (File.Exists(request.DestinationPath))
        {
            _logger.Info($"[Inference] '{request.Label}' already present");
            bytesCompleted?.Report(new FileInfo(request.DestinationPath).Length);
            return;
        }

        var directory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var partPath = request.DestinationPath + ".part";

        try
        {
            await FetchToPartFileAsync(request, partPath, bytesCompleted, cancellationToken).ConfigureAwait(false);

            if (request.ExpectedSizeBytes > 0)
            {
                var actualSize = new FileInfo(partPath).Length;
                if (actualSize != request.ExpectedSizeBytes)
                {
                    throw new InvalidDataException(
                        $"Asset '{request.Label}' downloaded {actualSize} bytes but the catalog expects " +
                        $"{request.ExpectedSizeBytes}. The partial file was discarded.");
                }
            }

            await VerifyHashAsync(partPath, request.Sha256!, request.Label, cancellationToken).ConfigureAwait(false);

            File.Move(partPath, request.DestinationPath, overwrite: true);
            _logger.Info($"[Inference] '{request.Label}' downloaded and verified");
        }
        catch
        {
            // Any failure discards the partial file. Keeping a mismatched or
            // truncated .part would make the next resume attempt repeat the same
            // failure forever.
            TryDelete(partPath);
            throw;
        }
    }

    private async Task FetchToPartFileAsync(
        VerifiedDownloadRequest request,
        string partPath,
        IProgress<long>? bytesCompleted,
        CancellationToken cancellationToken)
    {
        var resumeFrom = ResolveResumeOffset(request, partPath);

        using var httpClient = _httpClientFactory();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (resumeFrom > 0)
        {
            httpRequest.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            _logger.Info($"[Inference] Resuming '{request.Label}' at {resumeFrom} bytes");
        }

        using var response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // A server that ignores the Range header answers 200 with the whole body.
        // Appending that to the existing prefix would silently corrupt the file,
        // so fall back to a clean restart instead.
        var appending = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !appending)
        {
            _logger.Warn($"[Inference] Range request for '{request.Label}' was not honored; restarting the download");
            resumeFrom = 0;
        }

        var written = appending ? resumeFrom : 0L;
        bytesCompleted?.Report(written);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var fileStream = new FileStream(
            partPath,
            appending ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                bytesCompleted?.Report(written);
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Byte offset to resume from, or 0 for a fresh download. Only a partial file
    /// strictly shorter than the known expected size is resumable: a longer or
    /// equal-length <c>.part</c> means the previous attempt already failed
    /// verification, so it is discarded rather than continued.
    /// </summary>
    private long ResolveResumeOffset(VerifiedDownloadRequest request, string partPath)
    {
        if (!File.Exists(partPath)) return 0;

        if (!request.AllowResume || request.ExpectedSizeBytes <= 0)
        {
            TryDelete(partPath);
            return 0;
        }

        long existing;
        try
        {
            existing = new FileInfo(partPath).Length;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Inference] Could not stat partial file for '{request.Label}': {ex.Message}");
            TryDelete(partPath);
            return 0;
        }

        if (existing <= 0 || existing >= request.ExpectedSizeBytes)
        {
            TryDelete(partPath);
            return 0;
        }

        return existing;
    }

    /// <summary>
    /// Compare the file's SHA-256 to the pinned value. Throws on mismatch without
    /// echoing the computed hash, which would confirm to an attacker how close a
    /// forgery got.
    /// </summary>
    private static async Task VerifyHashAsync(
        string filePath,
        string expectedHex,
        string label,
        CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        var actual = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(actual).ToLowerInvariant();

        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                $"Asset '{label}' failed its integrity check. The downloaded file does not match the pinned SHA-256.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Inference] Could not delete '{Path.GetFileName(path)}': {ex.Message}");
        }
    }
}
