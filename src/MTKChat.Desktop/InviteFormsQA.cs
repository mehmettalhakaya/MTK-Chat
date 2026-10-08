using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyGroupInvites(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form; var room = fixture.InitialRoom; var actor = form._session!.User;
        var checks = new List<string>();
        void Require(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException("Group invitation QA: " + text);
            checks.Add(text);
        }
        const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string otherToken = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        const string prefix = "https://mtkaya.me/chat/invite/#";
        var link = prefix + token;
        Require(GroupInviteLink.TryParse(link, out var parsed) && parsed == token,
            "A canonical HTTPS invitation reads its exact 43-character fragment token");
        Require(GroupInviteLink.TryParse(" \tHTTPS://MTKAYA.ME/chat/invite/#" + token + "\r\n", out parsed) && parsed == token,
            "Outer paste whitespace and case-insensitive HTTPS/host remain safe");
        foreach (var invalid in new[] { "", token, "http://mtkaya.me/chat/invite/#" + token,
            "https://evil.invalid/chat/invite/#" + token, "https://mtkaya.me.evil.invalid/chat/invite/#" + token,
            "https://user@mtkaya.me/chat/invite/#" + token, "https://mtkaya.me:443/chat/invite/#" + token,
            "https://mtkaya.me:444/chat/invite/#" + token, "https://mtkaya.me/chat/invite/?x=1#" + token,
            "https://mtkaya.me/chat/invite/" + token, prefix + token + "/", prefix + token[..42], prefix + token + "A",
            prefix + token[..42] + ".", prefix + "%41" + token[1..], "https://mtkaya.me/CHAT/invite/#" + token,
            "https://mtkaya.me/chat/../chat/invite/#" + token, "https://mtkaya.me\\chat\\invite\\#" + token,
            prefix + token + "?x=1", prefix + token + "#x", "file:///chat/invite/#" + token })
            Require(!GroupInviteLink.TryParse(invalid, out var rejected) && rejected.Length == 0,
                "Noncanonical or external invitation input is rejected locally, case " + checks.Count);
        Require(GroupInviteLink.Active(DateTimeOffset.MaxValue, true) &&
            !GroupInviteLink.ValidExpiry(DateTimeOffset.MaxValue) &&
            !GroupInviteLink.ValidExpiry(DateTimeOffset.UtcNow.AddDays(1), true) &&
            GroupInviteLink.ExpiryText(DateTimeOffset.MaxValue, true) == "Geçerlilik: Sınırsız",
            "Only the explicit canonical unlimited flag permits no-expiry validity and bypasses MaxValue date formatting");
        var normal = actor with { Role = "user" };
        Require(CanRenameGroup(actor, room) && !CanRenameGroup(actor with { IsAgent = true }, room) &&
            !CanRenameGroup(actor, room with { Kind = "direct" }), "Invitation permission excludes bots and direct chats");
        Require(!CanRenameGroup(normal, room with { GroupRoles = null }) &&
            !CanRenameGroup(normal, room with { GroupRoles = new Dictionary<Guid, string> { [normal.Id] = "mod" } }) &&
            CanRenameGroup(normal, room with { GroupRoles = new Dictionary<Guid, string> { [normal.Id] = "admin" } }) &&
            !CanRenameGroup(normal, room with { Participants = [], GroupRoles = new Dictionary<Guid, string> { [normal.Id] = "admin" } }),
            "Only member group admins or site admins can manage links, never mods or nonmember group roles");

        using var handler = new InviteQaHandler(room, actor.Id);
        using var api = new ChatApiClient("https://invitation-qa.invalid/chat/", handler);
        var confirmation = true;
        using (var manager = new GroupInviteForm(api, room, _ => confirmation))
        {
            manager.Show(); Application.DoEvents();
            HistoryQaUntil(() => !manager.BusyForQa);
            Require(handler.Lists == 1 && handler.Creates == 0 && handler.Revokes == 0 &&
                manager.EntryCountForQa == 0 && manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa,
                "Opening invite management only GETs persisted links and never creates, rotates or revokes them");
            Require(manager.ActionButtonsForQa.Any(button => button.Text == "Davet Linkini Sil" && button.ForeColor == Theme.Danger) &&
                manager.ActionButtonsForQa.Any(button => button.Text == "Tüm Davet Linklerini Sil" && button.ForeColor == Theme.Danger),
                "Selected and bulk invitation deletion have distinct explicit captions and red destructive ink");
            ManagementActionQA.Verify(manager.ActionButtonsForQa, Require, "Normal empty invite management");
            manager.SetDurationForQa(45);
            HistoryQaPump(manager.CreateAsync());
            Require(handler.Creates == 1 && handler.LastDuration == 45 && manager.LinkForQa == link && manager.CopyEnabledForQa,
                "An explicit create sends the selected 45-minute duration and displays its validated link");
            var first = handler.Entries.Single();
            manager.SetDurationForQa(3, 1); HistoryQaPump(manager.CreateAsync());
            var second = handler.Entries.Single(e => e.Id != first.Id);
            Require(handler.Creates == 2 && handler.LastDuration == 180 && manager.EntryCountForQa == 2 &&
                handler.Entries.Any(e => e.Id == first.Id && e.InviteUrl == link) && manager.LinkForQa == prefix + otherToken,
                "A custom duration converts hours to minutes while a second link leaves the first link active");
            Require(!manager.HorizontalScrollForQa && !manager.InternalScrollForQa,
                "Normal invite management displays neither native horizontal nor vertical scrollbar");
            ManagementActionQA.Verify(manager.ActionButtonsForQa, Require, "Normal populated invite management");
            Capture(manager, "group-invite-manager.png");
            var normalSize = manager.Size;
            manager.Size = manager.MinimumSize; manager.PerformLayout(); Application.DoEvents();
            var actionBounds = manager.ActionBoundsForQa; var viewport = manager.ContentViewportForQa;
            Require(!manager.HorizontalScrollForQa && !manager.InternalScrollForQa,
                "The compact minimum fits without a one-pixel native scrollbar feedback loop");
            Require(actionBounds.All(bounds => bounds.Width > 0 && bounds.Height > 0 && viewport.Contains(bounds) && manager.ClientRectangle.Contains(bounds)),
                "At the compact minimum all invitation action buttons remain fully inside the visible client viewport");
            Require(!actionBounds.SelectMany((bounds, index) => actionBounds.Skip(index + 1).Select(other => bounds.IntersectsWith(other))).Any(overlap => overlap),
                "Compact invite management keeps create, expiry, copy, delete, refresh and footer buttons nonoverlapping");
            ManagementActionQA.Verify(manager.ActionButtonsForQa, Require, "Compact invite management");
            Capture(manager, "group-invite-manager-compact.png");
            manager.Size = normalSize; manager.PerformLayout(); Application.DoEvents();
            confirmation = false; HistoryQaPump(manager.DeleteAsync()); HistoryQaPump(manager.RevokeAsync());
            Require(handler.Deletes == 0 && handler.Revokes == 0 && manager.LinkForQa == prefix + otherToken,
                "Declining selective or bulk deletion preserves links without issuing a write");
            confirmation = true; manager.SelectForQa(first.Id); manager.SetDurationForQa(2, 2);
            HistoryQaPump(manager.ApplyDurationAsync());
            Require(handler.Updates == 1 && handler.LastDuration == 2880 && handler.LastSelected == first.Id &&
                handler.Entries.Single(e => e.Id == first.Id).InviteUrl == first.InviteUrl &&
                handler.Entries.Single(e => e.Id == second.Id) == second && manager.SelectedIdForQa == first.Id,
                "Applying two days changes only the selected expiry and preserves its token and every other invitation");
            var beforeInvalid = handler.Requests;
            manager.SetDurationForQa(4); HistoryQaPump(manager.CreateAsync()); HistoryQaPump(manager.ApplyDurationAsync());
            Require(handler.Requests == beforeInvalid && !manager.CreateEnabledForQa && !manager.ApplyEnabledForQa && manager.StatusForQa.Length > 0,
                "A duration below five minutes cannot issue create or expiry-update requests");
            manager.SetDurationForQa(366, 2); HistoryQaPump(manager.CreateAsync());
            Require(handler.Requests == beforeInvalid && !manager.CreateEnabledForQa,
                "A duration beyond 365 days is refused before any HTTP write");
            manager.SetDurationForQa(5); manager.SelectForQa(first.Id); HistoryQaPump(manager.DeleteAsync());
            Require(handler.Deletes == 1 && handler.LastSelected == first.Id && manager.EntryCountForQa == 1 &&
                handler.Entries.Single() == second,
                "Confirmed selective delete revokes one invitation without deleting the other link");
            HistoryQaPump(manager.LoadAsync()); manager.SelectForQa(second.Id);
            Require(manager.LinkForQa == second.InviteUrl && manager.CopyEnabledForQa,
                "Explicit refresh reopens persisted invitations without creating replacement links");
            handler.FailList = true; HistoryQaPump(manager.LoadAsync());
            Require(manager.EntryCountForQa == 0 && manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa && manager.StatusForQa.Length > 0,
                "A denied list refresh clears stale link capabilities and reports an inline error");
            handler.FailList = false; HistoryQaPump(manager.LoadAsync());
            handler.ForeignRoomList = true; HistoryQaPump(manager.LoadAsync());
            Require(manager.EntryCountForQa == 0 && !manager.CopyEnabledForQa,
                "A list entry from a different conversation is rejected before presentation");
            handler.ForeignRoomList = false; handler.ForeignLinkList = true; HistoryQaPump(manager.LoadAsync());
            Require(manager.EntryCountForQa == 0 && manager.LinkForQa.Length == 0,
                "A returned external URL cannot enter the invitation list or copy affordance");
            handler.ForeignLinkList = false; handler.DuplicateList = true; HistoryQaPump(manager.LoadAsync());
            Require(manager.EntryCountForQa == 0 && !manager.DeleteEnabledForQa,
                "Duplicate invite identifiers in malformed list data cannot target an ambiguous deletion");
            handler.DuplicateList = false; HistoryQaPump(manager.LoadAsync());
            var expired = new GroupInviteEntry(Guid.NewGuid(), room.Id, prefix + new string('X', 43),
                DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
            var legacy = new GroupInviteEntry(Guid.NewGuid(), room.Id, null, null, DateTimeOffset.UtcNow.AddDays(3));
            var unavailable = new GroupInviteEntry(Guid.NewGuid(), room.Id, null, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
            handler.Entries.Add(expired); handler.Entries.Add(legacy); handler.Entries.Add(unavailable);
            HistoryQaPump(manager.LoadAsync()); manager.SelectForQa(expired.Id);
            Require(!manager.CopyEnabledForQa && manager.ApplyEnabledForQa && manager.DeleteEnabledForQa,
                "Expired invitations cannot be copied but remain individually renewable or deletable");
            manager.SelectForQa(legacy.Id);
            Require(manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa && manager.ApplyEnabledForQa && manager.DeleteEnabledForQa &&
                manager.SelectionDetailsForQa.Contains("yeniden gösterilemiyor", StringComparison.Ordinal),
                "Legacy hash-only links remain visible and manageable without pretending their URL can be recovered");
            manager.SetDurationForQa(15); HistoryQaPump(manager.ApplyDurationAsync());
            Require(manager.SelectedIdForQa == legacy.Id && handler.LastSelected == legacy.Id &&
                handler.Entries.Single(e => e.Id == legacy.Id).InviteUrl is null && !manager.CopyEnabledForQa,
                "Extending a legacy link preserves its unrecoverable URL and existing identity");
            Capture(manager, "group-invite-manager-legacy.png");
            manager.SelectForQa(unavailable.Id);
            Require(manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa && manager.ApplyEnabledForQa && manager.DeleteEnabledForQa &&
                manager.SelectionDetailsForQa.Contains("güvenli depodan açılamadı", StringComparison.Ordinal),
                "A protected newer link unavailable from storage is distinguished from a migrated hash-only link without auto-rotation");
            var storedIds = handler.Entries.Select(e => e.Id).ToArray();
            using (var reopened = new GroupInviteForm(api, room, _ => true))
            {
                var createsBefore = handler.Creates; reopened.Show(); Application.DoEvents(); HistoryQaUntil(() => !reopened.BusyForQa);
                reopened.SelectForQa(second.Id);
                Require(reopened.EntryCountForQa == storedIds.Length && reopened.LinkForQa == second.InviteUrl && handler.Creates == createsBefore,
                    "Closing and reopening management reads every persisted link without rotating any token");
            }
            HistoryQaPump(manager.RevokeAsync());
            Require(handler.Revokes == 1 && handler.Entries.Count == 0 && manager.EntryCountForQa == 0 && manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa,
                "Explicit confirmed bulk revoke clears every invitation without changing member UI");
            handler.FailCreate = true; HistoryQaPump(manager.CreateAsync());
            Require(manager.Visible && manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa && manager.StatusForQa.Length > 0,
                "A denied create stays in the themed dialog and never offers an unverified link");
            handler.FailCreate = false; handler.ForeignLink = true; HistoryQaPump(manager.CreateAsync());
            Require(manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa,
                "A foreign-host link returned by a malformed server reply cannot be copied");
            handler.ForeignLink = false;
            HistoryQaPump(manager.CreateAsync()); handler.FailRevoke = true;
            HistoryQaPump(manager.RevokeAsync());
            Require(manager.Visible && manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa && manager.StatusForQa.Length > 0,
                "A failed revocation never leaves a possibly invalidated invitation offered for copy");
            handler.FailRevoke = false;
        }
        using (var unlimited = new InviteQaHandler(room, actor.Id))
        using (var unlimitedApi = new ChatApiClient("https://unlimited-invitation-qa.invalid/chat/", unlimited))
        using (var manager = new GroupInviteForm(unlimitedApi, room, _ => true))
        {
            manager.Show(); Application.DoEvents(); HistoryQaUntil(() => !manager.BusyForQa);
            manager.SetUnlimitedForQa();
            Require(manager.CreateEnabledForQa && !manager.CustomDurationVisibleForQa,
                "Sınırsız is an explicit selectable duration and hides irrelevant numeric/custom unit editors");
            HistoryQaPump(manager.CreateAsync()); var first = unlimited.Entries.Single();
            Require(unlimited.LastNeverExpires && unlimited.LastDuration == 0 && first.NeverExpires &&
                first.ExpiresAt == DateTimeOffset.MaxValue && manager.CopyEnabledForQa &&
                manager.ExpiryCaptionForQa == "Sınırsız" && manager.SelectionDetailsForQa == "Geçerlilik: Sınırsız",
                "Unlimited creation sends zero plus the explicit flag and displays Sınırsız instead of a fabricated expiry date");
            manager.SetDurationForQa(60); HistoryQaPump(manager.CreateAsync());
            var second = unlimited.Entries.Single(e => e.Id != first.Id);
            manager.SelectForQa(second.Id); manager.SetUnlimitedForQa(); HistoryQaPump(manager.ApplyDurationAsync());
            Require(unlimited.LastNeverExpires && unlimited.LastDuration == 0 &&
                unlimited.Entries.Single(e => e.Id == second.Id) is { NeverExpires: true, ExpiresAt: var noEnd } && noEnd == DateTimeOffset.MaxValue &&
                unlimited.Entries.Single(e => e.Id == first.Id) == first && manager.LinkForQa == second.InviteUrl,
                "A finite-to-unlimited change targets only the selected link and preserves all tokens and other entries");
            manager.SelectForQa(first.Id); manager.SetDurationForQa(120); HistoryQaPump(manager.ApplyDurationAsync());
            Require(!unlimited.LastNeverExpires && unlimited.LastDuration == 120 &&
                !unlimited.Entries.Single(e => e.Id == first.Id).NeverExpires &&
                manager.ExpiryCaptionForQa != "Sınırsız" && manager.CopyEnabledForQa,
                "An unlimited-to-finite change clears the explicit flag and restores actual expiry display");
            manager.SelectForQa(second.Id); HistoryQaPump(manager.LoadAsync());
            Require(manager.SelectedIdForQa == second.Id && manager.CopyEnabledForQa &&
                manager.StatusForQa == "2 davet · 2 aktif" && manager.ExpiryCaptionForQa == "Sınırsız",
                "Refresh counts unlimited links as active without rotating tokens or converting their dates");
            manager.SetUnlimitedForQa(); Capture(manager, "group-invite-manager-unlimited.png");
            manager.Size = manager.MinimumSize; manager.PerformLayout(); Application.DoEvents();
            ManagementActionQA.Verify(manager.ActionButtonsForQa, Require, "Compact unlimited invite management");
            Capture(manager, "group-invite-manager-unlimited-compact.png");
            var beforeInvalid = unlimited.Requests;
            manager.SetDurationForQa(0); HistoryQaPump(manager.CreateAsync()); HistoryQaPump(manager.ApplyDurationAsync());
            Require(unlimited.Requests == beforeInvalid && !manager.CreateEnabledForQa && !manager.ApplyEnabledForQa,
                "A zero custom finite duration cannot implicitly request unlimited validity");
            manager.SetUnlimitedForQa(); unlimited.WrongUnlimitedReply = true; HistoryQaPump(manager.CreateAsync());
            Require(manager.EntryCountForQa == 0 && !manager.CopyEnabledForQa,
                "A server reply omitting the requested unlimited flag never enables copy or silently downgrades the user's request");
            unlimited.WrongUnlimitedReply = false; HistoryQaPump(manager.LoadAsync()); manager.SelectForQa(second.Id);
            unlimited.WrongUnlimitedReply = true; HistoryQaPump(manager.ApplyDurationAsync());
            Require(manager.EntryCountForQa == 0 && !manager.CopyEnabledForQa,
                "An expiry-update reply changing the requested unlimited flag cannot expose an unverified capability");
            unlimited.WrongUnlimitedReply = false; HistoryQaPump(manager.LoadAsync()); manager.SelectForQa(second.Id);
            HistoryQaPump(manager.ApplyDurationAsync());
            Require(manager.CopyEnabledForQa && manager.ExpiryCaptionForQa == "Sınırsız",
                "Explicit refresh and reapply recover correctly after an inconsistent unlimited reply");
            var malformed = first with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.MaxValue, NeverExpires = false };
            unlimited.Entries.Add(malformed); HistoryQaPump(manager.LoadAsync());
            Require(manager.EntryCountForQa == 0 && !manager.CopyEnabledForQa,
                "A maximum compatibility timestamp without its flag is refused rather than inferred unlimited");
            unlimited.Entries.Remove(malformed); unlimited.Entries.Add(malformed with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), NeverExpires = true });
            HistoryQaPump(manager.LoadAsync());
            Require(manager.EntryCountForQa == 0 && !manager.CopyEnabledForQa,
                "An unlimited flag with a finite date is also refused before rendering or copy");
        }
        using (var unlimited = new InviteQaHandler(room, actor.Id) { UnlimitedPreview = true })
        using (var unlimitedApi = new ChatApiClient("https://unlimited-preview-qa.invalid/chat/", unlimited))
        using (var join = new JoinGroupInviteForm(unlimitedApi, actor.Id))
        {
            join.Show(); join.LinkForQa = link; HistoryQaPump(join.PreviewAsync());
            Require(join.JoinEnabledForQa && join.PreviewVisibleForQa && join.DetailsForQa.StartsWith("Geçerlilik: Sınırsız\n", StringComparison.Ordinal) &&
                !join.DetailsForQa.Contains("9999", StringComparison.Ordinal) && unlimited.Joins == 0,
                "Unlimited authenticated preview displays no expiry date but still waits for explicit membership confirmation");
            Capture(join, "group-invite-unlimited-preview.png");
            unlimited.MalformedUnlimitedPreview = true; HistoryQaPump(join.PreviewAsync());
            Require(!join.JoinEnabledForQa && !join.PreviewVisibleForQa,
                "Malformed unlimited preview cannot expose an enabled join button");
            unlimited.MalformedUnlimitedPreview = false; unlimited.NoFlagMaximumPreview = true; HistoryQaPump(join.PreviewAsync());
            Require(!join.JoinEnabledForQa && !join.PreviewVisibleForQa,
                "Legacy preview maximum timestamp is never inferred to have unlimited permission");
            unlimited.NoFlagMaximumPreview = false; HistoryQaPump(join.PreviewAsync()); HistoryQaPump(join.JoinAsync());
            Require(join.JoinedConversation?.Id == room.Id && unlimited.Joins == 1 && unlimited.SecretUrls == 0,
                "A valid unlimited link still uses authenticated explicit join with fragment secret only in POST body");
        }
        foreach (var all in new[] { false, true })
        {
            using var deleteConfirmation = new GroupInviteDeletionConfirmationForm(all
                ? "Bu grubun TÜM davet bağlantıları silinsin mi? Mevcut üyeler grupta kalır."
                : "Yalnız seçilen davet bağlantısı silinecek. Diğer davetler ve mevcut üyeler korunur. Devam edilsin mi?", all);
            deleteConfirmation.Show(); Application.DoEvents();
            ManagementActionQA.Verify(deleteConfirmation.ActionButtonsForQa, Require,
                all ? "Bulk invite-deletion confirmation" : "Selected invite-deletion confirmation");
            Require(deleteConfirmation.AcceptButton is null && deleteConfirmation.CancelButton is not null,
                "Invitation deletion confirmation requires an explicit click; Escape/window close cancel");
            Capture(deleteConfirmation, all ? "group-invite-delete-all-confirmation.png" : "group-invite-delete-confirmation.png");
            deleteConfirmation.Close();
            Require(deleteConfirmation.DialogResult != DialogResult.OK, "Closing an invitation deletion confirmation never accepts deletion");
        }
        using (var join = new JoinGroupInviteForm(api, actor.Id))
        {
            join.Show(); Application.DoEvents();
            Require(!join.PreviewEnabledForQa && !join.JoinEnabledForQa,
                "An empty invitation editor cannot preview or join");
            var before = handler.Requests; join.LinkForQa = "https://evil.invalid/#" + token;
            HistoryQaPump(join.PreviewAsync()); HistoryQaPump(join.JoinAsync());
            Require(handler.Requests == before && join.StatusForQa.Length > 0,
                "An external URL never reaches the API or changes membership");
            join.LinkForQa = link; Require(join.PreviewEnabledForQa && !join.JoinEnabledForQa,
                "Pasting a valid link enables preview only, not automatic entrance");
            var joins = handler.Joins; HistoryQaPump(join.PreviewAsync());
            Require(join.JoinEnabledForQa && join.PreviewVisibleForQa && handler.Joins == joins && handler.Previews == 1,
                "Authenticated preview displays group details but does not perform the separate join command");
            Capture(join, "group-invite-preview.png");
            join.LinkForQa = prefix + otherToken;
            Require(!join.JoinEnabledForQa && !join.PreviewVisibleForQa,
                "Editing the token immediately invalidates the old group's preview and confirmation");
            handler.FailPreview = true; HistoryQaPump(join.PreviewAsync());
            Require(join.Visible && !join.JoinEnabledForQa && !join.PreviewVisibleForQa && join.LinkForQa == prefix + otherToken,
                "A denied preview keeps pasted input but hides all stale group details");
            handler.FailPreview = false;
            handler.ExpiredPreview = true; HistoryQaPump(join.PreviewAsync());
            Require(!join.JoinEnabledForQa && !join.PreviewVisibleForQa && join.StatusForQa.Length > 0,
                "An expired invitation does not expose a join action");
            handler.ExpiredPreview = false; HistoryQaPump(join.PreviewAsync());
            handler.FailJoin = true; HistoryQaPump(join.JoinAsync());
            Require(join.Visible && join.JoinedConversation is null && join.LinkForQa == prefix + otherToken && join.JoinEnabledForQa,
                "Denied entrance preserves pasted input and displays its error inline without closing the dialog");
            handler.FailJoin = false; handler.WrongRoom = true; HistoryQaPump(join.JoinAsync());
            Require(join.JoinedConversation is null && join.Visible,
                "A reply for another room cannot select or grant the wrong membership");
            handler.WrongRoom = false; handler.WrongMember = true; HistoryQaPump(join.JoinAsync());
            Require(join.JoinedConversation is null && join.Visible,
                "A room reply that omits the logged-in actor cannot grant an unverified membership");
            handler.WrongMember = false; HistoryQaPump(join.JoinAsync());
            Require(join.JoinedConversation?.Id == room.Id && join.DialogResult == DialogResult.OK,
                "Explicit successful entrance returns the intended member group to the caller");
        }
        Require(handler.SecretUrls == 0 && handler.LastBodyToken == otherToken,
            "Invitation tokens are sent in JSON bodies, never API URL paths, queries or fragments");
        var requestsBeforeInvalid = handler.Requests;
        try { HistoryQaPump(api.PreviewGroupInviteAsync("invalid")); throw new InvalidOperationException("Invalid token unexpectedly accepted."); }
        catch (ArgumentException) { }
        Require(handler.Requests == requestsBeforeInvalid, "An invalid raw token is refused before HTTP dispatch");

        using (var held = new InviteQaHandler(room, actor.Id) { HoldPreview = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://invitation-held.invalid/chat/", held))
        {
            var join = new JoinGroupInviteForm(heldApi, actor.Id); join.Show(); join.LinkForQa = link;
            var request = join.PreviewAsync(); HistoryQaUntil(() => held.Previews == 1);
            HistoryQaPump(join.PreviewAsync());
            Require(held.Previews == 1 && join.BusyForQa && !join.JoinEnabledForQa,
                "A slow preview has one in-flight request and cannot be joined or duplicated");
            join.Dispose(); HistoryQaPump(request);
            Require(held.Canceled && request.IsCompletedSuccessfully,
                "Disposal cancels preview safely without a disposed-control continuation");
        }
        using (var held = new InviteQaHandler(room, actor.Id) { HoldJoin = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://invitation-join-held.invalid/chat/", held))
        using (var join = new JoinGroupInviteForm(heldApi, actor.Id))
        {
            join.Show(); join.LinkForQa = link; HistoryQaPump(join.PreviewAsync());
            var request = join.JoinAsync(); HistoryQaUntil(() => held.Joins == 1);
            HistoryQaPump(join.JoinAsync()); join.Close(); Application.DoEvents();
            Require(join.Visible && held.Joins == 1 && join.BusyForQa && !join.JoinEnabledForQa,
                "An in-flight join blocks duplicate writes and accidental closing while its result is unknown");
            held.HoldJoin!.SetResult(true); HistoryQaPump(request);
            Require(join.JoinedConversation?.Id == room.Id, "A held entrance returns on its owning UI thread");
        }
        using (var held = new InviteQaHandler(room, actor.Id) { HoldCreate = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://invitation-create-held.invalid/chat/", held))
        using (var manager = new GroupInviteForm(heldApi, room, _ => true))
        {
            manager.Show(); Application.DoEvents(); HistoryQaUntil(() => !manager.BusyForQa);
            var request = manager.CreateAsync(); HistoryQaUntil(() => held.Creates == 1);
            HistoryQaPump(manager.CreateAsync()); HistoryQaPump(manager.RevokeAsync()); manager.Close(); Application.DoEvents();
            Require(manager.Visible && manager.BusyForQa && held.Creates == 1 && held.Revokes == 0 && !manager.CopyEnabledForQa,
                "Creating a link cannot be duplicated, revoked or copied before its result is known");
            held.HoldCreate!.SetResult(true); HistoryQaPump(request);
            Require(manager.CopyEnabledForQa, "A completed held create enables copy only after validated success");
        }
        using (var held = new InviteQaHandler(room, actor.Id) { HoldCreate = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://invitation-create-cancel.invalid/chat/", held))
        {
            var manager = new GroupInviteForm(heldApi, room, _ => true); manager.Show(); Application.DoEvents(); HistoryQaUntil(() => !manager.BusyForQa);
            var request = manager.CreateAsync(); HistoryQaUntil(() => held.Creates == 1);
            manager.Dispose(); HistoryQaPump(request);
            Require(held.Canceled && request.IsCompletedSuccessfully,
                "Disposal also cancels an invitation write without an unhandled UI continuation");
        }
        using (var held = new InviteQaHandler(room, actor.Id) { HoldList = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://invitation-list-cancel.invalid/chat/", held))
        {
            var manager = new GroupInviteForm(heldApi, room, _ => true); manager.Show(); HistoryQaUntil(() => held.Lists == 1);
            var request = manager.LoadAsync(); HistoryQaPump(request); HistoryQaPump(manager.CreateAsync());
            Require(held.Lists == 1 && held.Creates == 0 && manager.BusyForQa,
                "A slow initial listing deduplicates refresh and prevents writes until permission is verified");
            manager.Dispose(); HistoryQaUntil(() => held.Canceled); Application.DoEvents();
            Require(held.Canceled, "Closing a read-only load cancels HTTP without recreating a disposed dialog");
        }
        using (var handlerFailures = new InviteQaHandler(room, actor.Id))
        using (var failureApi = new ChatApiClient("https://invitation-selective-errors.invalid/chat/", handlerFailures))
        using (var manager = new GroupInviteForm(failureApi, room, _ => true))
        {
            manager.Show(); Application.DoEvents(); HistoryQaUntil(() => !manager.BusyForQa);
            HistoryQaPump(manager.CreateAsync()); var selected = handlerFailures.Entries.Single();
            handlerFailures.WrongUpdate = true; HistoryQaPump(manager.ApplyDurationAsync());
            Require(manager.EntryCountForQa == 0 && !manager.CopyEnabledForQa && manager.Visible,
                "A stale or wrong-ID expiry response clears unverifiable capabilities instead of changing another row");
            handlerFailures.WrongUpdate = false; HistoryQaPump(manager.LoadAsync()); manager.SelectForQa(selected.Id);
            handlerFailures.FailDelete = true; HistoryQaPump(manager.DeleteAsync());
            Require(manager.LinkForQa.Length == 0 && !manager.CopyEnabledForQa && manager.Visible && manager.StatusForQa.Length > 0,
                "Failed selected deletion requires a refresh before an uncertain link is offered for copy");
        }
        using (var held = new InviteQaHandler(room, actor.Id) { HoldUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://invitation-update-held.invalid/chat/", held))
        using (var manager = new GroupInviteForm(heldApi, room, _ => true))
        {
            manager.Show(); Application.DoEvents(); HistoryQaUntil(() => !manager.BusyForQa);
            HistoryQaPump(manager.CreateAsync()); var first = held.Entries.Single();
            HistoryQaPump(manager.CreateAsync()); var second = held.Entries.Single(e => e.Id != first.Id);
            manager.SelectForQa(first.Id); manager.SetDurationForQa(120);
            var request = manager.ApplyDurationAsync(); HistoryQaUntil(() => held.Updates == 1);
            manager.SelectForQa(second.Id); HistoryQaPump(manager.ApplyDurationAsync()); manager.Close(); Application.DoEvents();
            Require(held.Updates == 1 && manager.Visible && manager.BusyForQa && !manager.CopyEnabledForQa,
                "A held expiry update captures its original target, suppresses duplicate writes and blocks accidental closing");
            held.HoldUpdate!.SetResult(true); HistoryQaPump(request);
            Require(held.LastSelected == first.Id && held.Entries.Single(e => e.Id == second.Id) == second && manager.SelectedIdForQa == first.Id,
                "A programmatic selection change cannot redirect an in-flight duration update to another invitation");
        }

        var joined = fixture.AddRoom("Davetle eklenen grup");
        form._premiumComposer.Text = "Korunacak taslak 😊"; var version = form._conversationVersion;
        var identity = form._identity; var rendered = form._messageList.Controls.Cast<Control>().ToArray();
        HistoryQaPump(form.ApplyJoinedGroupAsync(joined.Id));
        Require(form._selectedConversation?.Id == room.Id && form._premiumComposer.Text == "Korunacak taslak 😊" &&
            form._conversationVersion == version && rendered.SequenceEqual(form._messageList.Controls.Cast<Control>()) &&
            ReferenceEquals(identity, form._identity), "Joining while text is drafted refreshes the list without moving the draft or encryption identity");
        form._premiumComposer.Text = ""; form._pendingFile = new PendingFile([1, 2, 3], "qa.txt");
        Require(!form.CanSelectJoinedGroup(), "An encrypted-file draft prevents invitation-driven room selection");
        form.ClearPendingFile(); form._pendingVoice = new PendingVoice([1, 2, 3], TimeSpan.FromSeconds(2));
        Require(!form.CanSelectJoinedGroup(), "A voice draft prevents invitation-driven room selection");
        form.ClearPendingVoice(); form._pendingImage = new PendingImage([1, 2, 3], "image/png", "qa.png");
        Require(!form.CanSelectJoinedGroup(), "An image draft prevents invitation-driven room selection");
        form.ClearPendingImage(); form._sending = true;
        Require(!form.CanSelectJoinedGroup(), "An in-flight send never changes recipient after invitation acceptance");
        form._sending = false; form._voiceStopping = true;
        Require(!form.CanSelectJoinedGroup(), "Stopping a recording never changes the draft's destination");
        form._voiceStopping = false; HistoryQaPump(form.ApplyJoinedGroupAsync(joined.Id));
        Require(form._selectedConversation?.Id == joined.Id && ReferenceEquals(identity, form._identity),
            "Without drafts or sends the new member group is selected through the normal encrypted history loader");
        Require(fixture.Dialogs == 0, "Invitation UI regression opens no unexpected modal errors");
        return checks;

        void Capture(Form dialog, string name)
        {
            dialog.PerformLayout(); Application.DoEvents();
            using var image = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, name));
        }
    }

    private sealed class InviteQaHandler(ConversationSummary room, Guid actor) : HttpMessageHandler
    {
        internal int Requests, Creates, Revokes, Previews, Joins, SecretUrls, Lists, Updates, Deletes, LastDuration;
        internal Guid? LastSelected;
        internal readonly List<GroupInviteEntry> Entries = [];
        internal string? LastBodyToken;
        internal bool FailCreate, ForeignLink, ExpiredPreview, FailPreview, FailRevoke, FailJoin, WrongRoom, WrongMember, Canceled;
        internal bool FailList, ForeignRoomList, ForeignLinkList, DuplicateList, WrongUpdate, FailDelete;
        internal bool LastNeverExpires, WrongUnlimitedReply, UnlimitedPreview, MalformedUnlimitedPreview, NoFlagMaximumPreview;
        internal TaskCompletionSource<bool>? HoldPreview, HoldJoin, HoldCreate, HoldList, HoldUpdate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Query.Length != 0 || request.RequestUri.Fragment.Length != 0 ||
                path.Contains("AAAAAAAA", StringComparison.Ordinal) || path.Contains("BBBBBBBB", StringComparison.Ordinal)) SecretUrls++;
            if (path == $"/chat/api/conversations/{room.Id}/invite")
            {
                if (request.Method == HttpMethod.Delete)
                {
                    Revokes++; if (FailRevoke) return Denied(); Entries.Clear(); return Json(new { });
                }
                if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Invite QA unexpected command.");
                Creates++; await Held(HoldCreate);
                if (FailCreate) return Denied();
                var token = new string(Creates == 1 ? 'A' : 'B', 43);
                return Json(new GroupInviteResult(room.Id, room.Title,
                    (ForeignLink ? "https://evil.invalid/chat/invite/#" : "https://mtkaya.me/chat/invite/#") + token,
                    DateTimeOffset.UtcNow.AddDays(7)));
            }
            if (path == $"/chat/api/conversations/{room.Id}/invites")
            {
                if (request.Method == HttpMethod.Get)
                {
                    Lists++; await Held(HoldList); if (FailList) return Denied();
                    var entries = Entries.Select(e => e with
                    {
                        ConversationId = ForeignRoomList ? Guid.NewGuid() : e.ConversationId,
                        InviteUrl = ForeignLinkList && e.InviteUrl is not null ? "https://evil.invalid/#" + new string('A', 43) : e.InviteUrl
                    }).ToList();
                    if (DuplicateList && entries.Count > 0) entries.Add(entries[0]);
                    return Json(entries);
                }
                if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Invite QA unexpected manager command.");
                Creates++; await Held(HoldCreate);
                var createRequest = (await request.Content!.ReadFromJsonAsync<CreateGroupInviteRequest>(cancellationToken))!;
                LastDuration = createRequest.DurationMinutes; LastNeverExpires = createRequest.NeverExpires;
                if (FailCreate) return Denied();
                var created = DateTimeOffset.UtcNow;
                var entry = new GroupInviteEntry(Guid.NewGuid(), room.Id,
                    (ForeignLink ? "https://evil.invalid/chat/invite/#" : "https://mtkaya.me/chat/invite/#") + new string((char)('A' + (Creates - 1) % 26), 43),
                    created, LastNeverExpires ? DateTimeOffset.MaxValue : created.AddMinutes(LastDuration), LastNeverExpires);
                Entries.Add(entry); return Json(WrongUnlimitedReply ? entry with { NeverExpires = !entry.NeverExpires } : entry);
            }
            var managerPrefix = $"/chat/api/conversations/{room.Id}/invites/";
            if (path.StartsWith(managerPrefix, StringComparison.Ordinal))
            {
                var suffix = path[managerPrefix.Length..];
                if (!Guid.TryParse(suffix, out var id)) throw new InvalidOperationException("Invite QA invalid manager identifier.");
                LastSelected = id;
                if (request.Method == HttpMethod.Delete)
                {
                    Deletes++; if (FailDelete) return Denied(); Entries.RemoveAll(e => e.Id == id); return Json(new { });
                }
                if (request.Method != HttpMethod.Put)
                    throw new InvalidOperationException("Invite QA unexpected expiry command.");
                Updates++; await Held(HoldUpdate);
                var changeRequest = (await request.Content!.ReadFromJsonAsync<ChangeGroupInviteDurationRequest>(cancellationToken))!;
                LastDuration = changeRequest.DurationMinutes; LastNeverExpires = changeRequest.NeverExpires;
                var old = Entries.Single(e => e.Id == id); var updated = old with
                    { ExpiresAt = LastNeverExpires ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddMinutes(LastDuration), NeverExpires = LastNeverExpires };
                Entries[Entries.FindIndex(e => e.Id == id)] = updated;
                return Json(WrongUpdate ? updated with { Id = Guid.NewGuid() } :
                    WrongUnlimitedReply ? updated with { NeverExpires = !updated.NeverExpires } : updated);
            }
            if (request.Method != HttpMethod.Post || path is not ("/chat/api/group-invites/preview" or "/chat/api/group-invites/join"))
                throw new InvalidOperationException("Invite QA unexpected route.");
            LastBodyToken = (await request.Content!.ReadFromJsonAsync<GroupInviteTokenRequest>(cancellationToken))!.Token;
            if (path.EndsWith("/preview", StringComparison.Ordinal))
            {
                Previews++; await Held(HoldPreview);
                if (FailPreview) return Denied();
                return Json(new GroupInvitePreview(room.Id, room.Title,
                    (UnlimitedPreview && !MalformedUnlimitedPreview) || NoFlagMaximumPreview ? DateTimeOffset.MaxValue :
                        ExpiredPreview ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddDays(7), false,
                    UnlimitedPreview && !NoFlagMaximumPreview));
            }
            Joins++; await Held(HoldJoin);
            if (FailJoin) return Denied();
            return Json(room with { Id = WrongRoom ? Guid.NewGuid() : room.Id,
                Participants = WrongMember ? room.Participants.Where(user => user.Id != actor).ToArray() : room.Participants });

            async Task Held(TaskCompletionSource<bool>? hold)
            {
                if (hold is null) return;
                try { await hold.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Canceled = true; throw; }
            }
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        private static HttpResponseMessage Denied() => new(HttpStatusCode.Forbidden)
            { Content = JsonContent.Create(new ApiError("invite_denied", "Davet işlemi için yetki veya geçerli bağlantı gerekli.")) };
    }
}
