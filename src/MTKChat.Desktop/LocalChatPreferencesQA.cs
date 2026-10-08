using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MTKChat.Desktop;

// Every account and path below is synthetic. This probe never opens the real
// LocalAppData preference directory and never needs a server or message body.
internal static class LocalChatPreferencesQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Personal preferences regression: " + check);
            checks.Add(check);
        }
        void Reject<TException>(Action action, string check) where TException : Exception
        {
            try { action(); }
            catch (TException) { checks.Add(check); return; }
            throw new InvalidOperationException("Personal preferences regression: " + check);
        }
        void RejectLockedReplacement(Action action, string check)
        {
            try { action(); }
            // Windows may report destination sharing restrictions as access denied
            // rather than a sharing-violation IOException. Both are the deliberate
            // fixture lock; unrelated failures must still terminate this probe.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { checks.Add(check); return; }
            throw new InvalidOperationException("Personal preferences regression: " + check);
        }

        var probe = Path.Combine(Path.GetFullPath(directory), "preferences-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        var accountA = Id(1);
        var accountB = Id(2);
        var roomA = Id(10);
        var roomB = Id(11);
        var messageA = Id(100);
        var messageB = Id(101);
        string PathFor(Guid account) => Path.Combine(probe, account.ToString("N") + ".dat");

        var store = LocalChatPreferences.Open(accountA, probe);
        Require(store.IsPersistent && store.UserId == accountA, "Explicit QA directory opens an account-bound persistent store");
        Require(!File.Exists(PathFor(accountA)), "Opening an empty store does not create a preference file");
        Require(!store.IsFavorite(roomA) && store.StarredIds(roomA).Count == 0 && !store.IsPinned(roomA),
            "New account starts without favorites, starred messages or pinned conversations");

        store.SetFavorite(roomA, true);
        store.SetFavorite(roomB, true);
        store.SetStarred(roomA, messageA, true);
        store.SetStarred(roomA, messageB, true);
        store.SetStarred(roomB, messageA, true);
        store.SetPinned(roomA, true);
        store.SetPinned(roomB, true);
        Require(store.IsPinned(roomA) && store.IsPinned(roomB),
            "Group and direct conversation IDs can be pinned independently without a product-specific pin count cap");
        Require(store.IsFavorite(roomA) && store.IsFavorite(roomB), "Two rooms can independently be favorites");
        Require(store.IsStarred(roomA, messageA) && store.IsStarred(roomA, messageB) && store.IsStarred(roomB, messageA),
            "Stars are keyed by room and message rather than message ID alone");
        Require(store.StarredIds(roomA).SetEquals(new[] { messageA, messageB }) &&
            store.StarredIds(roomB).SetEquals(new[] { messageA }), "Star queries include only the requested room");
        var detached = store.StarredIds(roomA);
        if (detached is ISet<Guid> mutable) mutable.Clear();
        Require(store.IsStarred(roomA, messageA), "Returned star IDs cannot mutate the owned bookmark set");

        var protectedBytes = File.ReadAllBytes(PathFor(accountA));
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy(accountA), DataProtectionScope.CurrentUser);
        try
        {
            Require(!protectedBytes.AsSpan().SequenceEqual(plain), "On-disk bytes are DPAPI ciphertext rather than the preference JSON");
            var encryptedText = Encoding.UTF8.GetString(protectedBytes);
            Require(!encryptedText.Contains(accountA.ToString(), StringComparison.Ordinal) &&
                !encryptedText.Contains(roomA.ToString(), StringComparison.Ordinal) &&
                !encryptedText.Contains(messageA.ToString(), StringComparison.Ordinal), "Account, room and message IDs are not visible in the file");
            using var json = JsonDocument.Parse(plain);
            var data = json.RootElement;
            Require(data.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(new[] { "Version", "UserId", "Favorites", "Stars", "Archived", "Mutes", "Pins" }),
                "Decrypted preference schema contains only version, owner, bookmark/archive/pin IDs and mute deadlines");
            Require(data.GetProperty("Version").GetInt32() == 3 && data.GetProperty("UserId").GetGuid() == accountA,
                "Decrypted preference payload identifies its schema version and account");
            Require(data.GetProperty("Favorites").EnumerateArray().All(p => p.ValueKind == JsonValueKind.String && p.TryGetGuid(out _)),
                "Favorites serialize IDs only, without room names or conversation text");
            Require(data.GetProperty("Pins").EnumerateArray().All(p => p.ValueKind == JsonValueKind.String && p.TryGetGuid(out _)) &&
                data.GetProperty("Pins").GetArrayLength() == 2,
                "Conversation pins serialize room IDs only, without names, messages, keys or other account data");
            Require(data.GetProperty("Stars").EnumerateArray().All(p =>
                p.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(new[] { "RoomId", "MessageId" }) &&
                p.GetProperty("RoomId").TryGetGuid(out _) && p.GetProperty("MessageId").TryGetGuid(out _)),
                "Stars serialize room/message IDs only, without message bodies, media or usernames");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Reject<CryptographicException>(() => ProtectedData.Unprotect(protectedBytes, Entropy(accountB), DataProtectionScope.CurrentUser),
            "A different chat account cannot unprotect the existing preference file");

        var reopened = LocalChatPreferences.Open(accountA, probe);
        Require(reopened.IsFavorite(roomA) && reopened.IsFavorite(roomB) && reopened.IsStarred(roomA, messageA) &&
            reopened.IsStarred(roomB, messageA) && reopened.IsPinned(roomA) && reopened.IsPinned(roomB),
            "Favorites, stars and independent conversation pins survive store reopen");
        var originalWriteTime = File.GetLastWriteTimeUtc(PathFor(accountA));
        for (var i = 0; i < 20; i++)
        {
            reopened.SetFavorite(roomA, true);
            reopened.SetStarred(roomA, messageA, true);
            reopened.SetFavorite(Id(12), false);
            reopened.SetStarred(roomA, Id(102), false);
            reopened.SetPinned(roomA, true);
            reopened.SetPinned(Id(12), false);
        }
        Require(File.ReadAllBytes(PathFor(accountA)).AsSpan().SequenceEqual(protectedBytes) &&
            File.GetLastWriteTimeUtc(PathFor(accountA)) == originalWriteTime,
            "Repeated equivalent toggles do not rewrite or re-encrypt the file");

        reopened.SetFavorite(roomB, false);
        reopened.SetStarred(roomA, messageA, false);
        reopened.SetPinned(roomB, false);
        var afterRemoval = LocalChatPreferences.Open(accountA, probe);
        Require(afterRemoval.IsFavorite(roomA) && !afterRemoval.IsFavorite(roomB) &&
            !afterRemoval.IsStarred(roomA, messageA) && afterRemoval.IsStarred(roomB, messageA) &&
            afterRemoval.IsStarred(roomA, messageB) && afterRemoval.IsPinned(roomA) && !afterRemoval.IsPinned(roomB),
            "Bookmark and pin removal survive restart and preserve independent metadata in the other room");

        var otherAccount = LocalChatPreferences.Open(accountB, probe);
        Require(!otherAccount.IsFavorite(roomA) && !otherAccount.IsStarred(roomB, messageA) && !otherAccount.IsPinned(roomA),
            "Another chat account starts isolated even on the same Windows account");
        otherAccount.SetFavorite(roomB, true);
        otherAccount.SetStarred(roomB, messageB, true);
        otherAccount.SetPinned(roomB, true);
        Require(File.Exists(PathFor(accountB)) && !afterRemoval.IsFavorite(roomB) && !afterRemoval.IsStarred(roomB, messageB),
            "Other-account updates use a separate file and do not change the first account");
        Require(LocalChatPreferences.Open(accountB, probe).IsStarred(roomB, messageB) &&
            LocalChatPreferences.Open(accountB, probe).IsPinned(roomB) && !afterRemoval.IsPinned(roomB),
            "Second account reopens its own bookmarks and pins without changing the first account");

        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        afterRemoval.SetArchived(roomA, true);
        afterRemoval.SetArchived(roomB, true);
        afterRemoval.SetMute(roomA, now.AddHours(8), now);
        afterRemoval.SetMute(roomB, null, now);
        var personal = LocalChatPreferences.Open(accountA, probe);
        Require(personal.IsArchived(roomA) && personal.IsArchived(roomB) &&
            personal.IsMuted(roomA, now) && personal.IsMuted(roomB, now) && personal.IsPinned(roomA) && !personal.IsPinned(roomB),
            "Personal archive, finite/unlimited mute and pins survive DPAPI store reopen together");
        Require(personal.IsMuted(roomA, now.AddHours(8).AddTicks(-1)) && !personal.IsMuted(roomA, now.AddHours(8)) &&
            personal.IsMuted(roomB, DateTimeOffset.MaxValue), "Finite mute expires exactly at its deadline while unlimited mute stays active");
        Require(personal.MuteUntil(roomA) == now.AddHours(8) && personal.MuteUntil(roomB) is null,
            "Finite mute preserves its deadline and unlimited mute has no artificial far-future timestamp");
        Require(!otherAccount.IsArchived(roomA) && !otherAccount.IsMuted(roomB, now),
            "Archive and mute never affect the other chat account");
        var mutePlain = ProtectedData.Unprotect(File.ReadAllBytes(PathFor(accountA)), Entropy(accountA), DataProtectionScope.CurrentUser);
        try
        {
            using var json = JsonDocument.Parse(mutePlain);
            Require(json.RootElement.GetProperty("Archived").EnumerateArray().All(item => item.TryGetGuid(out _)) &&
                json.RootElement.GetProperty("Mutes").EnumerateArray().All(item =>
                    item.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(new[] { "RoomId", "Until" }) &&
                    item.GetProperty("RoomId").TryGetGuid(out _)),
                "Archive/mute persistence contains room IDs and deadlines only, not names, messages or other users");
        }
        finally { CryptographicOperations.ZeroMemory(mutePlain); }
        var personalBaseline = File.ReadAllBytes(PathFor(accountA));
        personal.SetArchived(roomA, true); personal.SetMute(roomA, now.AddHours(8), now); personal.SetMute(roomB, null, now);
        Require(File.ReadAllBytes(PathFor(accountA)).AsSpan().SequenceEqual(personalBaseline),
            "Equivalent archive and mute operations do not rewrite the protected file");
        using (var locked = new FileStream(PathFor(accountA), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RejectLockedReplacement(() => personal.SetArchived(roomA, false), "Failed archive write keeps the previous personal view");
            RejectLockedReplacement(() => personal.ClearMute(roomB), "Failed unmute write keeps the previous mute policy");
            RejectLockedReplacement(() => personal.SetMute(roomA, now.AddDays(7), now), "Failed duration change keeps the previous deadline");
            RejectLockedReplacement(() => personal.SetPinned(roomA, false), "Failed unpin write keeps the previous conversation order policy");
            RejectLockedReplacement(() => personal.SetPinned(roomB, true), "Failed pin write cannot change the in-memory conversation order");
            Require(personal.IsArchived(roomA) && personal.IsMuted(roomB, now) && personal.MuteUntil(roomA) == now.AddHours(8) &&
                personal.IsPinned(roomA) && !personal.IsPinned(roomB) &&
                File.ReadAllBytes(PathFor(accountA)).AsSpan().SequenceEqual(personalBaseline),
                "Archive/mute/pin mutations roll back both memory and ciphertext on an atomic replacement failure");
        }
        personal.SetArchived(roomA, false); personal.ClearMute(roomA);
        Require(!personal.IsArchived(roomA) && !personal.IsMuted(roomA, now) && personal.IsArchived(roomB) && personal.IsMuted(roomB, now) &&
            personal.IsFavorite(roomA) && personal.IsStarred(roomB, messageA),
            "Unarchive/unmute preserve another room and all existing favorites/stars");
        // Continue the older bookmark failure probes from the latest committed
        // instance rather than a stale copy with pre-archive in-memory state.
        afterRemoval = personal;

        var failedSaveBaseline = File.ReadAllBytes(PathFor(accountA));
        using (var locked = new FileStream(PathFor(accountA), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RejectLockedReplacement(() => afterRemoval.SetFavorite(roomB, true), "Locked destination rejects a favorite write");
            RejectLockedReplacement(() => afterRemoval.SetStarred(roomB, messageB, true), "Locked destination rejects a star write");
            Require(!afterRemoval.IsFavorite(roomB) && !afterRemoval.IsStarred(roomB, messageB) &&
                afterRemoval.IsFavorite(roomA) && afterRemoval.IsStarred(roomB, messageA),
                "Failed replacement rolls back in-memory state for both kinds of bookmark");
            Require(File.ReadAllBytes(PathFor(accountA)).AsSpan().SequenceEqual(failedSaveBaseline),
                "Failed atomic replacement preserves the previous complete ciphertext");
            Require(Directory.GetFiles(probe, "*.tmp").Length == 0, "Failed replacement cleans only its own temporary output");
        }
        afterRemoval.SetFavorite(roomB, true);
        Require(LocalChatPreferences.Open(accountA, probe).IsFavorite(roomB), "A write succeeds normally after the synthetic file lock is released");

        var blockedDirectory = Path.Combine(probe, "synthetic-file-not-directory");
        File.WriteAllText(blockedDirectory, "qa-directory-blocker");
        var blocked = LocalChatPreferences.Open(Id(3), blockedDirectory);
        Reject<IOException>(() => blocked.SetFavorite(roomA, true), "A file in place of a directory rejects persistence");
        Require(!blocked.IsFavorite(roomA) && File.ReadAllText(blockedDirectory) == "qa-directory-blocker",
            "Directory creation failure leaves memory and the unrelated blocker file unchanged");
        Reject<IOException>(() => blocked.SetPinned(roomA, true), "A directory creation failure rejects a pin write");
        Require(!blocked.IsPinned(roomA), "A failed initial pin save leaves the memory state unchanged");

        var corruptAccount = Id(4);
        var corruptPath = PathFor(corruptAccount);
        var corrupt = "synthetic invalid protected bytes"u8.ToArray();
        File.WriteAllBytes(corruptPath, corrupt);
        Reject<CryptographicException>(() => LocalChatPreferences.Open(corruptAccount, probe),
            "Corrupt DPAPI data is reported instead of silently replacing saved preferences");
        Require(File.ReadAllBytes(corruptPath).AsSpan().SequenceEqual(corrupt), "Failed open preserves corrupt bytes for recovery");

        var wrongOwnerAccount = Id(5);
        var wrongOwnerPath = PathFor(wrongOwnerAccount);
        var wrongOwnerPlain = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, UserId = accountA, Favorites = new[] { roomA }, Stars = Array.Empty<object>()
        });
        byte[] wrongOwnerProtected;
        try { wrongOwnerProtected = ProtectedData.Protect(wrongOwnerPlain, Entropy(wrongOwnerAccount), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(wrongOwnerPlain); }
        File.WriteAllBytes(wrongOwnerPath, wrongOwnerProtected);
        Reject<InvalidDataException>(() => LocalChatPreferences.Open(wrongOwnerAccount, probe),
            "Even a decryptable file with a different payload owner is rejected");
        Require(File.ReadAllBytes(wrongOwnerPath).AsSpan().SequenceEqual(wrongOwnerProtected), "Payload ownership rejection does not overwrite the file");

        var nullAccount = Id(7);
        var nullPath = PathFor(nullAccount);
        var nullPayload = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, UserId = nullAccount,
            Favorites = Array.Empty<Guid>(), Stars = new object?[] { null } });
        var nullProtected = ProtectedData.Protect(nullPayload, Entropy(nullAccount), DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(nullPayload);
        File.WriteAllBytes(nullPath, nullProtected);
        Reject<InvalidDataException>(() => LocalChatPreferences.Open(nullAccount, probe),
            "A null star entry is rejected as invalid data rather than crashing login with NullReferenceException");
        Require(File.ReadAllBytes(nullPath).AsSpan().SequenceEqual(nullProtected), "Invalid null-entry preferences remain untouched");

        var legacyAccount = Id(8);
        var legacyPlain = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, UserId = legacyAccount,
            Favorites = new[] { roomA }, Stars = new[] { new { RoomId = roomA, MessageId = messageA } } });
        var legacyProtected = ProtectedData.Protect(legacyPlain, Entropy(legacyAccount), DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(legacyPlain);
        File.WriteAllBytes(PathFor(legacyAccount), legacyProtected);
        var migrated = LocalChatPreferences.Open(legacyAccount, probe);
        Require(migrated.IsFavorite(roomA) && migrated.IsStarred(roomA, messageA) && !migrated.IsArchived(roomA) &&
            !migrated.IsMuted(roomA, now) && !migrated.IsPinned(roomA),
            "Version-1 preferences load existing bookmarks without inventing an archive, mute or pin");
        Require(File.ReadAllBytes(PathFor(legacyAccount)).AsSpan().SequenceEqual(legacyProtected),
            "Loading legacy preferences alone never rewrites or migrates the file");
        migrated.SetPinned(roomA, true);
        var migratedReopen = LocalChatPreferences.Open(legacyAccount, probe);
        Require(migratedReopen.IsPinned(roomA) && migratedReopen.IsFavorite(roomA) &&
            migratedReopen.IsStarred(roomA, messageA) && !migratedReopen.IsArchived(roomA) && !migratedReopen.IsMuted(roomA, now),
            "The first version-1 pin edit upgrades the schema while preserving favorites and stars");
        var legacyV2Account = Id(14);
        var legacyV2Plain = JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, UserId = legacyV2Account,
            Favorites = new[] { roomA }, Stars = new[] { new { RoomId = roomA, MessageId = messageA } },
            Archived = new[] { roomB }, Mutes = new[] { new { RoomId = roomA, Until = (DateTimeOffset?)now.AddHours(8) },
                new { RoomId = roomB, Until = (DateTimeOffset?)null } } });
        var legacyV2Protected = ProtectedData.Protect(legacyV2Plain, Entropy(legacyV2Account), DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(legacyV2Plain);
        File.WriteAllBytes(PathFor(legacyV2Account), legacyV2Protected);
        var migratedV2 = LocalChatPreferences.Open(legacyV2Account, probe);
        Require(!migratedV2.IsPinned(roomA) && migratedV2.IsFavorite(roomA) && migratedV2.IsStarred(roomA, messageA) &&
            migratedV2.IsArchived(roomB) && migratedV2.MuteUntil(roomA) == now.AddHours(8) && migratedV2.IsMuted(roomB, now),
            "Version-2 preferences load favorites, stars, archive and finite/unlimited mute with no invented pins");
        Require(File.ReadAllBytes(PathFor(legacyV2Account)).AsSpan().SequenceEqual(legacyV2Protected),
            "Opening version-2 preferences does not rewrite the account's existing ciphertext");
        migratedV2.SetPinned(roomA, true);
        var migratedV2Reopen = LocalChatPreferences.Open(legacyV2Account, probe);
        Require(migratedV2Reopen.IsPinned(roomA) && migratedV2Reopen.IsFavorite(roomA) &&
            migratedV2Reopen.IsStarred(roomA, messageA) && migratedV2Reopen.IsArchived(roomB) &&
            migratedV2Reopen.MuteUntil(roomA) == now.AddHours(8) && migratedV2Reopen.IsMuted(roomB, now),
            "A version-2 pin edit upgrades to version 3 without losing any existing personal preference");
        void InvalidV2(int suffix, object? mutes, string check)
        {
            var account = Id(suffix);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, UserId = account,
                Favorites = Array.Empty<Guid>(), Stars = Array.Empty<object>(), Archived = Array.Empty<Guid>(), Mutes = mutes });
            var encrypted = ProtectedData.Protect(bytes, Entropy(account), DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(bytes);
            File.WriteAllBytes(PathFor(account), encrypted);
            Reject<InvalidDataException>(() => LocalChatPreferences.Open(account, probe), check);
            Require(File.ReadAllBytes(PathFor(account)).AsSpan().SequenceEqual(encrypted), check + " — original file preserved");
        }
        InvalidV2(9, null, "Version-2 preferences reject a missing mute collection");
        InvalidV2(12, new object?[] { null }, "Version-2 preferences reject null mute records without dereferencing them");
        InvalidV2(13, new[] { new { RoomId = roomA, Until = (DateTimeOffset?)null }, new { RoomId = roomA, Until = (DateTimeOffset?)null } },
            "Version-2 preferences reject duplicate mute room IDs before dictionary construction");

        void ProtectedFixture(Guid account, object data)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
            try { File.WriteAllBytes(PathFor(account), ProtectedData.Protect(bytes, Entropy(account), DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var optionalPinsAccount = Id(15);
        ProtectedFixture(optionalPinsAccount, new { Version = 3, UserId = optionalPinsAccount,
            Favorites = new[] { roomA }, Stars = Array.Empty<object>(), Archived = new[] { roomB }, Mutes = Array.Empty<object>() });
        var optionalPins = LocalChatPreferences.Open(optionalPinsAccount, probe);
        Require(!optionalPins.IsPinned(roomA) && optionalPins.IsFavorite(roomA) && optionalPins.IsArchived(roomB),
            "Version-3 preferences accept an omitted optional Pins collection as empty without resetting other metadata");
        optionalPins.SetPinned(roomA, true);
        Require(LocalChatPreferences.Open(optionalPinsAccount, probe).IsPinned(roomA),
            "The first pin is persisted correctly when Pins was previously omitted");

        void InvalidPins(int suffix, int version, Guid[] pins, string check)
        {
            var account = Id(suffix);
            ProtectedFixture(account, new { Version = version, UserId = account, Favorites = Array.Empty<Guid>(),
                Stars = Array.Empty<object>(), Archived = Array.Empty<Guid>(), Mutes = Array.Empty<object>(), Pins = pins });
            var baseline = File.ReadAllBytes(PathFor(account));
            Reject<InvalidDataException>(() => LocalChatPreferences.Open(account, probe), check);
            Require(File.ReadAllBytes(PathFor(account)).AsSpan().SequenceEqual(baseline), check + " — original file preserved");
        }
        InvalidPins(16, 3, [Guid.Empty], "Version-3 preferences reject an empty pinned conversation ID");
        InvalidPins(17, 3, [roomA, roomA], "Version-3 preferences reject duplicate pinned conversation IDs");
        InvalidPins(18, 4, [], "An unsupported future version is rejected rather than mistaken for version 3");
        var boundaryPins = Enumerable.Range(100000, 20000).Select(Id).ToArray();
        var boundaryAccount = Id(19);
        ProtectedFixture(boundaryAccount, new { Version = 3, UserId = boundaryAccount, Favorites = Array.Empty<Guid>(),
            Stars = Array.Empty<object>(), Archived = Array.Empty<Guid>(), Mutes = Array.Empty<object>(), Pins = boundaryPins });
        var boundary = LocalChatPreferences.Open(boundaryAccount, probe);
        Require(boundary.IsPinned(boundaryPins[0]) && boundary.IsPinned(boundaryPins[^1]),
            "The storage boundary accepts 20,000 unique pins rather than imposing an arbitrary small pin cap");
        var boundaryBaseline = File.ReadAllBytes(PathFor(boundaryAccount));
        Reject<InvalidOperationException>(() => boundary.SetPinned(roomA, true),
            "An additional pin beyond the aggregate personal preference limit is rejected");
        Reject<InvalidOperationException>(() => boundary.SetFavorite(roomA, true),
            "Pins count toward the shared storage bound when another preference type is added");
        Require(!boundary.IsPinned(roomA) && !boundary.IsFavorite(roomA) &&
            File.ReadAllBytes(PathFor(boundaryAccount)).AsSpan().SequenceEqual(boundaryBaseline),
            "Storage-limit failures preserve both the existing pin set and complete ciphertext");
        InvalidPins(20, 3, boundaryPins.Append(roomA).ToArray(),
            "Loading too many unique pins rejects the entire unsupported payload without overwriting it");
        var aggregateAccount = Id(21);
        ProtectedFixture(aggregateAccount, new { Version = 3, UserId = aggregateAccount, Favorites = new[] { roomA },
            Stars = Array.Empty<object>(), Archived = Array.Empty<Guid>(), Mutes = Array.Empty<object>(), Pins = boundaryPins });
        Reject<InvalidDataException>(() => LocalChatPreferences.Open(aggregateAccount, probe),
            "The read-side storage bound includes existing favorites as well as pins");

        Reject<ArgumentException>(() => LocalChatPreferences.Memory(Guid.Empty), "Memory mode rejects an empty account ID");
        Reject<ArgumentException>(() => LocalChatPreferences.Open(Guid.Empty, probe), "Persistent mode rejects an empty account ID");
        Reject<ArgumentException>(() => afterRemoval.SetFavorite(Guid.Empty, false), "Favorite toggles reject an empty room even for a no-op");
        Reject<ArgumentException>(() => afterRemoval.SetStarred(Guid.Empty, messageA, true), "Star toggles reject an empty room");
        Reject<ArgumentException>(() => afterRemoval.SetStarred(roomA, Guid.Empty, false), "Star toggles reject an empty message even for a no-op");
        Reject<ArgumentException>(() => afterRemoval.SetArchived(Guid.Empty, false), "Archive toggles reject an empty room even for a no-op");
        Reject<ArgumentException>(() => afterRemoval.SetPinned(Guid.Empty, false), "Pin toggles reject an empty room even for a no-op");
        Reject<ArgumentException>(() => afterRemoval.SetMute(Guid.Empty, null, now), "Mute toggles reject an empty room");
        Reject<ArgumentException>(() => afterRemoval.ClearMute(Guid.Empty), "Unmute rejects an empty room even for a no-op");
        Reject<ArgumentOutOfRangeException>(() => afterRemoval.SetMute(roomA, now, now), "Mute rejects an already expired deadline");

        var filesBeforeMemory = Directory.GetFiles(probe, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
        var memory = LocalChatPreferences.Memory(Id(6));
        memory.SetFavorite(roomA, true);
        memory.SetStarred(roomA, messageA, true);
        memory.SetStarred(roomB, messageA, true);
        memory.SetStarred(roomA, messageA, false);
        memory.SetArchived(roomA, true); memory.SetMute(roomB, null, now);
        memory.SetPinned(roomA, true); memory.SetPinned(roomB, true); memory.SetPinned(roomB, false);
        Require(!memory.IsPersistent && memory.IsFavorite(roomA) && !memory.IsStarred(roomA, messageA) &&
            memory.IsStarred(roomB, messageA), "Memory mode supports the same isolated bookmark toggles without persistence");
        Require(memory.IsArchived(roomA) && memory.IsMuted(roomB, now), "Memory mode supports personal archive and mute without files");
        Require(memory.IsPinned(roomA) && !memory.IsPinned(roomB),
            "Memory mode supports independent pin/unpin toggles without changing other preferences");
        Require(filesBeforeMemory.SequenceEqual(Directory.GetFiles(probe, "*", SearchOption.AllDirectories).OrderBy(p => p)) &&
            !File.Exists(PathFor(Id(6))), "Memory mode creates no files in the explicit synthetic preference directory");
        Require(!LocalChatPreferences.Memory(Id(6)).IsFavorite(roomA) && !LocalChatPreferences.Memory(Id(6)).IsPinned(roomA),
            "A new memory-only store intentionally has no persisted bookmark or pin state");
        return checks;
    }

    private static Guid Id(int suffix) => Guid.Parse("d0c02026-1002-4000-8000-" + suffix.ToString("D12"));
    private static byte[] Entropy(Guid account) => Encoding.UTF8.GetBytes("MTKChat.PersonalPreferences.v1." + account.ToString("N"));
}
