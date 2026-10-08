using System.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;

namespace MTKChat.Server.Agents;

public sealed class AgentWorker(
    ChatState state,
    AgentIdentityRegistry identities,
    IEnumerable<IAgentProvider> providerList,
    AgentRateLimiter rateLimiter,
    IOptions<AgentOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IAgentProvider> _providers = providerList.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(Guid ConversationId, Guid AgentId), CancellationTokenSource> _activeWork = new();
    private readonly ConcurrentDictionary<Guid, AgentMode> _agentModes = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in state.NewMessages.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Agent message processing failed for {MessageId}", message.Id);
            }
        }
    }

    private async Task ProcessAsync(StoredMessage message, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || message.Kind != "text" || !state.IsMessageActive(message.Id)) return;
        var senderDevice = state.GetDevice(message.SenderId);
        if (senderDevice is null) return;

        string? text = null;
        foreach (var (agentId, agent) in identities.All)
        {
            var payload = message.Payloads.FirstOrDefault(item => item.RecipientId == agentId);
            if (payload is null) continue;
            try
            {
                var plaintext = MessageCryptography.Decrypt(
                    payload,
                    message.ClientMessageId,
                    message.ConversationId,
                    message.SenderId,
                    message.CreatedAt,
                    agent.Identity.EncryptionKey,
                    senderDevice.SigningPublicKey);
                text ??= Encoding.UTF8.GetString(plaintext);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
                // A bot is read only after its own envelope was successfully decrypted;
                // provider availability/presence alone never fabricates a receipt.
                state.AcknowledgeMessages(agentId, [message.Id], read: true);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Another agent envelope may be the first usable one.
            }
        }

        if (state.GetUser(message.SenderId)?.IsAgent != false || string.IsNullOrWhiteSpace(text)) return;
        var command = AgentCommand.Parse(text);
        if (command is null) return;
        if (ApplyControlCommand(message.ConversationId, command.Kind))
        {
            switch (command.Kind)
            {
                case AgentCommandKind.StopGemini:
                    StopActiveWork(message.ConversationId, AgentIdentityRegistry.GeminiId);
                    StopActiveWork(message.ConversationId, Guid.Empty);
                    break;
                case AgentCommandKind.StopGroq:
                    StopActiveWork(message.ConversationId, AgentIdentityRegistry.GroqId);
                    StopActiveWork(message.ConversationId, Guid.Empty);
                    break;
                case AgentCommandKind.StopAll:
                    StopActiveWork(message.ConversationId);
                    break;
            }
            return;
        }
        if (!rateLimiter.TryAcquire(message.SenderId))
        {
            await SendAsync(AgentIdentityRegistry.GeminiId, message.ConversationId,
                "Agent kullanım kotası doldu. Bir saatlik pencere yenilendiğinde tekrar deneyebilirsin.", cancellationToken);
            return;
        }

        if (command.Kind == AgentCommandKind.Roundtable)
        {
            _agentModes[message.ConversationId] = AgentMode.All;
            StopActiveWork(message.ConversationId);
            StartWork(message.ConversationId, Guid.Empty, cancellationToken,
                token => RunRoundtableAsync(message.Id, message.ConversationId, command.Prompt,
                    command.FirstSpeakerId ?? AgentIdentityRegistry.GeminiId, token));
            return;
        }

        if (command.Kind is AgentCommandKind.GeminiOnly or AgentCommandKind.GroqOnly)
        {
            _agentModes[message.ConversationId] = command.Kind == AgentCommandKind.GeminiOnly
                ? AgentMode.GeminiOnly
                : AgentMode.GroqOnly;
        }

        var targets = ResolveTargets(message.ConversationId, command.Kind);
        if (targets.Count == 0) return;
        StopActiveWork(message.ConversationId);
        foreach (var target in targets)
            StartWork(message.ConversationId, target, cancellationToken,
                token => GenerateAndSendAsync(message.Id, target, message.ConversationId, command.Prompt, token));
    }

    private bool ApplyControlCommand(Guid conversationId, AgentCommandKind kind)
    {
        switch (kind)
        {
            case AgentCommandKind.StopGemini:
                _agentModes.AddOrUpdate(conversationId,
                    _ => new AgentMode(false, true),
                    (_, current) => current with { GeminiEnabled = false });
                return true;
            case AgentCommandKind.StopGroq:
                _agentModes.AddOrUpdate(conversationId,
                    _ => new AgentMode(true, false),
                    (_, current) => current with { GroqEnabled = false });
                return true;
            case AgentCommandKind.StopAll:
                _agentModes[conversationId] = AgentMode.None;
                return true;
            case AgentCommandKind.EnableAll:
                _agentModes[conversationId] = AgentMode.All;
                return true;
            case AgentCommandKind.EnableGemini:
                _agentModes.AddOrUpdate(conversationId,
                    _ => AgentMode.GeminiOnly,
                    (_, current) => current with { GeminiEnabled = true });
                return true;
            case AgentCommandKind.EnableGroq:
                _agentModes.AddOrUpdate(conversationId,
                    _ => AgentMode.GroqOnly,
                    (_, current) => current with { GroqEnabled = true });
                return true;
            default:
                return false;
        }
    }

    private IReadOnlyList<Guid> ResolveTargets(Guid conversationId, AgentCommandKind kind)
    {
        if (kind is AgentCommandKind.Gemini or AgentCommandKind.GeminiOnly)
            return new[] { AgentIdentityRegistry.GeminiId };
        if (kind is AgentCommandKind.Groq or AgentCommandKind.GroqOnly)
            return new[] { AgentIdentityRegistry.GroqId };
        if (kind == AgentCommandKind.ExplicitAll)
            return new[] { AgentIdentityRegistry.GeminiId, AgentIdentityRegistry.GroqId };

        var mode = _agentModes.GetValueOrDefault(conversationId, AgentMode.All);
        var targets = new List<Guid>(2);
        if (mode.GeminiEnabled) targets.Add(AgentIdentityRegistry.GeminiId);
        if (mode.GroqEnabled) targets.Add(AgentIdentityRegistry.GroqId);
        return targets;
    }

    private void StartWork(Guid conversationId, Guid agentId, CancellationToken hostToken, Func<CancellationToken, Task> work)
    {
        var key = (conversationId, agentId);
        StopActiveWork(conversationId, agentId);
        var session = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        _activeWork[key] = session;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(session.Token);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                logger.LogInformation("Agent work stopped for conversation {ConversationId}", conversationId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Agent work failed for conversation {ConversationId}", conversationId);
            }
            finally
            {
                if (_activeWork.TryGetValue(key, out var active) && ReferenceEquals(active, session))
                    _activeWork.TryRemove(key, out _);
                session.Dispose();
            }
        }, CancellationToken.None);
    }

    private void StopActiveWork(Guid conversationId, Guid? agentId = null)
    {
        foreach (var key in _activeWork.Keys.Where(key => key.ConversationId == conversationId &&
                     (agentId is null || key.AgentId == agentId.Value)).ToArray())
        {
            if (!_activeWork.TryRemove(key, out var session)) continue;
            try { session.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RunRoundtableAsync(Guid sourceMessageId, Guid conversationId, string topic,
        Guid firstSpeakerId, CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();
        var turns = Math.Clamp(options.Value.MaxRoundtableTurns, 2, 12);
        for (var turn = 0; turn < turns; turn++)
        {
            if (!state.IsMessageActive(sourceMessageId)) return;
            var agentId = turn % 2 == 0 ? firstSpeakerId
                : firstSpeakerId == AgentIdentityRegistry.GeminiId ? AgentIdentityRegistry.GroqId : AgentIdentityRegistry.GeminiId;
            var agent = identities.Get(agentId);
            var otherName = agentId == AgentIdentityRegistry.GeminiId ? "Groq" : "Gemini";
            var prompt = $"""
                Sen {agent.Name} agentısın. MTK Chat bu yanıtını {otherName} agentına bir sonraki turda aktaracak.
                Doğrudan sağlayıcı API bağlantınız yok; uygulama aranızdaki metinleri sırayla iletir. Bu yüzden
                "onunla konuşamam" diye yanıt verme, kullanıcıya ayrı bir yardım metni yazma. {otherName} agentına
                hitap ederek doğal ve somut bir sohbet sürdür. İlk turdaysan kısa bir giriş ve açık bir soru sor;
                sonraki turdaysan aşağıdaki son agent mesajındaki fikre doğrudan karşılık ver.
                Her turu 2-5 tamamlanmış cümleyle sınırla. Tur {turn + 1}/{turns}.
                Kullanıcının isteği: {topic}

                Önceki agent mesajları:
                {(transcript.Length == 0 ? "Henüz mesaj yok." : transcript.ToString())}
                """;
            var answer = await CompleteSafelyAsync(agent.Name, prompt, cancellationToken);
            if (!state.IsMessageActive(sourceMessageId)) return;
            transcript.AppendLine().Append(agent.Name).Append(": ").AppendLine(answer);
            if (transcript.Length > 12_000) transcript.Remove(0, transcript.Length - 12_000);
            await SendAsync(agentId, conversationId, answer, cancellationToken);
        }
    }

    private async Task GenerateAndSendAsync(Guid sourceMessageId, Guid agentId, Guid conversationId, string prompt, CancellationToken cancellationToken)
    {
        if (!state.IsMessageActive(sourceMessageId)) return;
        var agent = identities.Get(agentId);
        var answer = await CompleteSafelyAsync(agent.Name, prompt, cancellationToken);
        if (!state.IsMessageActive(sourceMessageId)) return;
        await SendAsync(agentId, conversationId, answer, cancellationToken);
    }

    private async Task<string> CompleteSafelyAsync(string providerName, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return await _providers[providerName].CompleteAsync(prompt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning("{Provider} agent call failed: {ErrorType}", providerName, exception.GetType().Name);
            return $"Şu anda yanıt üretemiyorum: {exception.Message}";
        }
    }

    private Task SendAsync(Guid agentId, Guid conversationId, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var agent = identities.Get(agentId);
        var messageId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            var payloads = state.GetMembers(conversationId)
                .Select(recipientId => (RecipientId: recipientId, Device: state.GetDevice(recipientId)))
                .Where(item => item.Device is not null)
                .Select(item => MessageCryptography.Encrypt(
                    bytes,
                    messageId,
                    conversationId,
                    agentId,
                    item.RecipientId,
                    createdAt,
                    item.Device!.EncryptionPublicKey,
                    agent.Identity.SigningKey))
                .ToArray();
            state.AddMessage(agentId, new SendMessageRequest(
                messageId,
                conversationId,
                "text",
                createdAt,
                null,
                payloads,
                null), out _);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
        return Task.CompletedTask;
    }

    private readonly record struct AgentMode(bool GeminiEnabled, bool GroqEnabled)
    {
        public static AgentMode All => new(true, true);
        public static AgentMode None => new(false, false);
        public static AgentMode GeminiOnly => new(true, false);
        public static AgentMode GroqOnly => new(false, true);
    }

    private enum AgentCommandKind
    {
        Gemini,
        Groq,
        All,
        ExplicitAll,
        GeminiOnly,
        GroqOnly,
        Roundtable,
        StopGemini,
        StopGroq,
        StopAll,
        EnableAll,
        EnableGemini,
        EnableGroq
    }

    private sealed record AgentCommand(AgentCommandKind Kind, string Prompt, Guid? FirstSpeakerId = null)
    {
        public static AgentCommand? Parse(string input)
        {
            var trimmed = input.Trim();
            var normalized = trimmed.ToLower(CultureInfo.GetCultureInfo("tr-TR"))
                .Replace("ı", "i", StringComparison.Ordinal)
                .Replace("ş", "s", StringComparison.Ordinal)
                .Replace("ç", "c", StringComparison.Ordinal)
                .Replace("ğ", "g", StringComparison.Ordinal)
                .Replace("ü", "u", StringComparison.Ordinal)
                .Replace("ö", "o", StringComparison.Ordinal);
            var mentionsGemini = Regex.IsMatch(normalized, @"\bgemini\b", RegexOptions.CultureInvariant);
            var mentionsGroq = Regex.IsMatch(normalized, @"\bgroq\b", RegexOptions.CultureInvariant);
            var mentionsAll = normalized.Contains("ikiniz", StringComparison.Ordinal) ||
                              normalized.Contains("agentlar", StringComparison.Ordinal) ||
                              (mentionsGemini && mentionsGroq);
            var stopIntent = Regex.IsMatch(normalized, @"\b(dur|durun|sus|susun)\b", RegexOptions.CultureInvariant) ||
                             normalized.Contains("sessiz kal", StringComparison.Ordinal) ||
                             normalized.Contains("konusma", StringComparison.Ordinal);
            if (stopIntent && mentionsAll)
                return new AgentCommand(AgentCommandKind.StopAll, string.Empty);
            if (stopIntent && mentionsGemini)
                return new AgentCommand(AgentCommandKind.StopGemini, string.Empty);
            if (stopIntent && mentionsGroq)
                return new AgentCommand(AgentCommandKind.StopGroq, string.Empty);

            var mappings = new[]
            {
                (Prefix: "@gemini", Kind: AgentCommandKind.Gemini),
                (Prefix: "@groq", Kind: AgentCommandKind.Groq),
                (Prefix: "@agents", Kind: AgentCommandKind.ExplicitAll),
                (Prefix: "/roundtable", Kind: AgentCommandKind.Roundtable)
            };
            foreach (var mapping in mappings)
            {
                if (!trimmed.StartsWith(mapping.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var prompt = trimmed[mapping.Prefix.Length..].Trim();
                return prompt.Length == 0 ? null : new AgentCommand(mapping.Kind, prompt);
            }
            if (normalized.Contains("birbirinizle sohbet", StringComparison.Ordinal) ||
                normalized.Contains("kendi aranizda konus", StringComparison.Ordinal) ||
                normalized.Contains("birbirinizle konus", StringComparison.Ordinal) ||
                normalized.Contains("birbirinizle tartis", StringComparison.Ordinal))
                return new AgentCommand(AgentCommandKind.Roundtable, trimmed);

            var firstSpeaker = AgentConversationIntent.FirstSpeakerFor(trimmed);
            if (firstSpeaker is not null)
                return new AgentCommand(AgentCommandKind.Roundtable, trimmed, firstSpeaker);

            var onlyIntent = normalized.Contains("sadece", StringComparison.Ordinal) ||
                             normalized.Contains("yalnizca", StringComparison.Ordinal) ||
                             normalized.Contains("tek sen", StringComparison.Ordinal);
            if (onlyIntent && mentionsGemini && !mentionsGroq)
                return new AgentCommand(AgentCommandKind.GeminiOnly, RemoveLeadingAgentName(trimmed, "gemini"));
            if (onlyIntent && mentionsGroq && !mentionsGemini)
                return new AgentCommand(AgentCommandKind.GroqOnly, RemoveLeadingAgentName(trimmed, "groq"));

            var enableAll = mentionsAll && !stopIntent &&
                            (normalized.Contains("konusun", StringComparison.Ordinal) ||
                             normalized.Contains("devam", StringComparison.Ordinal) ||
                             normalized.Contains("aktif", StringComparison.Ordinal));
            if (enableAll)
                return new AgentCommand(AgentCommandKind.EnableAll, string.Empty);

            var enableNamed = normalized.Contains("devam et", StringComparison.Ordinal) ||
                              normalized.Contains("tekrar konus", StringComparison.Ordinal) ||
                              normalized.Contains("konusabilirsin", StringComparison.Ordinal) ||
                              normalized.Contains("aktif ol", StringComparison.Ordinal);
            if (enableNamed && mentionsGemini && !mentionsGroq)
                return new AgentCommand(AgentCommandKind.EnableGemini, string.Empty);
            if (enableNamed && mentionsGroq && !mentionsGemini)
                return new AgentCommand(AgentCommandKind.EnableGroq, string.Empty);

            if (normalized.StartsWith("gemini ", StringComparison.Ordinal))
                return new AgentCommand(AgentCommandKind.Gemini, RemoveLeadingAgentName(trimmed, "gemini"));
            if (normalized.StartsWith("groq ", StringComparison.Ordinal))
                return new AgentCommand(AgentCommandKind.Groq, RemoveLeadingAgentName(trimmed, "groq"));

            return trimmed.Length == 0 ? null : new AgentCommand(AgentCommandKind.All, trimmed);
        }

        private static string RemoveLeadingAgentName(string input, string agentName)
        {
            if (!input.StartsWith(agentName, StringComparison.OrdinalIgnoreCase)) return input;
            var prompt = input[agentName.Length..].TrimStart(' ', ',', ':', '-');
            return string.IsNullOrWhiteSpace(prompt) ? "Kısaca hazır olduğunu belirt." : prompt;
        }
    }
}
