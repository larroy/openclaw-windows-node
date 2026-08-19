using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// This downloader is the single gate between the catalogs and the filesystem,
/// and everything it fetches is either launched as a process or loaded into one.
/// Its fail-closed behavior is therefore pinned in detail.
/// </summary>
public class VerifiedFileDownloaderTests
{
    private const string Url = "https://example.test/asset.bin";

    [Fact]
    public async Task DownloadsAndVerifiesAGoodFile()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(4096, seed: 1);
        var transport = new FakeHttpTransport();
        transport.Add(Url, body);

        var destination = temp.Combine("asset.bin");
        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, hash, body.Length));

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task RefusesToDownloadWithoutAPinnedHash()
    {
        using var temp = new TempDirectory();
        var transport = new FakeHttpTransport();
        var destination = temp.Combine("asset.bin");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest(Url, destination, Sha256: null)));

        Assert.Contains("no pinned SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The gate must close before any network traffic, not after.
        Assert.Empty(transport.Requests);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task RejectsATamperedBodyAndLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var (body, _) = FakeHttpTransport.MakeBody(4096, seed: 2);
        var (otherBody, otherHash) = FakeHttpTransport.MakeBody(4096, seed: 3);
        Assert.NotEqual(body, otherBody);

        var transport = new FakeHttpTransport();
        transport.Add(Url, body);
        var destination = temp.Combine("asset.bin");

        // Pin the hash of a different body: the served bytes must be refused.
        await Assert.ThrowsAsync<SecurityException>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest(Url, destination, otherHash, otherBody.Length)));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task HashMismatchErrorDoesNotEchoTheComputedHash()
    {
        // Echoing the actual hash would give an attacker a confirmation oracle.
        using var temp = new TempDirectory();
        var (body, actualHash) = FakeHttpTransport.MakeBody(1024, seed: 4);
        var (_, wrongHash) = FakeHttpTransport.MakeBody(1024, seed: 5);

        var transport = new FakeHttpTransport();
        transport.Add(Url, body);

        var ex = await Assert.ThrowsAsync<SecurityException>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest(Url, temp.Combine("asset.bin"), wrongHash, body.Length)));

        Assert.DoesNotContain(actualHash, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsABodyWhoseLengthDisagreesWithTheCatalog()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(4096, seed: 6);
        var transport = new FakeHttpTransport();
        transport.Add(Url, body);
        var destination = temp.Combine("asset.bin");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest(Url, destination, hash, ExpectedSizeBytes: body.Length + 1)));

        Assert.Contains("expects", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SkipsAFileThatIsAlreadyPresent()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("asset.bin");
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);

        var transport = new FakeHttpTransport();
        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, new string('a', 64), 3));

        Assert.Empty(transport.Requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ResumesAPartialFileWithARangeRequest()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(8192, seed: 7);
        var destination = temp.Combine("asset.bin");

        // Simulate an earlier attempt that died after 3000 bytes.
        await File.WriteAllBytesAsync(destination + ".part", body[..3000]);

        var transport = new FakeHttpTransport();
        transport.Add(Url, body);

        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: true));

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
        Assert.Equal("bytes=3000-", Assert.Single(transport.RangeHeaders));
    }

    [Fact]
    public async Task RestartsCleanlyWhenTheServerIgnoresTheRangeHeader()
    {
        // Appending a full 200 body onto an existing prefix would silently
        // corrupt the file; a restart is the only safe response.
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(8192, seed: 8);
        var destination = temp.Combine("asset.bin");
        await File.WriteAllBytesAsync(destination + ".part", body[..3000]);

        var transport = new FakeHttpTransport();
        transport.Add(Url, body, supportsRange: false);

        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: true));

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DiscardsAPartialFileWhenResumeIsNotAllowed()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(4096, seed: 9);
        var destination = temp.Combine("asset.bin");
        await File.WriteAllBytesAsync(destination + ".part", body[..1000]);

        var transport = new FakeHttpTransport();
        transport.Add(Url, body);

        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: false));

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
        Assert.Null(Assert.Single(transport.RangeHeaders));
    }

    [Fact]
    public async Task DiscardsAPartialFileThatIsAlreadyAtOrOverTheExpectedSize()
    {
        // Such a file is the residue of an attempt that already failed
        // verification. Resuming from its end would download zero bytes and
        // fail the same way forever.
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(4096, seed: 10);
        var destination = temp.Combine("asset.bin");
        await File.WriteAllBytesAsync(destination + ".part", new byte[body.Length + 50]);

        var transport = new FakeHttpTransport();
        transport.Add(Url, body);

        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: true));

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
        Assert.Null(Assert.Single(transport.RangeHeaders));
    }

    [Fact]
    public async Task ATruncatedResponseFailsAndTheRetrySucceeds()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(8192, seed: 11);
        var destination = temp.Combine("asset.bin");

        var transport = new FakeHttpTransport();
        transport.Add(Url, body, truncateAfterBytes: 2000);
        var downloader = NewDownloader(transport);
        var request = new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(request));
        Assert.False(File.Exists(destination));

        transport.HealTruncation(Url);
        await downloader.DownloadAsync(request);

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ResumesAfterTheConnectionDropsMidTransfer()
    {
        // Reproduces a real failure: a 21 GB download died at 50% on a socket
        // read. The transfer must continue from the bytes already on disk rather
        // than failing or starting over.
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(60_000, seed: 20);
        var transport = new FakeHttpTransport();
        transport.Add(Url, body);
        transport.DropConnectionAfter(Url, afterBytes: 25_000);

        var destination = temp.Combine("asset.bin");
        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: true));

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, transport.Requests.Count);
        // The retry resumed rather than restarting from zero.
        Assert.Null(transport.RangeHeaders[0]);
        Assert.NotNull(transport.RangeHeaders[1]);
    }

    [Fact]
    public async Task KeepsThePartialFileWhenEveryAttemptIsExhausted()
    {
        // Deleting it would discard gigabytes of good bytes for a network fault
        // that says nothing about their validity.
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(60_000, seed: 21);
        var transport = new FakeHttpTransport();
        transport.Add(Url, body);
        transport.DropConnectionAfter(Url, afterBytes: 10_000, times: 99);

        var destination = temp.Combine("asset.bin");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest(Url, destination, hash, body.Length, AllowResume: true)));

        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".part"), "The partial file must survive a transport failure.");
        Assert.True(new FileInfo(destination + ".part").Length > 0);
    }

    [Fact]
    public async Task StopsRetryingAfterABoundedNumberOfAttempts()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(60_000, seed: 22);
        var transport = new FakeHttpTransport();
        transport.Add(Url, body);
        transport.DropConnectionAfter(Url, afterBytes: 10_000, times: 99);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest(Url, temp.Combine("asset.bin"), hash, body.Length, AllowResume: true)));

        // Bounded, so a permanently broken source fails instead of looping.
        Assert.InRange(transport.Requests.Count, 2, 10);
    }

    [Fact]
    public async Task ReportsMonotonicProgressEndingAtTheFullSize()
    {
        using var temp = new TempDirectory();
        var (body, hash) = FakeHttpTransport.MakeBody(300_000, seed: 12);
        var transport = new FakeHttpTransport();
        transport.Add(Url, body);

        var reports = new System.Collections.Generic.List<long>();
        var progress = new SynchronousProgress<long>(reports.Add);

        await NewDownloader(transport).DownloadAsync(
            new VerifiedDownloadRequest(Url, temp.Combine("asset.bin"), hash, body.Length),
            progress);

        Assert.NotEmpty(reports);
        Assert.Equal(body.Length, reports[^1]);
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i] >= reports[i - 1], "Progress went backwards.");
    }

    [Fact]
    public async Task AnHttpErrorLeavesNoPartialFile()
    {
        using var temp = new TempDirectory();
        var transport = new FakeHttpTransport();
        var destination = temp.Combine("asset.bin");

        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            NewDownloader(transport).DownloadAsync(
                new VerifiedDownloadRequest("https://example.test/missing.bin", destination, new string('a', 64), 10)));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    /// <summary>
    /// Retry backoff is zeroed so the retry paths cost no wall-clock time; the
    /// production default is a real escalating delay.
    /// </summary>
    private static VerifiedFileDownloader NewDownloader(FakeHttpTransport transport) =>
        new(NullLogger.Instance, transport.ClientFactory, retryDelay: _ => TimeSpan.Zero);

    /// <summary>
    /// <see cref="Progress{T}"/> posts to the synchronization context, so reports
    /// can arrive after the awaited call returns. These tests need the callbacks
    /// to have run by then.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
