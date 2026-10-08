namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    private sealed record MemoryFile(Guid Owner, Guid Room, Guid Client, byte[] Cipher, DateTimeOffset CreatedAt);
    private readonly Dictionary<string, MemoryFile> _memoryFiles = new();

    public string? UploadEncryptedFile(Guid senderId, Guid roomId, Guid clientMessageId, byte[] ciphertext)
    {
        lock (_gate)
        {
            if (clientMessageId == Guid.Empty || ciphertext.Length is < 1 or > 5_242_880 || IsBanned(senderId) || IsMuted(senderId, out _) ||
                !_users.TryGetValue(senderId, out var user) || user.IsAgent ||
                !_conversations.TryGetValue(roomId, out var room) || !room.MemberIds.Contains(senderId) ||
                IsChatWriteRestricted(roomId, senderId, out _)) return null;
            if (room.Kind == "direct" && room.MemberIds.Any(id => id != senderId &&
                (IsBlockedUnsafe(senderId, id) || IsBlockedUnsafe(id, senderId)))) return null;
            if (_database is not null) return _database.SaveEncryptedFile(senderId, roomId, clientMessageId, ciphertext);
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            _memoryFiles[token] = new(senderId, roomId, clientMessageId, ciphertext.ToArray(), DateTimeOffset.UtcNow);
            return token;
        }
    }

    public byte[]? DownloadEncryptedFile(Guid userId, string token)
    {
        lock (_gate)
        {
            if (token.Length != 48 || IsBanned(userId)) return null;
            var message = _messages.Values.FirstOrDefault(m => m.Attachment?.StorageToken == token);
            if (message is null || message.DeletedForEveryone ||
                message.ExpiresAt is not null && message.ExpiresAt <= DateTimeOffset.UtcNow ||
                !_conversations.TryGetValue(message.ConversationId, out var room) || !room.MemberIds.Contains(userId) ||
                _hiddenMessages.Contains((userId, message.Id)) || IsBlockedUnsafe(userId, message.SenderId) ||
                !message.Payloads.Any(p => p.RecipientId == userId)) return null;
            return _database is not null ? _database.LoadEncryptedFile(token) : _memoryFiles.GetValueOrDefault(token)?.Cipher.ToArray();
        }
    }

    private bool OwnsUploadedFileUnsafe(string token, Guid sender, Guid room, Guid client, long size)
    {
        if (token.Length != 48 || !token.All(Uri.IsHexDigit)) return false;
        return _database is not null ? _database.IsFileOwned(token, sender, room, client, size) :
            _memoryFiles.TryGetValue(token, out var file) && file.Owner == sender && file.Room == room &&
            file.Client == client && file.Cipher.LongLength == size && !_messages.Values.Any(m => m.Attachment?.StorageToken == token);
    }

    private void DeleteEncryptedFilesUnsafe(IEnumerable<string> tokens)
    {
        if (_database is not null) _database.DeleteEncryptedFiles(tokens);
        else foreach (var token in tokens)
            if (_memoryFiles.Remove(token, out var file)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(file.Cipher);
    }
}
