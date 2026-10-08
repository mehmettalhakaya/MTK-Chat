namespace MTKChat.Server.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public string Provider { get; init; } = "Unconfigured";
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed class SiteAuthenticationOptions
{
    public const string SectionName = "SiteAuthentication";
    public string BaseUrl { get; init; } = "https://mtkaya.me";
    public string VerifyPath { get; init; } = string.Empty;
    public string SharedSecret { get; init; } = string.Empty;
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public long MaxEncryptedAttachmentBytes { get; init; } = 15 * 1024 * 1024;
}

public sealed class AgentOptions
{
    public const string SectionName = "Agents";
    public bool Enabled { get; init; } = true;
    public int MaxRequestsPerUserPerHour { get; init; } = 30;
    public int MaxRoundtableTurns { get; init; } = 6;
    public int MaxContinuations { get; init; } = 8;
    public int MaxCombinedCharacters { get; init; } = 250_000;
    public string KeyStorePath { get; init; } = string.Empty;
    public AgentProviderOptions Gemini { get; init; } = new();
    public AgentProviderOptions Groq { get; init; } = new();
}

public sealed class AgentProviderOptions
{
    public string Model { get; init; } = string.Empty;
    public string[] FallbackModels { get; init; } = Array.Empty<string>();
    public string ApiKey { get; init; } = string.Empty;
}
