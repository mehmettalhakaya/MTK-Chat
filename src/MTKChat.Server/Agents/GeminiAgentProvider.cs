using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MTKChat.Server.Configuration;

namespace MTKChat.Server.Agents;

public sealed class GeminiAgentProvider(HttpClient http, IOptions<AgentOptions> options) : IAgentProvider
{
    public string Name => "Gemini";

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var provider = settings.Gemini;
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("Gemini API anahtarı sunucu secret yapılandırmasında bulunamadı.");

        var contents = new List<object>
        {
            new { role = "user", parts = new[] { new { text = prompt } } }
        };
        var combined = new System.Text.StringBuilder();
        var models = new[] { provider.Model }
            .Concat(provider.FallbackModels)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeModel = 0;

        for (var continuation = 0; continuation <= settings.MaxContinuations; continuation++)
        {
            HttpResponseMessage? received = null;
            for (var modelIndex = activeModel; modelIndex < models.Length; modelIndex++)
            {
                var model = models[modelIndex];
                received = await AgentHttpRetry.SendAsync(http, () =>
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
                    request.Headers.Add("x-goog-api-key", provider.ApiKey);
                    request.Content = JsonContent.Create(new
                    {
                        systemInstruction = new { parts = new[] { new { text = AgentPolicy.SystemPrompt } } },
                        contents,
                        generationConfig = new { maxOutputTokens = 8192, temperature = 0.6 }
                    });
                    return request;
                }, cancellationToken);
                if (received.IsSuccessStatusCode)
                {
                    activeModel = modelIndex;
                    break;
                }

                // A preview model can remain overloaded after retries; try a configured stable model for 5xx too.
                var status = (int)received.StatusCode;
                if ((status is not (404 or 408 or 429) && status < 500) || modelIndex == models.Length - 1) break;
                received.Dispose();
                received = null;
            }

            using var response = received ?? throw new InvalidOperationException("Yapılandırılmış bir Gemini modeli bulunamadı.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Gemini isteği başarısız oldu ({(int)response.StatusCode}).");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var candidates = document.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0)
                throw new InvalidOperationException("Gemini güvenli bir yanıt üretemedi.");
            var candidate = candidates[0];
            var text = string.Concat(candidate.GetProperty("content").GetProperty("parts").EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString()));
            combined.Append(text);

            var finishReason = candidate.TryGetProperty("finishReason", out var finish)
                ? finish.GetString()
                : "STOP";
            if (!string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                return combined.ToString();
            if (combined.Length >= settings.MaxCombinedCharacters || continuation == settings.MaxContinuations)
                return combined.AppendLine().Append("[Yanıt sağlayıcı sınırına ulaştığı için burada durduruldu.]").ToString();

            contents.Add(new { role = "model", parts = new[] { new { text } } });
            contents.Add(new { role = "user", parts = new[] { new { text = AgentPolicy.ContinuePrompt } } });
        }

        return combined.ToString();
    }
}
