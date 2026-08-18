using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Where a usable <c>llama-server.exe</c> came from.
/// </summary>
public enum LlamaRuntimeSource
{
    /// <summary>Extracted from a hash-verified archive in the pinned catalog.</summary>
    Catalog = 0,
    /// <summary>A build the user supplied. Not hash-verified, by design.</summary>
    CustomBuild = 1,
}

/// <summary>
/// A resolved llama.cpp runtime.
/// </summary>
/// <param name="ServerExecutablePath">Absolute path to <c>llama-server.exe</c>.</param>
/// <param name="Source">Whether this came from the catalog or a user-supplied build.</param>
/// <param name="Variant">Catalog variant, or null for a custom build.</param>
public sealed record LlamaRuntime(
    string ServerExecutablePath,
    LlamaRuntimeSource Source,
    LlamaBackendVariant? Variant)
{
    /// <summary>Directory holding the server and its native dependencies.</summary>
    public string Directory => Path.GetDirectoryName(ServerExecutablePath) ?? string.Empty;

    /// <summary>
    /// True when this runtime bypassed integrity verification. The UI must show
    /// this state explicitly so the bypass is never silent.
    /// </summary>
    public bool IsUnverified => Source == LlamaRuntimeSource.CustomBuild;
}

/// <summary>
/// Downloads, extracts, and resolves llama.cpp runtimes.
///
/// <para>Layout: <c>&lt;data&gt;\llama\runtimes\&lt;runtime-key&gt;\</c>. The key
/// includes the pinned release tag, so bumping the catalog installs alongside the
/// old runtime rather than half-overwriting a directory another process may be
/// running from.</para>
///
/// <para>A CUDA variant carries two archives (the binaries and the CUDA runtime
/// redistributable) that both extract into that one directory. Both must land
/// before the runtime is usable, so the install is only marked complete after all
/// of them are extracted and the server executable is found.</para>
/// </summary>
public sealed class LlamaRuntimeManager
{
    /// <summary>
    /// Written into a runtime directory once every archive has been extracted.
    /// Its absence means a previous install was interrupted, so the directory is
    /// torn down and rebuilt rather than trusted. Without this marker a partial
    /// extraction that happened to contain llama-server.exe but not its CUDA DLLs
    /// would look installed and fail at launch.
    /// </summary>
    private const string CompletionMarkerName = ".install-complete";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlightInstalls =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _runtimesDirectory;
    private readonly IOpenClawLogger _logger;
    private readonly VerifiedFileDownloader _downloader;

    /// <param name="dataDirectory">Tray data directory; runtimes go under <c>llama\runtimes</c>.</param>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="downloader">Optional override so tests can inject a fake transport.</param>
    public LlamaRuntimeManager(
        string dataDirectory,
        IOpenClawLogger logger,
        VerifiedFileDownloader? downloader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimesDirectory = Path.Combine(dataDirectory, "llama", "runtimes");
        _downloader = downloader ?? new VerifiedFileDownloader(logger);
        Directory.CreateDirectory(_runtimesDirectory);
    }

    /// <summary>Directory a variant installs into, whether or not it exists yet.</summary>
    public string GetRuntimeDirectory(LlamaBackendVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return Path.Combine(_runtimesDirectory, variant.RuntimeKey);
    }

    /// <summary>
    /// True when the variant is fully installed: the completion marker is present
    /// and the server executable exists.
    /// </summary>
    public bool IsInstalled(LlamaBackendVariant variant)
    {
        var directory = GetRuntimeDirectory(variant);
        return File.Exists(Path.Combine(directory, CompletionMarkerName))
            && TryFindServerExecutable(directory) is not null;
    }

    /// <summary>
    /// Resolve a user-supplied build without downloading anything.
    /// </summary>
    /// <param name="customPath">
    /// Path to <c>llama-server.exe</c> or to a directory containing it.
    /// </param>
    /// <exception cref="FileNotFoundException">No server executable at that path.</exception>
    public LlamaRuntime ResolveCustomBuild(string customPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customPath);

        var executable = Directory.Exists(customPath)
            ? TryFindServerExecutable(customPath)
            : File.Exists(customPath) ? customPath : null;

        if (executable is null)
        {
            throw new FileNotFoundException(
                $"No {LlamaBackendCatalog.ServerExecutableName} found at '{customPath}'. " +
                "Point the custom build setting at the executable or the directory containing it.");
        }

        if (!Path.GetFileName(executable).Equals(LlamaBackendCatalog.ServerExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                $"'{Path.GetFileName(executable)}' is not {LlamaBackendCatalog.ServerExecutableName}.");
        }

        // Deliberately no hash check: this binary is the user's own. The caller
        // is responsible for surfacing LlamaRuntime.IsUnverified in the UI.
        _logger.Warn($"[Inference] Using a custom llama.cpp build at '{executable}'. It is not integrity verified.");
        return new LlamaRuntime(Path.GetFullPath(executable), LlamaRuntimeSource.CustomBuild, null);
    }

    /// <summary>
    /// Ensure a variant is installed, downloading and extracting its archives if
    /// needed, and return the resolved runtime.
    /// Concurrent calls for the same variant share one install.
    /// </summary>
    /// <param name="variant">Variant from <see cref="LlamaBackendCatalog"/>.</param>
    /// <param name="progress">Aggregate bytes downloaded and total across all archives.</param>
    public async Task<LlamaRuntime> EnsureInstalledAsync(
        LlamaBackendVariant variant,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variant);

        if (!IsInstalled(variant))
        {
            await SingleFlightDownload.RunAsync(
                InFlightInstalls,
                GetRuntimeDirectory(variant),
                token => InstallCoreAsync(variant, progress, token),
                cancellationToken).ConfigureAwait(false);
        }

        var directory = GetRuntimeDirectory(variant);
        var executable = TryFindServerExecutable(directory)
            ?? throw new InvalidOperationException(
                $"Runtime '{variant.RuntimeKey}' installed but {LlamaBackendCatalog.ServerExecutableName} was not found under '{directory}'.");

        return new LlamaRuntime(executable, LlamaRuntimeSource.Catalog, variant);
    }

    private async Task InstallCoreAsync(
        LlamaBackendVariant variant,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        // SECURITY: fail closed on an unpinned variant. VerifiedFileDownloader
        // enforces this per asset too; checking up front avoids downloading one
        // archive of a pair before discovering the other is unverifiable.
        if (!variant.IsDownloadable)
        {
            throw new InvalidOperationException(
                $"llama.cpp variant '{variant.RuntimeKey}' has assets without a pinned SHA-256; refusing to install.");
        }

        var directory = GetRuntimeDirectory(variant);

        // A previous interrupted attempt leaves a directory with no completion
        // marker. Rebuild from scratch: reusing it risks mixing archives from
        // two different releases.
        if (Directory.Exists(directory)) DeleteDirectory(directory);
        Directory.CreateDirectory(directory);

        var archivesDirectory = Path.Combine(directory, ".archives");
        Directory.CreateDirectory(archivesDirectory);

        try
        {
            var totalBytes = variant.ApproximateSizeBytes;
            var completedBytes = 0L;

            foreach (var asset in variant.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var archivePath = Path.Combine(archivesDirectory, asset.FileName);
                var baseBytes = completedBytes;
                var assetProgress = progress is null
                    ? null
                    : new InlineProgress<long>(bytes => progress.Report((baseBytes + bytes, totalBytes)));

                await _downloader.DownloadAsync(
                    new VerifiedDownloadRequest(
                        asset.DownloadUrl,
                        archivePath,
                        asset.Sha256,
                        asset.ApproximateSizeBytes,
                        AllowResume: false,
                        DisplayName: asset.FileName),
                    assetProgress,
                    cancellationToken).ConfigureAwait(false);

                completedBytes += asset.ApproximateSizeBytes;
                progress?.Report((completedBytes, totalBytes));

                _logger.Info($"[Inference] Extracting '{asset.FileName}'");
                SafeZipExtractor.ExtractTo(archivePath, directory, cancellationToken);

                // Free the archive as we go. A CUDA pair is close to 800 MB and
                // keeping both around doubles the peak disk requirement for no
                // benefit once extraction succeeded.
                TryDeleteFile(archivePath);
            }

            if (TryFindServerExecutable(directory) is null)
            {
                throw new InvalidOperationException(
                    $"Archives for '{variant.RuntimeKey}' extracted but contained no {LlamaBackendCatalog.ServerExecutableName}.");
            }

            TryDeleteDirectory(archivesDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, CompletionMarkerName),
                variant.RuntimeKey,
                cancellationToken).ConfigureAwait(false);

            _logger.Info($"[Inference] Runtime '{variant.RuntimeKey}' installed");
        }
        catch
        {
            // Leave nothing half-installed: without the completion marker the
            // directory would be rebuilt anyway, and a stale tree wastes disk.
            DeleteDirectorySafely(directory);
            throw;
        }
    }

    /// <summary>Delete an installed runtime. Returns false when it was not present.</summary>
    public bool Uninstall(LlamaBackendVariant variant)
    {
        var directory = GetRuntimeDirectory(variant);
        if (!Directory.Exists(directory)) return false;

        DeleteDirectory(directory);
        _logger.Info($"[Inference] Removed runtime '{variant.RuntimeKey}'");
        return true;
    }

    /// <summary>
    /// Locate <c>llama-server.exe</c> under a directory. Searched recursively
    /// because upstream archives have changed between a flat layout and a
    /// <c>build\bin</c> layout across releases.
    /// </summary>
    private static string? TryFindServerExecutable(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        var direct = Path.Combine(directory, LlamaBackendCatalog.ServerExecutableName);
        if (File.Exists(direct)) return direct;

        try
        {
            return Directory
                .EnumerateFiles(directory, LlamaBackendCatalog.ServerExecutableName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // An unreadable subtree means "not found" for our purposes; the
            // caller turns that into an explicit install failure.
            return null;
        }
    }

    private static void DeleteDirectory(string directory) => Directory.Delete(directory, recursive: true);

    private void DeleteDirectorySafely(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) DeleteDirectory(directory);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Inference] Could not clean up '{directory}': {ex.Message}");
        }
    }

    private void TryDeleteDirectory(string directory) => DeleteDirectorySafely(directory);

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Inference] Could not delete archive '{Path.GetFileName(path)}': {ex.Message}");
        }
    }
}
