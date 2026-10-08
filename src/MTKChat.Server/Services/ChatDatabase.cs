using System.Globalization;
using Microsoft.Extensions.Options;
using MTKChat.Server.Configuration;
using MySqlConnector;

namespace MTKChat.Server.Services;

public sealed record SiteAccount(int SiteId, Guid ChatId, string Username, string Email, string Role, bool IsActive);

public sealed class ChatDatabase
{
    private readonly string? _connectionString;

    public ChatDatabase(IOptions<DatabaseOptions> options)
    {
        if (!string.Equals(options.Value.Provider, "MySQL", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrWhiteSpace(options.Value.ConnectionString))
        {
            _connectionString = options.Value.ConnectionString;
            return;
        }

        // The chat service can reuse the site's local DB_* environment without copying its password.
        var user = Environment.GetEnvironmentVariable("DB_USER");
        var password = Environment.GetEnvironmentVariable("DB_PASS");
        var database = Environment.GetEnvironmentVariable("DB_NAME");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(database)) return;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            Port = uint.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 3306,
            UserID = user,
            Password = password,
            Database = database,
            CharacterSet = "utf8mb4",
            Pooling = true
        }.ConnectionString;
    }

    public bool IsConfigured => _connectionString is not null;

    private MySqlConnection Open()
    {
        if (_connectionString is null) throw new InvalidOperationException("MySQL bağlantısı yapılandırılmadı.");
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_state_snapshot (
                id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
                payload LONGTEXT NOT NULL,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                CONSTRAINT chk_chat_state_one_row CHECK (id = 1)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
            """;
        command.ExecuteNonQuery();
    }

    public void EnsurePhotoSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_profile_photos (
                user_id CHAR(36) NOT NULL PRIMARY KEY,
                version CHAR(64) NOT NULL,
                jpeg MEDIUMBLOB NOT NULL,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """;
        command.ExecuteNonQuery();
    }

    public void EnsureGroupPhotoSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_group_photos (
                conversation_id CHAR(36) NOT NULL PRIMARY KEY,
                version CHAR(64) NOT NULL,
                jpeg MEDIUMBLOB NOT NULL,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """;
        command.ExecuteNonQuery();
    }

    public void EnsureEncryptedFileSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_encrypted_files (
                token CHAR(48) NOT NULL PRIMARY KEY,
                owner_id CHAR(36) NOT NULL,
                conversation_id CHAR(36) NOT NULL,
                client_message_id CHAR(36) NOT NULL,
                message_id CHAR(36) NULL,
                ciphertext MEDIUMBLOB NOT NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """;
        command.ExecuteNonQuery();
    }

    public string SaveEncryptedFile(Guid ownerId, Guid roomId, Guid clientMessageId, byte[] ciphertext)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO chat_encrypted_files(token,owner_id,conversation_id,client_message_id,ciphertext)
            VALUES(@token,@owner,@room,@client,@cipher)
            """;
        command.Parameters.AddWithValue("@token", token);
        command.Parameters.AddWithValue("@owner", ownerId.ToString());
        command.Parameters.AddWithValue("@room", roomId.ToString());
        command.Parameters.AddWithValue("@client", clientMessageId.ToString());
        command.Parameters.AddWithValue("@cipher", ciphertext);
        command.ExecuteNonQuery();
        return token;
    }

    public bool IsFileOwned(string token, Guid ownerId, Guid roomId, Guid clientMessageId, long size)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM chat_encrypted_files WHERE token=@token AND owner_id=@owner
                AND conversation_id=@room AND client_message_id=@client AND message_id IS NULL AND OCTET_LENGTH(ciphertext)=@size)
            """;
        command.Parameters.AddWithValue("@token", token);
        command.Parameters.AddWithValue("@owner", ownerId.ToString());
        command.Parameters.AddWithValue("@room", roomId.ToString());
        command.Parameters.AddWithValue("@client", clientMessageId.ToString());
        command.Parameters.AddWithValue("@size", size);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public bool AttachEncryptedFile(string token, Guid ownerId, Guid roomId, Guid clientMessageId, Guid messageId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE chat_encrypted_files SET message_id=@message
            WHERE token=@token AND owner_id=@owner AND conversation_id=@room AND client_message_id=@client
              AND message_id IS NULL
            """;
        command.Parameters.AddWithValue("@message", messageId.ToString());
        command.Parameters.AddWithValue("@token", token);
        command.Parameters.AddWithValue("@owner", ownerId.ToString());
        command.Parameters.AddWithValue("@room", roomId.ToString());
        command.Parameters.AddWithValue("@client", clientMessageId.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    public byte[]? LoadEncryptedFile(string token)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ciphertext FROM chat_encrypted_files WHERE token=@token";
        command.Parameters.AddWithValue("@token", token);
        return command.ExecuteScalar() as byte[];
    }

    public void DeleteEncryptedFiles(IEnumerable<string> tokens)
    {
        using var connection = Open();
        foreach (var token in tokens)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chat_encrypted_files WHERE token=@token";
            command.Parameters.AddWithValue("@token", token);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteUnreferencedEncryptedFiles(IReadOnlySet<string> activeTokens)
    {
        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT token FROM chat_encrypted_files WHERE created_at < UTC_TIMESTAMP() - INTERVAL 1 DAY";
        var stale = new List<string>();
        using (var reader = select.ExecuteReader())
            while (reader.Read()) { var token = reader.GetString(0); if (!activeTokens.Contains(token)) stale.Add(token); }
        foreach (var token in stale)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chat_encrypted_files WHERE token=@token";
            command.Parameters.AddWithValue("@token", token);
            command.ExecuteNonQuery();
        }
    }

    public void SaveGroupPhoto(Guid conversationId, ProfilePhoto? photo)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = photo is null ? "DELETE FROM chat_group_photos WHERE conversation_id=@id" : """
            INSERT INTO chat_group_photos(conversation_id, version, jpeg) VALUES (@id, @version, @jpeg)
            ON DUPLICATE KEY UPDATE version=VALUES(version), jpeg=VALUES(jpeg)
            """;
        command.Parameters.AddWithValue("@id", conversationId.ToString());
        if (photo is not null)
        {
            command.Parameters.AddWithValue("@version", photo.Version);
            command.Parameters.AddWithValue("@jpeg", photo.Jpeg);
        }
        command.ExecuteNonQuery();
    }

    public Dictionary<Guid, ProfilePhoto> LoadGroupPhotos()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id, version, jpeg FROM chat_group_photos";
        using var reader = command.ExecuteReader();
        var photos = new Dictionary<Guid, ProfilePhoto>();
        // MySqlConnector maps CHAR(36) to Guid; GetString throws after the first photo is saved.
        while (reader.Read()) photos[reader.GetGuid(0)] = new(reader.GetString(1), (byte[])reader[2]);
        return photos;
    }

    public void SavePhoto(Guid userId, ProfilePhoto? photo)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = photo is null ? "DELETE FROM chat_profile_photos WHERE user_id=@id" : """
            INSERT INTO chat_profile_photos(user_id, version, jpeg) VALUES (@id, @version, @jpeg)
            ON DUPLICATE KEY UPDATE version=VALUES(version), jpeg=VALUES(jpeg)
            """;
        command.Parameters.AddWithValue("@id", userId.ToString());
        if (photo is not null)
        {
            command.Parameters.AddWithValue("@version", photo.Version);
            command.Parameters.AddWithValue("@jpeg", photo.Jpeg);
        }
        command.ExecuteNonQuery();
    }

    public Dictionary<Guid, ProfilePhoto> LoadPhotos()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, version, jpeg FROM chat_profile_photos";
        using var reader = command.ExecuteReader();
        var photos = new Dictionary<Guid, ProfilePhoto>();
        // Use the typed reader so existing profile photos survive service restarts.
        while (reader.Read()) photos[reader.GetGuid(0)] = new(reader.GetString(1), (byte[])reader[2]);
        return photos;
    }

    public string? LoadSnapshot()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM chat_state_snapshot WHERE id = 1";
        return command.ExecuteScalar() as string;
    }

    public void SaveSnapshot(string payload)
    {
        using var connection = Open();
        // Publishing the snapshot and linking its newly uploaded ciphertext commit together.
        // A restart must not leave a successfully sent file without its message (or vice versa).
        using var transaction = connection.BeginTransaction();
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        foreach (var message in document.RootElement.GetProperty("Messages").EnumerateArray())
        {
            if (!message.TryGetProperty("Attachment", out var attachment) || attachment.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = """
                UPDATE chat_encrypted_files SET message_id=@message
                WHERE token=@token AND owner_id=@owner AND conversation_id=@room AND client_message_id=@client
                  AND (message_id IS NULL OR message_id=@message)
                """;
            link.Parameters.AddWithValue("@message", message.GetProperty("Id").GetGuid().ToString());
            link.Parameters.AddWithValue("@token", attachment.GetProperty("StorageToken").GetString());
            link.Parameters.AddWithValue("@owner", message.GetProperty("SenderId").GetGuid().ToString());
            link.Parameters.AddWithValue("@room", message.GetProperty("ConversationId").GetGuid().ToString());
            link.Parameters.AddWithValue("@client", message.GetProperty("ClientMessageId").GetGuid().ToString());
            if (link.ExecuteNonQuery() != 1)
            {
                // UseAffectedRows=true reports zero for an already linked unchanged row.
                // Verify its exact binding instead of rejecting subsequent snapshot saves.
                link.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM chat_encrypted_files WHERE token=@token AND owner_id=@owner
                        AND conversation_id=@room AND client_message_id=@client AND message_id=@message)
                    """;
                if (Convert.ToInt32(link.ExecuteScalar()) != 1)
                    throw new InvalidDataException("Şifreli dosya mesajla bağlanamadı.");
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chat_state_snapshot (id, payload) VALUES (1, @payload)
            ON DUPLICATE KEY UPDATE payload = VALUES(payload)
            """;
        command.Parameters.AddWithValue("@payload", payload);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<SiteAccount> ReadSiteAccounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, email, role, is_active FROM users ORDER BY id";
        using var reader = command.ExecuteReader();
        var users = new List<SiteAccount>();
        while (reader.Read())
        {
            var siteId = reader.GetInt32(0);
            users.Add(new SiteAccount(siteId,
                SiteUserId.FromExternalId(siteId.ToString(CultureInfo.InvariantCulture)),
                reader.GetString(1), reader.GetString(2),
                string.Equals(reader.GetString(3), "admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "user",
                reader.GetBoolean(4)));
        }
        return users;
    }

    public bool UpdateSiteRole(Guid chatUserId, string role)
    {
        if (role is not ("admin" or "user")) return false;
        using var connection = Open();
        using var list = connection.CreateCommand();
        list.CommandText = "SELECT id FROM users WHERE is_active = 1";
        var siteId = 0;
        using (var reader = list.ExecuteReader())
        {
            while (reader.Read())
            {
                var candidate = reader.GetInt32(0);
                if (SiteUserId.FromExternalId(candidate.ToString(CultureInfo.InvariantCulture)) != chatUserId) continue;
                siteId = candidate;
                break;
            }
        }
        if (siteId == 0) return false;
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE users SET role = @role WHERE id = @id AND is_active = 1";
        update.Parameters.AddWithValue("@role", role);
        update.Parameters.AddWithValue("@id", siteId);
        return update.ExecuteNonQuery() == 1;
    }
}
