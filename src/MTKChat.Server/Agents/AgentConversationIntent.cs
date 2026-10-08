using System.Globalization;
using System.Text.RegularExpressions;

namespace MTKChat.Server.Agents;

public static class AgentConversationIntent
{
    public static Guid? FirstSpeakerFor(string text)
    {
        var normalized = text.ToLower(CultureInfo.GetCultureInfo("tr-TR"))
            .Replace("ı", "i", StringComparison.Ordinal)
            .Replace("ş", "s", StringComparison.Ordinal)
            .Replace("ç", "c", StringComparison.Ordinal)
            .Replace("ğ", "g", StringComparison.Ordinal)
            .Replace("ü", "u", StringComparison.Ordinal)
            .Replace("ö", "o", StringComparison.Ordinal);
        if (!Regex.IsMatch(normalized, @"\b(sohbet|konus|tartis)\w*\b", RegexOptions.CultureInvariant))
            return null;

        var withGroq = Regex.IsMatch(normalized, @"\bgroq(?:['’]?(?:la|yla)|\s+ile)\b", RegexOptions.CultureInvariant);
        var withGemini = Regex.IsMatch(normalized, @"\bgemini(?:['’]?(?:yle|le)|\s+ile)\b", RegexOptions.CultureInvariant);
        if (withGroq == withGemini) return null;
        return withGroq ? AgentIdentityRegistry.GeminiId : AgentIdentityRegistry.GroqId;
    }
}
