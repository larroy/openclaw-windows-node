using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Tests.Inference;

/// <summary>
/// In-memory HTTP transport for the download managers: serves registered byte
/// bodies, honours (or deliberately ignores) Range requests, and can be told to
/// truncate a response mid-body so resume and integrity paths are exercisable
/// without a network.
/// </summary>
internal sealed class FakeHttpTransport : HttpMessageHandler
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every absolute URL requested, in order, including repeats.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Range header values seen, in order. Null entries mean no Range header.</summary>
    public List<string?> RangeHeaders { get; } = [];

    public void Add(string url, byte[] body, bool supportsRange = true, int? truncateAfterBytes = null) =>
        _entries[url] = new Entry(body, supportsRange, truncateAfterBytes);

    /// <summary>Stop truncating a previously-truncated entry, so a retry succeeds.</summary>
    public void HealTruncation(string url) =>
        _entries[url] = _entries[url] with { TruncateAfterBytes = null };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        RangeHeaders.Add(request.Headers.Range?.ToString());

        if (!_entries.TryGetValue(url, out var entry))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        long? rangeFrom = null;
        if (request.Headers.Range is { } rangeHeader)
        {
            foreach (var range in rangeHeader.Ranges)
            {
                rangeFrom = range.From;
                break;
            }
        }

        byte[] body;
        HttpStatusCode status;

        if (rangeFrom is { } offset && entry.SupportsRange)
        {
            if (offset > entry.Body.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            body = entry.Body[(int)offset..];
            status = HttpStatusCode.PartialContent;
        }
        else
        {
            // Either no range was asked for, or this entry pretends not to
            // understand Range and answers with the whole body.
            body = entry.Body;
            status = HttpStatusCode.OK;
        }

        if (entry.TruncateAfterBytes is { } limit && body.Length > limit)
            body = body[..limit];

        // Simulate a connection that drops partway through the body, which is how
        // a long real transfer actually fails.
        if (entry.DropAfterBytes is { } dropAfter && _dropsRemaining > 0)
        {
            _dropsRemaining--;
            var delivered = Math.Min(dropAfter, body.Length);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StreamContent(new DroppingStream(body[..delivered])),
            });
        }

        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }

    /// <summary>
    /// Make the next <paramref name="times"/> responses for <paramref name="url"/>
    /// deliver <paramref name="afterBytes"/> bytes and then fail the stream, the
    /// way a dropped TCP connection does mid-transfer.
    /// </summary>
    public void DropConnectionAfter(string url, int afterBytes, int times = 1)
    {
        _entries[url] = _entries[url] with { DropAfterBytes = afterBytes };
        _dropsRemaining = times;
    }

    private int _dropsRemaining;

    /// <summary>Yields its buffer, then throws as a broken connection would.</summary>
    private sealed class DroppingStream(byte[] payload) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= payload.Length)
                throw new IOException("The connection was closed unexpectedly.");

            var n = Math.Min(count, payload.Length - _position);
            Array.Copy(payload, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= payload.Length)
                throw new IOException("The connection was closed unexpectedly.");

            var n = Math.Min(buffer.Length, payload.Length - _position);
            payload.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Factory to hand to the downloader under test.</summary>
    public Func<HttpClient> ClientFactory => () => new HttpClient(this, disposeHandler: false);

    /// <summary>Deterministic pseudo-random body plus its SHA-256, for catalog fixtures.</summary>
    public static (byte[] Body, string Sha256) MakeBody(int length, int seed)
    {
        var body = new byte[length];
        new Random(seed).NextBytes(body);
        return (body, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
    }

    public static string Sha256Of(byte[] body) =>
        Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    private sealed record Entry(byte[] Body, bool SupportsRange, int? TruncateAfterBytes, int? DropAfterBytes = null);
}
