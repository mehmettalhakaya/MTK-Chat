using MTKChat.Server.Agents;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class AgentConversationIntentTests
{
    [Theory]
    [InlineData("Groq'la sohbet etsenize")]
    [InlineData("groqla sohbet etsenize.")]
    [InlineData("Groq ile konuşur musun?")]
    public void GroqPartnerRequestsStartWithGemini(string input) =>
        Assert.Equal(AgentIdentityRegistry.GeminiId, AgentConversationIntent.FirstSpeakerFor(input));

    [Theory]
    [InlineData("Gemini'yle sohbet edin")]
    [InlineData("geminiyle sohbet edin.")]
    [InlineData("Gemini ile tartış")]
    public void GeminiPartnerRequestsStartWithGroq(string input) =>
        Assert.Equal(AgentIdentityRegistry.GroqId, AgentConversationIntent.FirstSpeakerFor(input));

    [Theory]
    [InlineData("Groq hakkında bilgi ver")]
    [InlineData("Gemini dur")]
    [InlineData("Merhaba")]
    public void UnrelatedMessagesDoNotStartRoundtable(string input) =>
        Assert.Null(AgentConversationIntent.FirstSpeakerFor(input));
}
