using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MTKChat.Desktop;

// Personal preferences contain IDs and mute deadlines only. Message bodies/media are never copied to
// this file; Windows DPAPI binds it to this Windows account and chat account.
internal sealed class LocalChatPreferences
{
    private const int MaximumEntries = 20000;
    private readonly string? _path;
    private HashSet<Guid> _favorites = [];
    private HashSet<StarReference> _stars = [];
    private HashSet<Guid> _archived = [];
    private Dictionary<Guid, DateTimeOffset?> _mutes = [];
    private HashSet<Guid> _pins = [];
    internal Guid UserId { get; }
    internal bool IsPersistent => _path is not null;

    private LocalChatPreferences(Guid userId, string? path)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A chat account is required.", nameof(userId));
        UserId = userId;
        _path = path;
    }

    internal static LocalChatPreferences Memory(Guid userId) => new(userId, null);

    internal static LocalChatPreferences Open(Guid userId, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MTKChat", "Preferences");
        var store = new LocalChatPreferences(userId, Path.Combine(directory, userId.ToString("N") + ".dat"));
        if (!File.Exists(store._path)) return store;
        if (new FileInfo(store._path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Personal preferences exceed the supported size.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(store._path), store.Entropy(), DataProtectionScope.CurrentUser);
        try
        {
            var data = JsonSerializer.Deserialize<PreferenceData>(bytes)
                ?? throw new InvalidDataException("Personal preferences are empty.");
            // Keep the existing account-bound entropy so version-1 favorites and
            // stars migrate without resetting them. Versions 2/3 add personal
            // archive, mute and pin metadata, never membership or message bodies.
            // Pins is optional so an older account has no invented pinned rooms.
            if (data.Version is not (1 or 2 or 3) || data.UserId != userId || data.Favorites is null || data.Stars is null ||
                data.Version >= 2 && (data.Archived is null || data.Mutes is null) ||
                (long)data.Favorites.Length + data.Stars.Length + (data.Archived?.Length ?? 0) +
                    (data.Mutes?.Length ?? 0) + (data.Pins?.Length ?? 0) > MaximumEntries ||
                data.Favorites.Any(id => id == Guid.Empty) || data.Stars.Any(s => s is null || s.RoomId == Guid.Empty || s.MessageId == Guid.Empty) ||
                data.Archived?.Any(id => id == Guid.Empty) == true ||
                data.Pins?.Any(id => id == Guid.Empty) == true ||
                data.Pins is { } pins && pins.Distinct().Count() != pins.Length ||
                data.Mutes?.Any(mute => mute is null || mute.RoomId == Guid.Empty || mute.Until == DateTimeOffset.MinValue) == true ||
                data.Mutes is { } mutes && mutes.Select(mute => mute.RoomId).Distinct().Count() != mutes.Length)
                throw new InvalidDataException("Personal preferences are invalid or belong to another account.");
            store._favorites = data.Favorites.ToHashSet();
            store._stars = data.Stars.ToHashSet();
            store._archived = data.Archived?.ToHashSet() ?? [];
            store._mutes = data.Mutes?.ToDictionary(mute => mute.RoomId, mute => mute.Until) ?? [];
            store._pins = data.Pins?.ToHashSet() ?? [];
            return store;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal bool IsFavorite(Guid roomId) => _favorites.Contains(roomId);
    internal bool IsStarred(Guid roomId, Guid messageId) => _stars.Contains(new(roomId, messageId));
    internal IReadOnlySet<Guid> StarredIds(Guid roomId) => _stars.Where(s => s.RoomId == roomId).Select(s => s.MessageId).ToHashSet();
    internal bool IsArchived(Guid roomId) => _archived.Contains(roomId);
    internal bool IsPinned(Guid roomId) => _pins.Contains(roomId);
    internal bool IsMuted(Guid roomId, DateTimeOffset? now = null) =>
        _mutes.TryGetValue(roomId, out var until) && (until is null || (now ?? DateTimeOffset.UtcNow) < until);
    internal DateTimeOffset? MuteUntil(Guid roomId) => _mutes.GetValueOrDefault(roomId);

    internal void SetPinned(Guid roomId, bool pinned)
    {
        Validate(roomId);
        if (IsPinned(roomId) == pinned) return;
        var candidate = new HashSet<Guid>(_pins);
        if (pinned) candidate.Add(roomId); else candidate.Remove(roomId);
        Save(_favorites, _stars, _archived, _mutes, candidate);
        _pins = candidate;
    }

    internal void SetArchived(Guid roomId, bool archived)
    {
        Validate(roomId);
        if (IsArchived(roomId) == archived) return;
        var candidate = new HashSet<Guid>(_archived);
        if (archived) candidate.Add(roomId); else candidate.Remove(roomId);
        Save(_favorites, _stars, candidate, _mutes, _pins);
        _archived = candidate;
    }

    internal void SetMute(Guid roomId, DateTimeOffset? until, DateTimeOffset? now = null)
    {
        Validate(roomId);
        if (until is { } deadline && deadline <= (now ?? DateTimeOffset.UtcNow))
            throw new ArgumentOutOfRangeException(nameof(until), "Susturma süresi gelecekte olmalıdır.");
        if (_mutes.TryGetValue(roomId, out var prior) && prior == until) return;
        var candidate = new Dictionary<Guid, DateTimeOffset?>(_mutes) { [roomId] = until };
        Save(_favorites, _stars, _archived, candidate, _pins);
        _mutes = candidate;
    }

    internal void ClearMute(Guid roomId)
    {
        Validate(roomId);
        if (!_mutes.ContainsKey(roomId)) return;
        var candidate = new Dictionary<Guid, DateTimeOffset?>(_mutes);
        candidate.Remove(roomId);
        Save(_favorites, _stars, _archived, candidate, _pins);
        _mutes = candidate;
    }

    internal void SetFavorite(Guid roomId, bool favorite)
    {
        Validate(roomId);
        if (IsFavorite(roomId) == favorite) return;
        var candidate = new HashSet<Guid>(_favorites);
        if (favorite) candidate.Add(roomId); else candidate.Remove(roomId);
        Save(candidate, _stars, _archived, _mutes, _pins);
        _favorites = candidate;
    }

    internal void SetStarred(Guid roomId, Guid messageId, bool starred)
    {
        Validate(roomId); Validate(messageId);
        if (IsStarred(roomId, messageId) == starred) return;
        var candidate = new HashSet<StarReference>(_stars);
        if (starred) candidate.Add(new(roomId, messageId)); else candidate.Remove(new(roomId, messageId));
        Save(_favorites, candidate, _archived, _mutes, _pins);
        _stars = candidate;
    }

    private void Save(HashSet<Guid> favorites, HashSet<StarReference> stars,
        HashSet<Guid> archived, Dictionary<Guid, DateTimeOffset?> mutes, HashSet<Guid> pins)
    {
        if ((long)favorites.Count + stars.Count + archived.Count + mutes.Count + pins.Count > MaximumEntries)
            throw new InvalidOperationException("Kişisel tercihler için depolama sınırına ulaşıldı.");
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PreferenceData(3, UserId, favorites.ToArray(), stars.ToArray(),
            archived.ToArray(), mutes.Select(pair => new MuteReference(pair.Key, pair.Value)).ToArray(), pins.ToArray()));
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy(), DataProtectionScope.CurrentUser);
            // Write-through + atomic replacement preserves the old complete file on
            // write failure. In-memory state commits only after this succeeds.
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                file.Write(protectedBytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private byte[] Entropy() => Encoding.UTF8.GetBytes("MTKChat.PersonalPreferences.v1." + UserId.ToString("N"));
    private static void Validate(Guid id) { if (id == Guid.Empty) throw new ArgumentException("An empty identifier is not valid."); }
    private sealed record PreferenceData(int Version, Guid UserId, Guid[] Favorites, StarReference[] Stars,
        Guid[]? Archived = null, MuteReference[]? Mutes = null, Guid[]? Pins = null);
    private sealed record StarReference(Guid RoomId, Guid MessageId);
    private sealed record MuteReference(Guid RoomId, DateTimeOffset? Until);
}
