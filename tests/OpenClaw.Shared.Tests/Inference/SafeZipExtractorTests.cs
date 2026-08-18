using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// These archives carry native executables that are then launched, so an entry
/// that escapes the destination is a code-execution primitive, not a tidiness
/// issue. The guard is asserted directly rather than trusted to the framework.
/// </summary>
public class SafeZipExtractorTests
{
    [Fact]
    public void ExtractsFilesAndNestedDirectories()
    {
        using var temp = new TempDirectory();
        var archive = temp.Combine("bundle.zip");
        WriteZip(archive, [("llama-server.exe", "server"), ("lib/ggml.dll", "lib")]);

        var destination = temp.Combine("out");
        var written = SafeZipExtractor.ExtractTo(archive, destination);

        Assert.Equal(2, written.Count);
        Assert.Equal("server", File.ReadAllText(Path.Combine(destination, "llama-server.exe")));
        Assert.Equal("lib", File.ReadAllText(Path.Combine(destination, "lib", "ggml.dll")));
    }

    [Fact]
    public void RejectsAnEntryThatEscapesTheDestination()
    {
        using var temp = new TempDirectory();
        var archive = temp.Combine("evil.zip");
        WriteZip(archive, [("../../evil.exe", "pwned")]);

        var destination = temp.Combine("out");

        var ex = Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractTo(archive, destination));

        Assert.Contains("outside the destination", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(temp.Combine("evil.exe")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "..", "evil.exe")));
    }

    [Fact]
    public void RejectsASiblingDirectoryPrefixAttack()
    {
        // "out-evil" shares a string prefix with "out"; only a separator-aware
        // containment check rejects it.
        using var temp = new TempDirectory();
        var archive = temp.Combine("evil.zip");
        WriteZip(archive, [("../out-evil/payload.exe", "pwned")]);

        Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractTo(archive, temp.Combine("out")));
        Assert.False(File.Exists(temp.Combine("out-evil", "payload.exe")));
    }

    [Fact]
    public void OverwritesAnExistingFile()
    {
        // Two archives (llama.cpp binaries plus cudart) extract into one
        // directory, so a second pass must not fail on an existing name.
        using var temp = new TempDirectory();
        var destination = temp.Combine("out");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "shared.dll"), "old");

        var archive = temp.Combine("bundle.zip");
        WriteZip(archive, [("shared.dll", "new")]);

        SafeZipExtractor.ExtractTo(archive, destination);

        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "shared.dll")));
    }

    [Fact]
    public void CreatesTheDestinationWhenItDoesNotExist()
    {
        using var temp = new TempDirectory();
        var archive = temp.Combine("bundle.zip");
        WriteZip(archive, [("a.txt", "a")]);

        var destination = temp.Combine("deep", "nested", "out");
        SafeZipExtractor.ExtractTo(archive, destination);

        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
    }

    private static void WriteZip(string path, (string Name, string Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            // CreateEntry is used directly (not CreateEntryFromFile) so the
            // traversal names survive into the archive verbatim.
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }
}
