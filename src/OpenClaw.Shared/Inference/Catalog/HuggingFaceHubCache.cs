using OpenClaw.Shared.IO;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>
/// Resolves paths in the standard Hugging Face hub cache layout
/// (<c>&lt;cache root&gt;\models--&lt;org&gt;--&lt;repo&gt;\snapshots\&lt;revision&gt;\&lt;file&gt;</c>), the
/// same layout <c>huggingface_hub</c>, <c>huggingface-cli</c>, and llama.cpp's own
/// <c>--hf-repo</c> downloader use. Matching it lets a model already downloaded by any
/// of those tools be recognized and reused here, and vice versa.
/// </summary>
/// <remarks>
/// Only the visible folder shape is replicated: each file is written directly at its
/// snapshot path, without the content-addressed <c>blobs/</c> store or symlinks real
/// <c>huggingface_hub</c> caches use. This sacrifices disk dedup between two revisions
/// of the same file, but needs no elevated privileges or Developer Mode on Windows.
///
/// Unlike the app-owned Local AI directories under <c>LocalAiPathPolicy</c>, this cache
/// root is not exclusively owned by this app -- other tools may legitimately create
/// their own files, and even symlinks, inside sibling <c>models--*</c> folders.
/// Validation here is therefore narrower: it rejects path traversal and unsafe path
/// segments, and refuses to write through a reparse point at our own target or its
/// immediate parent, but does not require the whole cache tree to be reparse-point-free.
/// </remarks>
public static class HuggingFaceHubCache
{
    private const string HubCacheEnvironmentVariable = "HF_HUB_CACHE";
    private const string LegacyHubCacheEnvironmentVariable = "HUGGINGFACE_HUB_CACHE";
    private const string HubHomeEnvironmentVariable = "HF_HOME";

    /// <summary>
    /// Resolves the Hugging Face hub cache root using the same precedence
    /// <c>huggingface_hub</c> uses: <c>HF_HUB_CACHE</c>, then the legacy
    /// <c>HUGGINGFACE_HUB_CACHE</c>, then <c>&lt;HF_HOME&gt;\hub</c>, then the default
    /// <c>%USERPROFILE%\.cache\huggingface\hub</c>.
    /// </summary>
    public static string ResolveCacheRoot() => ResolveCacheRoot(Environment.GetEnvironmentVariable);

    internal static string ResolveCacheRoot(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        string? explicitCache = NullIfWhiteSpace(getEnvironmentVariable(HubCacheEnvironmentVariable))
            ?? NullIfWhiteSpace(getEnvironmentVariable(LegacyHubCacheEnvironmentVariable));
        if (explicitCache is not null)
            return WindowsPathSafety.NormalizePath(explicitCache);

        string? home = NullIfWhiteSpace(getEnvironmentVariable(HubHomeEnvironmentVariable));
        if (home is not null)
            return WindowsPathSafety.NormalizePath(Path.Combine(home, "hub"));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WindowsPathSafety.NormalizePath(Path.Combine(userProfile, ".cache", "huggingface", "hub"));
    }

    /// <summary>
    /// Computes the standard hub-cache snapshot path for one pinned Hugging Face model
    /// file, plus a same-directory <c>.partial</c> sibling used while downloading.
    /// </summary>
    public static bool TryGetSnapshotPaths(
        string cacheRoot,
        string repositoryId,
        string revision,
        string fileName,
        out string modelPath,
        out string partialPath,
        out string error)
    {
        modelPath = "";
        partialPath = "";

        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            error = "The Hugging Face hub cache root is required.";
            return false;
        }

        string[] repositorySegments = repositoryId?.Split('/') ?? [];
        if (repositorySegments.Length != 2 ||
            repositorySegments.Any(segment => !WindowsPathSafety.IsSafeSegment(segment)) ||
            revision is null || revision.Length != 40 || !PinnedArtifactValidation.IsLowerHex(revision, 40) ||
            !WindowsPathSafety.IsSafeSegment(fileName) ||
            !string.Equals(Path.GetExtension(fileName), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = "The Hugging Face model identity contains an invalid path segment.";
            return false;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
            string repositoryFolder = $"models--{repositorySegments[0]}--{repositorySegments[1]}";
            string snapshotDirectory = WindowsPathSafety.NormalizePath(
                Path.Combine(normalizedRoot, repositoryFolder, "snapshots", revision));
            modelPath = WindowsPathSafety.NormalizePath(Path.Combine(snapshotDirectory, fileName));
            partialPath = WindowsPathSafety.NormalizePath(Path.Combine(snapshotDirectory, fileName + ".partial"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            modelPath = "";
            partialPath = "";
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(modelPath, normalizedRoot) ||
            !WindowsPathSafety.IsStrictDescendant(partialPath, normalizedRoot))
        {
            modelPath = "";
            partialPath = "";
            error = "The Hugging Face model path escaped the hub cache root.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Validates that <paramref name="candidatePath"/> is a fully qualified path
    /// contained within <paramref name="cacheRoot"/>, and that neither it nor its
    /// immediate parent directory is currently a reparse point. Used both to accept a
    /// model path recorded in a manifest and to authorize deleting one this app wrote.
    /// </summary>
    public static bool TryValidateManagedPath(
        string cacheRoot,
        string candidatePath,
        out string validatedPath,
        out string error)
    {
        validatedPath = "";
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            error = "The Hugging Face hub cache root is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidatePath) ||
            !(Path.IsPathFullyQualified(candidatePath) || Path.IsPathRooted(candidatePath)))
        {
            error = "The Hugging Face model path must be a fully qualified path.";
            return false;
        }

        string normalizedRoot;
        string normalizedPath;
        try
        {
            normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
            normalizedPath = WindowsPathSafety.NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(normalizedPath, normalizedRoot))
        {
            error = $"Hugging Face model path '{normalizedPath}' is not contained within the hub cache root.";
            return false;
        }

        if (!TryRejectReparsePoint(normalizedPath, out error))
            return false;

        string? parent = Path.GetDirectoryName(normalizedPath);
        if (parent is not null && !TryRejectReparsePoint(parent, out error))
            return false;

        validatedPath = normalizedPath;
        error = "";
        return true;
    }

    private static bool TryRejectReparsePoint(string path, out string error)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"Refusing to operate on '{path}' because it is a reparse point.";
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Cannot verify Hugging Face hub cache path '{path}': {ex.Message}";
            return false;
        }

        error = "";
        return true;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
