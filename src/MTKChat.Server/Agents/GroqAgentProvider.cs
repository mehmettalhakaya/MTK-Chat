using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MTKChat.Server.Configuration;

namespace MTKChat.Server.Agents;

public sealed class GroqAgentProvider(HttpClient http, IOptions<AgentOptions> options) : IAgentProvider
{
    public string Name => "Groq";

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var provider = settings.Groq;
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("Groq API anahtarı sunucu secret yapılandırmasında bulunamadı.");

        var messages = new List<object>
        {
            new { role = "user", content = $"{AgentPolicy.SystemPrompt}\n\nKullanıcı mesajı:\n{prompt}" }
        };
        var combined = new System.Text.StringBuilder();

        for (var continuation = 0; continuation <= settings.MaxContinuations; continuation++)
        {
            using var response = await AgentHttpRetry.SendAsync(http, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "openai/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
                request.Content = JsonContent.Create(new
                {
                    model = provider.Model,
                    messages,
                    temperature = 0.6,
                    max_completion_tokens = 2048,
                    reasoning_effort = "medium",
                    reasoning_format = "hidden"
                });
                return request;
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Groq isteği başarısız oldu ({(int)response.StatusCode}).");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var choice = document.RootElement.GetProperty("choices")[0];
            var text = choice.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            combined.Append(text);
            var finishReason = choice.GetProperty("finish_reason").GetString();
            if (!string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                return combined.ToString();
            if (combined.Length >= settings.MaxCombinedCharacters || continuation == settings.MaxContinuations)
                return combined.AppendLine().Append("[Yanıt sağlayıcı sınırına ulaştığı için burada durduruldu.]").ToString();

            messages.Add(new { role = "assistant", content = text });
            messages.Add(new { role = "user", content = AgentPolicy.ContinuePrompt });
        }

        return combined.ToString();
    }
}
