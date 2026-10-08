using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Services;
using MTKChat.Server.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Security.Cryptography;

namespace MTKChat.Server.Agents;

public sealed class AgentIdentityRegistry : IDisposable
{
    public static readonly Guid GeminiId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid GroqId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Dictionary<Guid, AgentIdentity> _identities;

    public AgentIdentityRegistry(ChatState state, IOptions<AgentOptions>? options = null)
    {
        var path = options?.Value.KeyStorePath;
        var devices = string.IsNullOrWhiteSpace(path) ? CreateDevices() : LoadOrCreateDevices(path);
        _identities = new Dictionary<Guid, AgentIdentity>
        {
            [GeminiId] = new("Gemini", devices.Gemini),
            [GroqId] = new("Groq", devices.Groq)
        };
        foreach (var (userId, agent) in _identities)
        {
            if (!state.RegisterDevice(userId, new RegisterDeviceRequest(
                $"{agent.Name} Server Agent",
                agent.Identity.ExportEncryptionPublicKey(),
                agent.Identity.ExportSigningPublicKey())))
                throw new InvalidOperationException($"{agent.Name} özel anahtarı kayıtlı cihaz anahtarıyla uyuşmuyor.");
        }
    }

    private static (DeviceIdentity Gemini, DeviceIdentity Groq) CreateDevices() =>
        (DeviceIdentity.Create(), DeviceIdentity.Create());

    private sealed record PrivatePair(string Encryption, string Signing);
    private sealed record PrivateKeys(PrivatePair Gemini, PrivatePair Groq);

    private static (DeviceIdentity Gemini, DeviceIdentity Groq) LoadOrCreateDevices(string path)
    {
        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<PrivateKeys>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Agent anahtar dosyası boş.");
            return (Import(data.Gemini), Import(data.Groq));
        }

        var devices = CreateDevices();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var dataToSave = new PrivateKeys(Export(devices.Gemini), Export(devices.Groq));
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (OperatingSystem.IsLinux()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, fileOptions)) JsonSerializer.Serialize(stream, dataToSave);
        return devices;
    }

    private static PrivatePair Export(DeviceIdentity identity)
    {
        var encryption = identity.ExportEncryptionPrivateKey();
        var signing = identity.ExportSigningPrivateKey();
        try { return new PrivatePair(Convert.ToBase64String(encryption), Convert.ToBase64String(signing)); }
        finally
        {
            CryptographicOperations.ZeroMemory(encryption);
            CryptographicOperations.ZeroMemory(signing);
        }
    }

    private static DeviceIdentity Import(PrivatePair pair)
    {
        var encryption = Convert.FromBase64String(pair.Encryption);
        var signing = Convert.FromBase64String(pair.Signing);
        try { return DeviceIdentity.Import(encryption, signing); }
        finally
        {
            CryptographicOperations.ZeroMemory(encryption);
            CryptographicOperations.ZeroMemory(signing);
        }
    }

    public IReadOnlyDictionary<Guid, AgentIdentity> All => _identities;
    public AgentIdentity Get(Guid userId) => _identities[userId];

    public void Dispose()
    {
        foreach (var agent in _identities.Values) agent.Identity.Dispose();
    }
}

public sealed record AgentIdentity(string Name, DeviceIdentity Identity);
