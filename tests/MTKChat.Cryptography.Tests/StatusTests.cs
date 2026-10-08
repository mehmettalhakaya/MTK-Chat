using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class StatusTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly List<DeviceIdentity> _identities = [];
        public Clock Time { get; } = new();
        public ChatState State { get; }
        public ChatUser Author { get; }
        public ChatUser Member { get; }
        public ChatUser Outside { get; }
        public ChatUser Admin { get; }
        public Dictionary<Guid, DeviceIdentity> Identities { get; } = [];
        public Fixture()
        {
            State = new(timeProvider: Time);
            Author = User("author"); Member = User("member"); Outside = User("outside"); Admin = User("admin", "admin");
        }
        public ChatUser User(string name, string role = "user", bool device = true)
        {
            var user = State.UpsertSiteUser(Guid.NewGuid(), name, name + "@status.invalid", role);
            if (device)
            {
                var identity = DeviceIdentity.Create(); _identities.Add(identity); Identities.Add(user.Id, identity);
                Assert.True(State.RegisterDevice(user.Id, new("Synthetic", identity.ExportEncryptionPublicKey(), identity.ExportSigningPublicKey())));
            }
            return user;
        }
        public SendStatusRequest Request(Guid? author = null, Guid[]? audience = null, byte[]? content = null,
            string kind = "text", DateTimeOffset? created = null, Guid? clientId = null)
        {
            var sender = author ?? Author.Id;
            var recipients = (audience ?? [Member.Id]).Append(sender).Distinct().Select(id => State.GetDevice(id)!).ToArray();
            return StatusCryptography.Encrypt(content ?? Encoding.UTF8.GetBytes("SYNTHETIC PRIVATE STATUS"), kind,
                clientId ?? Guid.NewGuid(), sender, created ?? Time.Now, recipients, Identities[sender].SigningKey);
        }
        public StoredStatus Add(SendStatusRequest? request = null, Guid? author = null)
        {
            Assert.Equal(StatusResult.Success, State.AddStatus(author ?? Author.Id, request ?? Request(author), out var result));
            return result!;
        }
        public void Dispose() { foreach (var identity in _identities) identity.Dispose(); }
    }

    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
    private static void Restore(ChatState state, string json) => typeof(ChatState)
        .GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [json]);

    [Fact]
    public void OnlyExplicitAudienceAndAuthorReceiveOneEnvelopeAndNoAdminBypass()
    {
        using var fixture = new Fixture(); var sent = fixture.Add();
        var own = Assert.Single(fixture.State.GetStatuses(fixture.Author.Id));
        var selected = Assert.Single(fixture.State.GetStatuses(fixture.Member.Id));
        Assert.Equal(own, selected); Assert.Empty(fixture.State.GetStatuses(fixture.Outside.Id)); Assert.Empty(fixture.State.GetStatuses(fixture.Admin.Id));
        Assert.Null(fixture.State.GetStatus(fixture.Outside.Id, sent.Id)); Assert.Null(fixture.State.GetStatus(fixture.Admin.Id, sent.Id));
        var memberView = fixture.State.GetStatus(fixture.Member.Id, sent.Id)!;
        Assert.Equal(fixture.Member.Id, memberView.Payload.RecipientId); Assert.Equal(fixture.Author.Id, sent.Payload.RecipientId);
        var plaintext = StatusCryptography.Decrypt(memberView, fixture.Member.Id, fixture.Identities[fixture.Member.Id].EncryptionKey,
            fixture.State.GetDevice(fixture.Author.Id)!.SigningPublicKey);
        Assert.Equal("SYNTHETIC PRIVATE STATUS", Encoding.UTF8.GetString(plaintext));
        using var feedJson = JsonDocument.Parse(JsonSerializer.Serialize(selected));
        Assert.Equal(new[] { "ClientStatusId", "CreatedAt", "ExpiresAt", "Id", "Kind", "SenderId" },
            feedJson.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.DoesNotContain("SYNTHETIC PRIVATE STATUS", Snapshot(fixture.State));
        Assert.DoesNotContain("\"Key\":", JsonSerializer.Serialize(memberView));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(5)]
    public void ServerAcceptanceControlsTwentyFourHoursNotClientTimestamp(int offset)
    {
        using var fixture = new Fixture(); var sent = fixture.Add(fixture.Request(created: fixture.Time.Now.AddMinutes(offset)));
        Assert.Equal(fixture.Time.Now.AddHours(24), sent.ExpiresAt);
        fixture.Time.Now = sent.ExpiresAt.AddTicks(-1); Assert.NotNull(fixture.State.GetStatus(fixture.Member.Id, sent.Id));
        fixture.Time.Now = sent.ExpiresAt; Assert.Empty(fixture.State.GetStatuses(fixture.Author.Id));
        Assert.Null(fixture.State.GetStatus(fixture.Member.Id, sent.Id));
        Assert.Equal(1, fixture.State.RemoveExpiredStatuses()); Assert.Equal(0, fixture.State.RemoveExpiredStatuses());
        Assert.Empty(JsonNode.Parse(Snapshot(fixture.State))!["Statuses"]!.AsArray());
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void StaleOrFutureClientTimestampRejectedWithoutMutation(int offset)
    {
        using var fixture = new Fixture(); var before = Snapshot(fixture.State);
        Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id,
            fixture.Request(created: fixture.Time.Now.AddMinutes(offset)), out var sent));
        Assert.Null(sent); Assert.Equal(before, Snapshot(fixture.State));
    }

    [Fact]
    public void RetryDoesNotExtendExpiryAndChangedRequestCannotReuseClientIdentity()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var sent = fixture.Add(request);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);
        Assert.Equal(StatusResult.Success, fixture.State.AddStatus(fixture.Author.Id,
            request with { Payloads = request.Payloads.ToArray() }, out var retried));
        Assert.Equal(sent, retried); Assert.Single(fixture.State.GetStatuses(fixture.Author.Id));
        var changed = fixture.Request(clientId: request.ClientStatusId, created: request.CreatedAt,
            content: Encoding.UTF8.GetBytes("Changed content"));
        Assert.Equal(StatusResult.ReplayConflict, fixture.State.AddStatus(fixture.Author.Id, changed, out _));
        Assert.Equal(StatusResult.ReplayConflict, fixture.State.AddStatus(fixture.Author.Id, request with { Kind = "image/png" }, out _));
        Assert.Equal(sent, fixture.State.GetStatus(fixture.Author.Id, sent.Id));
    }

    [Fact]
    public void OnlyAuthorCanDeleteAndTombstonePreventsResurrectionAcrossRestart()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var sent = fixture.Add(request);
        Assert.Equal(StatusResult.NotFound, fixture.State.DeleteStatus(fixture.Member.Id, sent.Id));
        Assert.Equal(StatusResult.NotFound, fixture.State.DeleteStatus(fixture.Admin.Id, sent.Id));
        Assert.Equal(StatusResult.Success, fixture.State.DeleteStatus(fixture.Author.Id, sent.Id));
        Assert.Equal(StatusResult.Success, fixture.State.DeleteStatus(fixture.Author.Id, sent.Id));
        Assert.Empty(fixture.State.GetStatuses(fixture.Member.Id));
        Assert.Equal(StatusResult.NotFound, fixture.State.AddStatus(fixture.Author.Id, request, out _));
        var json = Snapshot(fixture.State); Assert.DoesNotContain(request.Ciphertext, json);
        var deleted = Assert.Single(JsonNode.Parse(json)!["Statuses"]!.AsArray());
        Assert.True(deleted!["Deleted"]!.GetValue<bool>()); Assert.Empty(deleted["Payloads"]!.AsArray());
        Restore(fixture.State, json);
        Assert.Equal(StatusResult.NotFound, fixture.State.AddStatus(fixture.Author.Id, request, out _));
        fixture.Time.Now = sent.ExpiresAt; Assert.Equal(1, fixture.State.RemoveExpiredStatuses());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockingEitherDirectionImmediatelyHidesExistingStatusAndRejectsNewSharing(bool byAuthor)
    {
        using var fixture = new Fixture(); var sent = fixture.Add();
        var owner = byAuthor ? fixture.Author.Id : fixture.Member.Id;
        var target = byAuthor ? fixture.Member.Id : fixture.Author.Id;
        Assert.True(fixture.State.SetBlocked(owner, target, true));
        Assert.Empty(fixture.State.GetStatuses(fixture.Member.Id)); Assert.Null(fixture.State.GetStatus(fixture.Member.Id, sent.Id));
        Assert.Equal(StatusResult.Forbidden, fixture.State.AddStatus(fixture.Author.Id, fixture.Request(), out _));
        Assert.NotNull(fixture.State.GetStatus(fixture.Author.Id, sent.Id)); // Author can still inspect/delete their own status.
        Assert.True(fixture.State.SetBlocked(owner, target, false));
        Assert.NotNull(fixture.State.GetStatus(fixture.Member.Id, sent.Id));
    }

    [Fact]
    public void BannedAuthorsOrRecipientsAndAgentsHaveNoStatusAccess()
    {
        using var fixture = new Fixture(); var sent = fixture.Add(); var request = fixture.Request();
        Assert.True(fixture.State.ModerateUser(fixture.Member.Id, "ban", null));
        Assert.Empty(fixture.State.GetStatuses(fixture.Member.Id));
        Assert.Equal(StatusResult.Forbidden, fixture.State.AddStatus(fixture.Author.Id, request, out _));
        Assert.True(fixture.State.ModerateUser(fixture.Member.Id, "unban", null));
        Assert.True(fixture.State.ModerateUser(fixture.Author.Id, "ban", null));
        Assert.Empty(fixture.State.GetStatuses(fixture.Member.Id)); Assert.Null(fixture.State.GetStatus(fixture.Member.Id, sent.Id));
        Assert.Equal(StatusResult.Forbidden, fixture.State.DeleteStatus(fixture.Author.Id, sent.Id));
        Assert.Equal(StatusResult.Forbidden, fixture.State.AddStatus(fixture.Author.Id, request, out _));
        var gemini = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal(StatusResult.Forbidden, fixture.State.AddStatus(gemini, request, out _));
        Assert.Empty(fixture.State.GetStatuses(gemini));
    }

    [Fact]
    public void MissingSenderDuplicateOrUnchosenUnknownAgentAndMissingDeviceAudienceIsRejected()
    {
        using var fixture = new Fixture(); var request = fixture.Request(); var original = request.Payloads.ToArray();
        foreach (var invalid in new[]
        {
            request with { Payloads = [] },
            request with { Payloads = [original[0]] },
            request with { Payloads = [original[0], original[0], original[1]] },
            request with { Payloads = [original[0] with { RecipientId = fixture.Outside.Id }, original[1] with { RecipientId = fixture.Member.Id }] }
        }) Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, invalid, out _));
        foreach (var id in new[] { Guid.NewGuid(), Guid.Parse("22222222-2222-2222-2222-222222222222"), fixture.User("nodevice", device: false).Id })
            Assert.Equal(StatusResult.Forbidden, fixture.State.AddStatus(fixture.Author.Id,
                request with { Payloads = [original[0] with { RecipientId = id }, original[1]] }, out _));
        Assert.Empty(fixture.State.GetStatuses(fixture.Author.Id));
    }

    [Fact]
    public void InvalidCryptographicFieldsSignatureAndScopeCannotPublish()
    {
        using var fixture = new Fixture(); var request = fixture.Request();
        EncryptedPayload[] Replace(EncryptedPayload changed) => [changed, request.Payloads[1]];
        foreach (var payload in new[]
        {
            request.Payloads[0] with { Algorithm = "other" },
            request.Payloads[0] with { EphemeralPublicKey = "invalid" },
            request.Payloads[0] with { Nonce = Convert.ToBase64String(new byte[11]) },
            request.Payloads[0] with { Tag = Convert.ToBase64String(new byte[15]) },
            request.Payloads[0] with { Signature = Convert.ToBase64String(new byte[64]) },
            request.Payloads[0] with { Ciphertext = Convert.ToBase64String(new byte[StatusProtocol.MaximumEnvelopeBytes + 1]) }
        }) Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, request with { Payloads = Replace(payload) }, out _));
        Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, request with { CreatedAt = request.CreatedAt.AddSeconds(1) }, out _));
        var wrongScope = MessageCryptography.Encrypt([1], request.ClientStatusId, Guid.NewGuid(), fixture.Author.Id,
            fixture.Member.Id, request.CreatedAt, fixture.State.GetDevice(fixture.Member.Id)!.EncryptionPublicKey, fixture.Identities[fixture.Author.Id].SigningKey);
        Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, request with { Payloads = Replace(wrongScope) }, out _));
        Assert.Empty(fixture.State.GetStatuses(fixture.Author.Id));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("image/gif")]
    [InlineData("")]
    public void UnsupportedStatusKindIsRejected(string kind)
    {
        using var fixture = new Fixture();
        Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, fixture.Request() with { Kind = kind }, out _));
    }

    [Fact]
    public void MalformedSharedBodyEmptyIdAndOversizedBodyRejectedBeforeRetention()
    {
        using var fixture = new Fixture(); var request = fixture.Request();
        foreach (var invalid in new[] { request with { ClientStatusId = Guid.Empty }, request with { Ciphertext = "" },
            request with { Ciphertext = "not base64" }, request with { Ciphertext = Convert.ToBase64String(new byte[StatusProtocol.MaximumBodyBytes + 1]) } })
            Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, invalid, out _));
        Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id, null, out _));
        Assert.Empty(fixture.State.GetStatuses(fixture.Author.Id));
    }

    [Fact]
    public void FiftyChosenViewersAreAcceptedButLargerAudienceIsRejected()
    {
        using var fixture = new Fixture(); var chosen = Enumerable.Range(0, 50).Select(i => fixture.User("viewer" + i).Id).ToArray();
        var request = fixture.Request(audience: chosen); Assert.Equal(51, request.Payloads.Count); var sent = fixture.Add(request);
        foreach (var viewer in chosen) Assert.Equal(viewer, fixture.State.GetStatus(viewer, sent.Id)!.Payload.RecipientId);
        Assert.Empty(fixture.State.GetStatuses(fixture.Member.Id));
        Assert.Equal(StatusResult.Invalid, fixture.State.AddStatus(fixture.Author.Id,
            request with { Payloads = request.Payloads.Append(request.Payloads[0]).ToArray() }, out _));
    }

    [Fact]
    public void ActiveSenderLimitCanFreeOneSlotWithoutReusingDeletedClientIdentity()
    {
        using var fixture = new Fixture(); var sent = Enumerable.Range(0, 5).Select(_ => fixture.Add()).ToArray();
        Assert.Equal(StatusResult.CapacityReached, fixture.State.AddStatus(fixture.Author.Id, fixture.Request(), out _));
        Assert.Equal(StatusResult.Success, fixture.State.DeleteStatus(fixture.Author.Id, sent[0].Id));
        fixture.Add(); Assert.Equal(5, fixture.State.GetStatuses(fixture.Author.Id).Count);
    }

    [Fact]
    public void SharedCiphertextMemoryBudgetCannotBeExceededAcrossManyAuthors()
    {
        using var fixture = new Fixture(); var image = new byte[StatusProtocol.MaximumBodyBytes];
        var accepted = 0; var rejected = false;
        for (var index = 0; index < 70; index++)
        {
            var author = fixture.User("large" + index);
            var result = fixture.State.AddStatus(author.Id, fixture.Request(author.Id, content: image, kind: "image/png"), out _);
            if (result == StatusResult.Success) accepted++;
            else { Assert.Equal(StatusResult.CapacityReached, result); rejected = true; break; }
        }
        Assert.InRange(accepted, 60, 64); Assert.True(rejected);
        Assert.Equal(accepted, fixture.State.GetStatuses(fixture.Member.Id).Count);
    }

    [Fact]
    public void GlobalActiveStatusCountIsBoundedIndependentlyOfSmallBodySize()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 40; index++)
        {
            var author = fixture.User("small" + index);
            for (var count = 0; count < 5; count++) fixture.Add(fixture.Request(author.Id), author.Id);
        }
        Assert.Equal(200, fixture.State.GetStatuses(fixture.Member.Id).Count);
        Assert.Equal(StatusResult.CapacityReached, fixture.State.AddStatus(fixture.Author.Id, fixture.Request(), out _));
    }

    [Fact]
    public void RestartRetainsOnlyEncryptedActiveAudienceAndMissingLegacyFieldIsEmpty()
    {
        using var fixture = new Fixture(); var sent = fixture.Add(); var json = Snapshot(fixture.State);
        Restore(fixture.State, json); Assert.Equal(sent, fixture.State.GetStatus(fixture.Author.Id, sent.Id));
        Assert.NotNull(fixture.State.GetStatus(fixture.Member.Id, sent.Id)); Assert.Null(fixture.State.GetStatus(fixture.Outside.Id, sent.Id));
        var snapshot = JsonNode.Parse(json)!.AsObject(); snapshot.Remove("Statuses"); Restore(fixture.State, snapshot.ToJsonString());
        Assert.Empty(fixture.State.GetStatuses(fixture.Author.Id));
        Assert.NotNull(fixture.State.GetDevice(fixture.Member.Id));
        Assert.Contains(fixture.State.GetConversations(fixture.Admin.Id), room => room.Id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    }

    [Fact]
    public void RestoreSkipsCorruptedNullSignatureScopeExpiryAndMissingSenderEnvelope()
    {
        using var fixture = new Fixture(); fixture.Add(); var original = JsonNode.Parse(Snapshot(fixture.State))!.AsObject();
        foreach (var mutation in new Action<JsonObject>[]
        {
            row => row["Id"] = Guid.Empty,
            row => row["CreatedAt"] = DateTimeOffset.MaxValue,
            row => row["ExpiresAt"] = fixture.Time.Now.AddHours(25),
            row => row["Ciphertext"] = "bad",
            row => row["Payloads"]![0]!["Signature"] = Convert.ToBase64String(new byte[64]),
            row => row["Payloads"]!.AsArray().RemoveAt(1)
        })
        {
            var snapshot = original.DeepClone().AsObject(); mutation(snapshot["Statuses"]![0]!.AsObject());
            Restore(fixture.State, snapshot.ToJsonString()); Assert.Empty(fixture.State.GetStatuses(fixture.Author.Id));
        }
        var nullRow = original.DeepClone().AsObject(); nullRow["Statuses"]!.AsArray().Insert(0, null);
        Restore(fixture.State, nullRow.ToJsonString()); Assert.Single(fixture.State.GetStatuses(fixture.Author.Id));
    }

    [Fact]
    public void FailedPersistenceRollsBackPublishDeleteAndExpiryWithoutNetwork()
    {
        using var fixture = new Fixture(); var sent = fixture.Add();
        typeof(ChatState).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(fixture.State, new ChatDatabase(Options.Create(new DatabaseOptions { Provider = "disabled" })));
        var before = Snapshot(fixture.State);
        Assert.Throws<InvalidOperationException>(() => fixture.State.AddStatus(fixture.Author.Id, fixture.Request(), out _));
        Assert.Equal(before, Snapshot(fixture.State));
        Assert.Throws<InvalidOperationException>(() => fixture.State.DeleteStatus(fixture.Author.Id, sent.Id));
        Assert.Equal(before, Snapshot(fixture.State));
        fixture.Time.Now = sent.ExpiresAt;
        Assert.Throws<InvalidOperationException>(() => fixture.State.RemoveExpiredStatuses());
        Assert.Equal(before, Snapshot(fixture.State));
    }
}
