using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Extracts a zip archive with a path-traversal guard.
///
/// <para><see cref="ZipFile.ExtractToDirectory(string, string)"/> already rejects
/// escaping entries on modern .NET, but these archives carry native executables
/// that we then launch, so the check is made explicit and testable here rather
/// than inherited from a framework implementation detail. An entry that resolves
/// outside the destination aborts the whole extraction.</para>
/// </summary>
public static class SafeZipExtractor
{
    /// <summary>
    /// Extract every entry of <paramref name="archivePath"/> into
    /// <paramref name="destinationDirectory"/>, overwriting existing files.
    /// </summary>
    /// <returns>Relative paths of the files written, in archive order.</returns>
    /// <exception cref="InvalidDataException">
    /// An entry resolves outside the destination directory.
    /// </exception>
    public static IReadOnlyList<string> ExtractTo(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        // Trailing separator matters: without it "C:\dir" would be accepted as a
        // prefix of "C:\dir-evil\payload.exe".
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory))
            + Path.DirectorySeparatorChar;

        var written = new List<string>();
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A directory entry has an empty Name. Nothing to write; the file
            // entries below create their own parent directories.
            if (entry.Name.Length == 0) continue;

            var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' resolves outside the destination directory. " +
                    "Refusing to extract.");
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            entry.ExtractToFile(targetPath, overwrite: true);
            written.Add(Path.GetRelativePath(destinationDirectory, targetPath));
        }

        return written;
    }
}
