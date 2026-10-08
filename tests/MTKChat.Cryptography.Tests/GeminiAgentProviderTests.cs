using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using MTKChat.Server.Agents;
using MTKChat.Server.Configuration;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class GeminiAgentProviderTests
{
    [Fact]
    public async Task ServiceUnavailableFallsBackAfterRetries()
    {
        using var handler = new StubHandler(new[]
        {
            Failure(HttpStatusCode.ServiceUnavailable),
            Failure(HttpStatusCode.ServiceUnavailable),
            Failure(HttpStatusCode.ServiceUnavailable),
            Success("Yedek model yanıtı")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var provider = CreateProvider(client);

        var answer = await provider.CompleteAsync("Merhaba", CancellationToken.None);

        Assert.Equal("Yedek model yanıtı", answer);
        Assert.Equal(new[] { "preview", "preview", "preview", "stable" }, handler.Models);
    }

    [Fact]
    public async Task BadRequestDoesNotSwitchModels()
    {
        using var handler = new StubHandler(new[] { Failure(HttpStatusCode.BadRequest) });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync("Merhaba", CancellationToken.None));

        Assert.Equal(new[] { "preview" }, handler.Models);
    }

    private static GeminiAgentProvider CreateProvider(HttpClient client) => new(client,
        Options.Create(new AgentOptions
        {
            Gemini = new AgentProviderOptions
            {
                ApiKey = "unit-test-placeholder",
                Model = "preview",
                FallbackModels = new[] { "stable" }
            }
        }));

    private static HttpResponseMessage Failure(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static HttpResponseMessage Success(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]},\"finishReason\":\"STOP\"}]}",
            Encoding.UTF8,
            "application/json")
    };

    private sealed class StubHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<string> Models { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Models.Add(request.RequestUri!.Segments[^1].Split(':')[0]);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
