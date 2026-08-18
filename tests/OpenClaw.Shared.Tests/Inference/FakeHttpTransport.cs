using System;
using System.Collections.Generic;
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

        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
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

    private sealed record Entry(byte[] Body, bool SupportsRange, int? TruncateAfterBytes);
}
