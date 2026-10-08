using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Agents;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class AgentRoundtableTests
{
    private static readonly Guid DemoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LobbyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Theory]
    [InlineData("groqla sohbet etsenize.", "Gemini", "Groq")]
    [InlineData("geminiyle sohbet edin.", "Groq", "Gemini")]
    public async Task NaturalPartnerRequestRelaysFirstReplyToSecondAgent(
        string request, string firstName, string secondName)
    {
        var state = new ChatState();
        state.UpsertSiteUser(DemoId, "Test Yönetici", "test@example.test", "admin");
        using var user = DeviceIdentity.Create();
        using var agents = new AgentIdentityRegistry(state);
        Assert.True(state.RegisterDevice(DemoId, new RegisterDeviceRequest(
            "Test", user.ExportEncryptionPublicKey(), user.ExportSigningPublicKey())));
        var first = new RecordingProvider(firstName);
        var second = new RecordingProvider(secondName);
        var options = Options.Create(new AgentOptions { MaxRoundtableTurns = 2 });
        var worker = new AgentWorker(state, agents, new IAgentProvider[] { first, second },
            new AgentRateLimiter(options), options, NullLogger<AgentWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var messageId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var bytes = Encoding.UTF8.GetBytes(request);
            var payloads = agents.All.Select(item => MessageCryptography.Encrypt(
                bytes, messageId, LobbyId, DemoId, item.Key, createdAt,
                item.Value.Identity.ExportEncryptionPublicKey(), user.SigningKey)).ToArray();
            Assert.True(state.AddMessage(DemoId, new SendMessageRequest(
                messageId, LobbyId, "text", createdAt, null, payloads, null), out var source));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            StoredMessage[] replies;
            do
            {
                replies = state.GetMessages(DemoId, LobbyId, null)
                    .Where(item => item.SenderId != DemoId).ToArray();
                if (replies.Length >= 2) break;
                await Task.Delay(20, timeout.Token);
            } while (true);

            Assert.Equal(2, replies.Length);
            var sourceStatus = state.GetMessages(DemoId, LobbyId, null).Single(m => m.Id == source!.Id).Delivery!;
            Assert.Equal("read", sourceStatus.Status);
            Assert.Equal(2, sourceStatus.Recipients.Count);
            Assert.All(sourceStatus.Recipients, r => Assert.NotNull(r.ReadAt));
            Assert.Equal(firstName == "Gemini" ? AgentIdentityRegistry.GeminiId : AgentIdentityRegistry.GroqId,
                replies[0].SenderId);
            Assert.Equal(secondName == "Gemini" ? AgentIdentityRegistry.GeminiId : AgentIdentityRegistry.GroqId,
                replies[1].SenderId);
            Assert.Contains(firstName + " yanıtı", Assert.Single(second.Prompts));
            Assert.Contains("Henüz mesaj yok", Assert.Single(first.Prompts));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private sealed class RecordingProvider(string name) : IAgentProvider
    {
        public string Name => name;
        public List<string> Prompts { get; } = new();

        public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Name + " yanıtı");
        }
    }
}
