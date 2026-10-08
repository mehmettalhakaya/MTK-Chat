using System.Security.Cryptography;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Server.Services;

public enum StatusResult { Success, Invalid, Forbidden, NotFound, CapacityReached, ReplayConflict }

public sealed partial class ChatState
{
    private const int MaximumRetainedStatuses = 500;
    private readonly Dictionary<Guid, StatusData> _statuses = new();
    private readonly Dictionary<(Guid SenderId, Guid ClientStatusId), Guid> _clientStatusIds = new();

    // Deleted rows are content-free tombstones until the ORIGINAL deadline. A
    // retried upload must never resurrect a deleted status or extend its lifetime.
    private sealed record StatusData(Guid Id, Guid ClientStatusId, Guid SenderId, string Kind,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Ciphertext,
        EncryptedPayload[] Payloads, bool Deleted = false);

    public StatusResult AddStatus(Guid senderId, SendStatusRequest? request, out StoredStatus? result)
    {
        lock (_gate)
        {
            result = null;
            if (!StatusActorUnsafe(senderId)) return StatusResult.Forbidden;
            if (!ValidStatusShape(request)) return StatusResult.Invalid;
            var payloads = request!.Payloads;
            if (payloads.Count is < 2 or > StatusProtocol.MaximumRecipients ||
                payloads.Select(p => p.RecipientId).Distinct().Count() != payloads.Count ||
                !payloads.Any(p => p.RecipientId == senderId)) return StatusResult.Invalid;
            // Recipient ids are the audience: no directory-wide/default audience
            // inference, bot recipients, blocked relation or site-admin exception.
            if (payloads.Any(p => !StatusAudienceUnsafe(senderId, p.RecipientId))) return StatusResult.Forbidden;
            if (!_devicesByUser.TryGetValue(senderId, out var senderDevice) || !ValidStatusDevice(senderDevice))
                return StatusResult.Invalid;
            try
            {
                foreach (var payload in payloads)
                    MessageCryptography.VerifyEnvelope(payload, request.ClientStatusId, StatusProtocol.ScopeId,
                        senderId, request.CreatedAt, senderDevice.SigningPublicKey);
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
            { return StatusResult.Invalid; }

            var now = _time.GetUtcNow();
            if (_clientStatusIds.TryGetValue((senderId, request.ClientStatusId), out var existingId))
            {
                var existing = _statuses[existingId];
                if (existing.Deleted || existing.ExpiresAt <= now) return StatusResult.NotFound;
                if (existing.Kind != request.Kind || existing.CreatedAt != request.CreatedAt ||
                    existing.Ciphertext != request.Ciphertext || !existing.Payloads.SequenceEqual(payloads))
                    return StatusResult.ReplayConflict;
                result = StatusViewUnsafe(existing, senderId);
                return StatusResult.Success;
            }
            if (request.CreatedAt == default || (request.CreatedAt - now).Duration() > TimeSpan.FromMinutes(5) ||
                now > DateTimeOffset.MaxValue.AddHours(-24))
                return StatusResult.Invalid;
            var active = _statuses.Values.Where(item => !item.Deleted && item.ExpiresAt > now).ToArray();
            if (active.Count(item => item.SenderId == senderId) >= StatusProtocol.MaximumActivePerSender ||
                active.Length >= StatusProtocol.MaximumActiveStatuses || _statuses.Count >= MaximumRetainedStatuses ||
                active.Sum(StatusStorageCharacters) + StatusStorageCharacters(request.Ciphertext, payloads) >
                    (long)StatusProtocol.MaximumStoredBodyBytes * 4 / 3)
                return StatusResult.CapacityReached;
            var created = new StatusData(Guid.NewGuid(), request.ClientStatusId, senderId, request.Kind,
                request.CreatedAt, now.AddHours(24), request.Ciphertext, payloads.ToArray());
            _statuses.Add(created.Id, created);
            _clientStatusIds.Add((senderId, request.ClientStatusId), created.Id);
            try { PersistUnsafe(); }
            catch
            {
                _statuses.Remove(created.Id);
                _clientStatusIds.Remove((senderId, request.ClientStatusId));
                throw;
            }
            result = StatusViewUnsafe(created, senderId);
            return StatusResult.Success;
        }
    }

    public IReadOnlyList<StatusSummary> GetStatuses(Guid viewerId)
    {
        lock (_gate)
        {
            if (!StatusActorUnsafe(viewerId)) return [];
            return _statuses.Values.Where(item => CanReadStatusUnsafe(viewerId, item))
                .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id)
                .Select(item => new StatusSummary(item.Id, item.ClientStatusId, item.SenderId, item.Kind,
                    item.CreatedAt, item.ExpiresAt)).ToArray();
        }
    }

    public StoredStatus? GetStatus(Guid viewerId, Guid statusId)
    {
        lock (_gate)
            return _statuses.TryGetValue(statusId, out var status) && CanReadStatusUnsafe(viewerId, status)
                ? StatusViewUnsafe(status, viewerId) : null;
    }

    public StatusResult DeleteStatus(Guid actorId, Guid statusId)
    {
        lock (_gate)
        {
            if (!StatusActorUnsafe(actorId)) return StatusResult.Forbidden;
            if (!_statuses.TryGetValue(statusId, out var original) || original.SenderId != actorId ||
                original.ExpiresAt <= _time.GetUtcNow()) return StatusResult.NotFound;
            if (original.Deleted) return StatusResult.Success;
            _statuses[statusId] = original with { Deleted = true, Ciphertext = "", Payloads = [] };
            try { PersistUnsafe(); }
            catch { _statuses[statusId] = original; throw; }
            return StatusResult.Success;
        }
    }

    public int RemoveExpiredStatuses()
    {
        lock (_gate)
        {
            var expired = _statuses.Values.Where(item => item.ExpiresAt <= _time.GetUtcNow()).ToArray();
            if (expired.Length == 0) return 0;
            foreach (var row in expired)
            {
                _statuses.Remove(row.Id);
                _clientStatusIds.Remove((row.SenderId, row.ClientStatusId));
            }
            try { PersistUnsafe(); }
            catch
            {
                foreach (var row in expired)
                {
                    _statuses.Add(row.Id, row);
                    _clientStatusIds.Add((row.SenderId, row.ClientStatusId), row.Id);
                }
                throw;
            }
            return expired.Length;
        }
    }

    private bool StatusActorUnsafe(Guid id) => _users.TryGetValue(id, out var user) &&
        !user.IsAgent && !_bannedUsers.Contains(id);

    private bool StatusAudienceUnsafe(Guid author, Guid viewer) => StatusActorUnsafe(viewer) &&
        !IsBlockedUnsafe(author, viewer) && !IsBlockedUnsafe(viewer, author) &&
        _devicesByUser.TryGetValue(viewer, out var device) && ValidStatusDevice(device);

    private bool CanReadStatusUnsafe(Guid viewer, StatusData status) => !status.Deleted &&
        status.ExpiresAt > _time.GetUtcNow() && StatusActorUnsafe(status.SenderId) && StatusActorUnsafe(viewer) &&
        !IsBlockedUnsafe(status.SenderId, viewer) && !IsBlockedUnsafe(viewer, status.SenderId) &&
        _devicesByUser.ContainsKey(viewer) && status.Payloads.Any(p => p.RecipientId == viewer);

    // Count all encrypted strings, not just the shared media body, so malicious
    // maximum-size recipient envelopes cannot evade the retained-memory budget.
    private static long StatusStorageCharacters(StatusData row) => StatusStorageCharacters(row.Ciphertext, row.Payloads);
    private static long StatusStorageCharacters(string body, IReadOnlyList<EncryptedPayload> payloads) => body.Length +
        payloads.Sum(p => (long)p.Ciphertext.Length + p.EphemeralPublicKey.Length + p.Nonce.Length + p.Tag.Length + p.Signature.Length);

    private static StoredStatus StatusViewUnsafe(StatusData status, Guid viewer) => new(status.Id,
        status.ClientStatusId, StatusProtocol.ScopeId, status.SenderId, status.Kind, status.CreatedAt,
        status.ExpiresAt, status.Ciphertext, status.Payloads.Single(p => p.RecipientId == viewer));

    private static bool ValidStatusShape(SendStatusRequest? request) => request is not null &&
        request.ClientStatusId != Guid.Empty && StatusProtocol.IsKind(request.Kind) && request.Payloads is not null &&
        StatusBase64(request.Ciphertext, 1, StatusProtocol.MaximumBodyBytes) &&
        request.Payloads.Count <= StatusProtocol.MaximumRecipients && request.Payloads.All(payload => payload is not null &&
            payload.RecipientId != Guid.Empty && payload.Algorithm == MessageCryptography.Algorithm &&
            StatusBase64(payload.EphemeralPublicKey, 80, 128) && ValidStatusEphemeralKey(payload.EphemeralPublicKey) &&
            StatusBase64(payload.Nonce, 12, 12) && StatusBase64(payload.Tag, 16, 16) &&
            StatusBase64(payload.Signature, 64, 72) && StatusBase64(payload.Ciphertext, 1, StatusProtocol.MaximumEnvelopeBytes));

    private static bool StatusBase64(string? text, int minimum, int maximum)
    {
        if (string.IsNullOrEmpty(text) || text.Length > ((maximum + 2) / 3) * 4) return false;
        try { var bytes = Convert.FromBase64String(text); return bytes.Length >= minimum && bytes.Length <= maximum; }
        catch (FormatException) { return false; }
    }

    private static bool ValidStatusEphemeralKey(string key)
    {
        try
        {
            using var encryption = ECDiffieHellman.Create();
            var bytes = Convert.FromBase64String(key); encryption.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && encryption.KeySize == 256 &&
                encryption.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    private static bool ValidStatusDevice(DeviceKeyBundle device)
    {
        if (!StatusBase64(device.EncryptionPublicKey, 80, 128) || !StatusBase64(device.SigningPublicKey, 80, 128) ||
            !ValidStatusEphemeralKey(device.EncryptionPublicKey)) return false;
        try
        {
            using var signing = ECDsa.Create();
            var bytes = Convert.FromBase64String(device.SigningPublicKey); signing.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && signing.KeySize == 256 &&
                signing.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    private void RestoreStatusesUnsafe(StatusData[]? rows)
    {
        _statuses.Clear(); _clientStatusIds.Clear();
        long bodyCharacters = 0;
        foreach (var row in rows ?? [])
        {
            if (row is null || row.Payloads is null || row.Ciphertext is null ||
                _statuses.Count >= MaximumRetainedStatuses || row.Id == Guid.Empty || row.ClientStatusId == Guid.Empty ||
                row.SenderId == Guid.Empty || !StatusProtocol.IsKind(row.Kind) || row.CreatedAt == default ||
                row.ExpiresAt <= _time.GetUtcNow() ||
                row.CreatedAt > _time.GetUtcNow() && row.CreatedAt - _time.GetUtcNow() > TimeSpan.FromMinutes(5) ||
                _users.TryGetValue(row.SenderId, out var knownSender) && knownSender.IsAgent ||
                (row.ExpiresAt - row.CreatedAt - TimeSpan.FromHours(24)).Duration() > TimeSpan.FromMinutes(5) ||
                _statuses.ContainsKey(row.Id) || _clientStatusIds.ContainsKey((row.SenderId, row.ClientStatusId))) continue;
            if (row.Deleted)
            {
                if (row.Ciphertext != "" || row.Payloads.Length != 0) continue;
            }
            else
            {
                var request = new SendStatusRequest(row.ClientStatusId, row.Kind, row.CreatedAt, row.Ciphertext, row.Payloads);
                if (!ValidStatusShape(request) || row.Payloads.Length is < 2 or > StatusProtocol.MaximumRecipients ||
                    row.Payloads.Select(p => p.RecipientId).Distinct().Count() != row.Payloads.Length ||
                    !row.Payloads.Any(p => p.RecipientId == row.SenderId) ||
                    !_devicesByUser.TryGetValue(row.SenderId, out var senderDevice) || !ValidStatusDevice(senderDevice) ||
                    row.Payloads.Any(p => !_devicesByUser.TryGetValue(p.RecipientId, out var device) || !ValidStatusDevice(device) ||
                        _users.TryGetValue(p.RecipientId, out var knownViewer) && knownViewer.IsAgent)) continue;
                try
                {
                    foreach (var payload in row.Payloads)
                        MessageCryptography.VerifyEnvelope(payload, row.ClientStatusId, StatusProtocol.ScopeId,
                            row.SenderId, row.CreatedAt, senderDevice.SigningPublicKey);
                }
                catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException) { continue; }
                if (_statuses.Values.Count(s => !s.Deleted) >= StatusProtocol.MaximumActiveStatuses ||
                    _statuses.Values.Count(s => !s.Deleted && s.SenderId == row.SenderId) >= StatusProtocol.MaximumActivePerSender ||
                    bodyCharacters + StatusStorageCharacters(row) > (long)StatusProtocol.MaximumStoredBodyBytes * 4 / 3) continue;
                bodyCharacters += StatusStorageCharacters(row);
            }
            _statuses.Add(row.Id, row); _clientStatusIds.Add((row.SenderId, row.ClientStatusId), row.Id);
        }
    }
}
