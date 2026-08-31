using OpenClaw.Connection.LocalAi;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerInferenceClientTests
{
    private const string ModelAlias = "qwen3.6-27b-mtp-q4-k-m";
    private static readonly Uri s_endpoint = new("http://127.0.0.1:18803/v1");

    /// <summary>
    /// The body llama-server actually returns when a model instance dies during load — the case
    /// that previously surfaced as a bare "HTTP 500 (InternalServerError)" with no root cause.
    /// </summary>
    private const string ModelLoadFailureBody =
        """
        {"error":{"code":500,"message":"model name=qwen3.6-27b-mtp-q4-k-m failed to load","type":"server_error"}}
        """;

    [Fact]
    public async Task VerifyAsync_SurfacesLlamaServerErrorBodyOnHttpFailure()
    {
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, ModelLoadFailureBody))));

        LlamaServerInferenceException failure =
            await Assert.ThrowsAsync<LlamaServerInferenceException>(
                () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.Equal(500, failure.StatusCode);
        Assert.Equal("model name=qwen3.6-27b-mtp-q4-k-m failed to load", failure.ServerError);
        Assert.Contains("HTTP 500", failure.Message, StringComparison.Ordinal);
        Assert.Contains("failed to load", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>500</html>")]
    [InlineData("{")]
    [InlineData("""{"detail":"nope"}""")]
    [InlineData("oversized")]
    public async Task VerifyAsync_FallsBackToStatusOnlyWhenErrorBodyIsUnusable(string body)
    {
        string payload = body == "oversized" ? new string('x', 32 * 1024) : body;
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, payload))));

        LlamaServerInferenceException failure =
            await Assert.ThrowsAsync<LlamaServerInferenceException>(
                () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.Equal(500, failure.StatusCode);
        Assert.Contains("HTTP 500", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the class privacy contract: assistant output must never reach an exception message,
    /// even now that the failure path reads a response body.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_DoesNotLeakAssistantContent()
    {
        const string sentinel = "SENTINEL-ASSISTANT-CONTENT";
        string payload = JsonSerializer.Serialize(new
        {
            model = "a-different-alias",
            choices = new[] { new { message = new { content = sentinel } } },
        });
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, payload))));

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.DoesNotContain(sentinel, failure.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string payload) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
