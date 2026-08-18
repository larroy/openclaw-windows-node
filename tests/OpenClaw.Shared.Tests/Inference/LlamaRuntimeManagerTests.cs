using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit;

// Disambiguates from the OpenClaw.Shared.Tests.Architecture namespace.
using Arch = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference;

public class LlamaRuntimeManagerTests
{
    [Fact]
    public async Task InstallsAVariantAndResolvesTheServerExecutable()
    {
        using var temp = new TempDirectory();
        var (transport, variant) = SetUpTwoArchiveVariant();

        var runtime = await NewManager(temp, transport).EnsureInstalledAsync(variant);

        Assert.Equal(LlamaRuntimeSource.Catalog, runtime.Source);
        Assert.False(runtime.IsUnverified);
        Assert.EndsWith(LlamaBackendCatalog.ServerExecutableName, runtime.ServerExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(runtime.ServerExecutablePath));

        // Both archives of a CUDA pair have to land in the same directory.
        Assert.True(File.Exists(Path.Combine(runtime.Directory, "cudart64_12.dll")));
    }

    [Fact]
    public async Task DeletesTheDownloadedArchivesAfterExtracting()
    {
        // A CUDA pair is close to 800 MB; keeping the zips doubles peak disk use.
        using var temp = new TempDirectory();
        var (transport, variant) = SetUpTwoArchiveVariant();
        var manager = NewManager(temp, transport);

        var runtime = await manager.EnsureInstalledAsync(variant);

        Assert.Empty(Directory.EnumerateFiles(runtime.Directory, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SecondCallIsANoOpOnceInstalled()
    {
        using var temp = new TempDirectory();
        var (transport, variant) = SetUpTwoArchiveVariant();
        var manager = NewManager(temp, transport);

        await manager.EnsureInstalledAsync(variant);
        var requestsAfterInstall = transport.Requests.Count;
        Assert.True(manager.IsInstalled(variant));

        await manager.EnsureInstalledAsync(variant);

        Assert.Equal(requestsAfterInstall, transport.Requests.Count);
    }

    [Fact]
    public async Task RefusesAVariantWithoutPinnedHashes()
    {
        using var temp = new TempDirectory();
        var transport = new FakeHttpTransport();
        var unpinned = new LlamaBackendVariant(
            LlamaBackend.Cpu,
            Arch.X64,
            [new LlamaBackendAsset("llama-bin.zip", null, 10)],
            "Unpinned test variant");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewManager(temp, transport).EnsureInstalledAsync(unpinned));

        Assert.Contains("pinned SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task AFailedInstallLeavesNothingBehindAndIsNotReportedAsInstalled()
    {
        using var temp = new TempDirectory();
        var goodZip = MakeZip([(LlamaBackendCatalog.ServerExecutableName, "server")]);
        var variant = new LlamaBackendVariant(
            LlamaBackend.Cuda12,
            Arch.X64,
            [
                new LlamaBackendAsset("llama-bin.zip", FakeHttpTransport.Sha256Of(goodZip), goodZip.Length),
                new LlamaBackendAsset("cudart.zip", new string('a', 64), 10),
            ],
            "Half-broken test variant");

        var transport = new FakeHttpTransport();
        // Only the first archive is served; the second 404s partway through.
        transport.Add(variant.Assets[0].DownloadUrl, goodZip);

        var manager = NewManager(temp, transport);
        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureInstalledAsync(variant));

        // Critical: the first archive did contain llama-server.exe. Without the
        // completion marker this would look installed and then fail at launch
        // with a missing CUDA DLL.
        Assert.False(manager.IsInstalled(variant));
        Assert.False(Directory.Exists(manager.GetRuntimeDirectory(variant)));
    }

    [Fact]
    public async Task RebuildsARuntimeDirectoryLeftOverFromAnInterruptedInstall()
    {
        using var temp = new TempDirectory();
        var (transport, variant) = SetUpTwoArchiveVariant();
        var manager = NewManager(temp, transport);

        // Simulate an interrupted attempt: files present, no completion marker.
        var directory = manager.GetRuntimeDirectory(variant);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "stale-from-old-release.dll"), "stale");

        Assert.False(manager.IsInstalled(variant));
        var runtime = await manager.EnsureInstalledAsync(variant);

        Assert.True(manager.IsInstalled(variant));
        Assert.False(File.Exists(Path.Combine(runtime.Directory, "stale-from-old-release.dll")));
    }

    [Fact]
    public async Task FailsWhenTheArchivesContainNoServerExecutable()
    {
        using var temp = new TempDirectory();
        var zip = MakeZip([("readme.txt", "no server here")]);
        var variant = new LlamaBackendVariant(
            LlamaBackend.Cpu,
            Arch.X64,
            [new LlamaBackendAsset("llama-bin.zip", FakeHttpTransport.Sha256Of(zip), zip.Length)],
            "Serverless test variant");

        var transport = new FakeHttpTransport();
        transport.Add(variant.Assets[0].DownloadUrl, zip);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewManager(temp, transport).EnsureInstalledAsync(variant));

        Assert.Contains(LlamaBackendCatalog.ServerExecutableName, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindsAServerExecutableNestedInsideTheArchiveLayout()
    {
        // Upstream has moved between a flat layout and build\bin across releases.
        using var temp = new TempDirectory();
        var zip = MakeZip([($"build/bin/{LlamaBackendCatalog.ServerExecutableName}", "server")]);
        var variant = new LlamaBackendVariant(
            LlamaBackend.Cpu,
            Arch.X64,
            [new LlamaBackendAsset("llama-bin.zip", FakeHttpTransport.Sha256Of(zip), zip.Length)],
            "Nested test variant");

        var transport = new FakeHttpTransport();
        transport.Add(variant.Assets[0].DownloadUrl, zip);

        var runtime = await NewManager(temp, transport).EnsureInstalledAsync(variant);

        Assert.True(File.Exists(runtime.ServerExecutablePath));
        Assert.Contains("bin", runtime.ServerExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsProgressEndingAtTheTotalSize()
    {
        using var temp = new TempDirectory();
        var (transport, variant) = SetUpTwoArchiveVariant();

        (long downloaded, long total) last = (0, 0);
        var progress = new InlineProgress<(long downloaded, long total)>(p => last = p);

        await NewManager(temp, transport).EnsureInstalledAsync(variant, progress);

        Assert.Equal(variant.ApproximateSizeBytes, last.total);
        Assert.Equal(variant.ApproximateSizeBytes, last.downloaded);
    }

    [Fact]
    public void ResolvesACustomBuildFromADirectoryAndFlagsItUnverified()
    {
        using var temp = new TempDirectory();
        var buildDir = temp.Combine("my-build");
        Directory.CreateDirectory(buildDir);
        var exe = Path.Combine(buildDir, LlamaBackendCatalog.ServerExecutableName);
        File.WriteAllText(exe, "custom");

        var runtime = NewManager(temp, new FakeHttpTransport()).ResolveCustomBuild(buildDir);

        Assert.Equal(LlamaRuntimeSource.CustomBuild, runtime.Source);
        Assert.True(runtime.IsUnverified);
        Assert.Null(runtime.Variant);
        Assert.Equal(Path.GetFullPath(exe), runtime.ServerExecutablePath);
    }

    [Fact]
    public void ResolvesACustomBuildFromAnExecutablePath()
    {
        using var temp = new TempDirectory();
        var exe = temp.Combine(LlamaBackendCatalog.ServerExecutableName);
        File.WriteAllText(exe, "custom");

        var runtime = NewManager(temp, new FakeHttpTransport()).ResolveCustomBuild(exe);

        Assert.True(runtime.IsUnverified);
    }

    [Fact]
    public void RejectsACustomPathThatIsNotTheServerExecutable()
    {
        using var temp = new TempDirectory();
        var wrong = temp.Combine("llama-cli.exe");
        File.WriteAllText(wrong, "not the server");

        Assert.Throws<FileNotFoundException>(() =>
            NewManager(temp, new FakeHttpTransport()).ResolveCustomBuild(wrong));
    }

    [Fact]
    public void RejectsAMissingCustomPath()
    {
        using var temp = new TempDirectory();

        Assert.Throws<FileNotFoundException>(() =>
            NewManager(temp, new FakeHttpTransport()).ResolveCustomBuild(temp.Combine("nope")));
    }

    [Fact]
    public async Task UninstallRemovesTheRuntime()
    {
        using var temp = new TempDirectory();
        var (transport, variant) = SetUpTwoArchiveVariant();
        var manager = NewManager(temp, transport);
        await manager.EnsureInstalledAsync(variant);

        Assert.True(manager.Uninstall(variant));
        Assert.False(manager.IsInstalled(variant));
        Assert.False(manager.Uninstall(variant));
    }

    private static (FakeHttpTransport Transport, LlamaBackendVariant Variant) SetUpTwoArchiveVariant()
    {
        var binaries = MakeZip([(LlamaBackendCatalog.ServerExecutableName, "server"), ("ggml.dll", "ggml")]);
        var cudart = MakeZip([("cudart64_12.dll", "cudart")]);

        var transport = new FakeHttpTransport();

        // Asset URLs are derived from the pinned release base, so the fake is
        // registered against the variant's own DownloadUrl values.
        var variant = new LlamaBackendVariant(
            LlamaBackend.Cuda12,
            Arch.X64,
            [
                new LlamaBackendAsset("llama-bin.zip", FakeHttpTransport.Sha256Of(binaries), binaries.Length),
                new LlamaBackendAsset("cudart.zip", FakeHttpTransport.Sha256Of(cudart), cudart.Length),
            ],
            "Test CUDA variant");

        transport.Add(variant.Assets[0].DownloadUrl, binaries);
        transport.Add(variant.Assets[1].DownloadUrl, cudart);

        return (transport, variant);
    }

    private static LlamaRuntimeManager NewManager(TempDirectory temp, FakeHttpTransport transport) =>
        new(temp.Path, NullLogger.Instance, new VerifiedFileDownloader(NullLogger.Instance, transport.ClientFactory));

    private static byte[] MakeZip((string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Reports on the calling thread so assertions see the final value.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
