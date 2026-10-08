using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class GroupMemberRemovalQA
{
    internal static IReadOnlyList<string> Verify(string directory) => MainForm.VerifyGroupMemberRemoval(directory);
}

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyGroupMemberRemoval(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException("Group member removal QA: " + text);
            checks.Add(text);
        }
        var owner = new ChatUser(Guid.NewGuid(), "Grup yöneticisi", "", false, null, "user");
        var member = new ChatUser(Guid.NewGuid(), "Deniz Yılmaz", "", false, null, "user");
        var alternate = member with { Id = Guid.NewGuid(), DisplayName = "Elif Kaya" };
        GroupMemberView Row(ChatUser user, string role = "user", bool banned = false) => new(user, role, new(user.Id, false, null, banned));
        var target = Row(member);
        var room = new GroupManagementView(Guid.NewGuid(), "MTK Tasarım Ekibi", "admin", [Row(owner, "admin"), target, Row(alternate)]);
        var site = owner with { Role = "admin" };
        Require(GroupManagementForm.CanRemoveMember(owner, room, target), "A member group administrator can remove an ordinary user");
        Require(GroupManagementForm.CanRemoveMember(owner, room, target with { GroupRole = "mod" }), "A group administrator can remove a group moderator");
        Require(GroupManagementForm.CanRemoveMember(owner, room, Row(member, banned: true)), "Removal of a banned member is separate from clearing their ban");
        Require(!GroupManagementForm.CanRemoveMember(owner, room with { Members = [Row(owner, "admin", banned: true), target] }, target),
            "A group-banned actor cannot expose the removal action even with a local admin role");
        Require(!GroupManagementForm.CanRemoveMember(owner, room with { MyRole = "mod" }, target) &&
            !GroupManagementForm.CanRemoveMember(owner, room with { MyRole = "user" }, target), "Mods and ordinary users do not expose member removal");
        Require(!GroupManagementForm.CanRemoveMember(owner, room with { Members = [target] }, target), "A stale local admin role without membership cannot expose removal");
        Require(!GroupManagementForm.CanRemoveMember(owner, room, Row(owner)) &&
            !GroupManagementForm.CanRemoveMember(owner, room, Row(member with { IsAgent = true })) &&
            !GroupManagementForm.CanRemoveMember(owner, room, Row(member with { Role = "admin" })) &&
            !GroupManagementForm.CanRemoveMember(owner, room, Row(member, "admin")), "Self, bots, site admins and peer group admins are protected");
        Require(GroupManagementForm.CanRemoveMember(site, room with { IsSiteAdmin = true, Members = [target] }, Row(member, "admin")),
            "A site admin can remove a local group admin without becoming a private-group reader");
        Require(!GroupManagementForm.CanRemoveMember(owner, room with { IsSiteAdmin = true, MyRole = "mod" }, target) &&
            !GroupManagementForm.CanRemoveMember(owner with { IsAgent = true }, room, target), "A forged site-admin flag or bot identity is not enough");

        using var handler = new GroupMemberRemovalQaHandler(room, member.Id);
        using var api = new ChatApiClient("https://member-removal-qa.invalid/chat/", handler);
        var confirm = false;
        using (var management = new GroupManagementForm(api, owner, room.Id, snapshot: true, confirmRemovalForQa: _ => confirm))
        {
            management.SetGroupsForQa([room]); management.Show(); Application.DoEvents(); management.SelectMemberForQa(member.Id);
            Require(management.RemoveEnabledForQa, "The themed group-management action enables for the selected removable member");
            Require(management.ActionButtonsForQa.Any(button => button.Text == "Gruptan çıkar" && button.Kind == ButtonKind.Secondary && button.ForeColor == Theme.Danger),
                "Member removal uses the same visible outlined shape as other commands while retaining red ink");
            ManagementActionQA.Verify(management.ActionButtonsForQa, Require, "Normal group management");
            Capture(management, "group-member-removal-management.png");
            var normalSize = management.Size;
            management.Size = management.MinimumSize; management.PerformLayout(); Application.DoEvents();
            ManagementActionQA.Verify(management.ActionButtonsForQa, Require, "Compact group management");
            Capture(management, "group-member-removal-management-compact.png");
            management.Size = normalSize; management.PerformLayout(); Application.DoEvents();
            HistoryQaPump(management.RemoveMemberAsync());
            Require(handler.Removes == 0 && management.MembersForQa.Contains(member.Id), "Declining confirmation preserves the member and sends no request");
            confirm = true; handler.FailRemove = true;
            HistoryQaPump(management.RemoveMemberAsync());
            Require(handler.Removes == 1 && management.Visible && management.RemoveEnabledForQa &&
                management.MembersForQa.Contains(member.Id) && management.StatusForQa.Length > 0,
                "A denied removal stays in the dialog with inline feedback and the member intact");
            handler.FailRemove = false;
            HistoryQaPump(management.RemoveMemberAsync());
            Require(handler.Removes == 2 && handler.Gets == 1 && handler.LastGroup == room.Id && handler.LastTarget == member.Id,
                "One confirmed action posts the exact group and target then refreshes membership once");
            Require(management.CurrentRoomForQa == room.Id && !management.MembersForQa.Contains(member.Id),
                "Reload preserves the intended group even when the server's list order changes");
            Require(management.StatusForQa.Contains("çıkarıldı") && !management.BusyForQa, "Successful removal is acknowledged without closing the management panel");
            management.SetGroupsForQa([room with { MyRole = "mod" }]); management.SelectMemberForQa(member.Id);
            Require(!management.RemoveEnabledForQa, "Switching to mod-only authority immediately disables removal");
            HistoryQaPump(management.RemoveMemberAsync()); Require(handler.Removes == 2, "Calling removal under a mod role cannot bypass the disabled action");
        }

        using (var reloadHandler = new GroupMemberRemovalQaHandler(room, member.Id) { FailReload = true })
        using (var reloadApi = new ChatApiClient("https://member-reload-qa.invalid/chat/", reloadHandler))
        using (var management = new GroupManagementForm(reloadApi, owner, room.Id, snapshot: true, confirmRemovalForQa: _ => true))
        {
            management.SetGroupsForQa([room]); management.Show(); management.SelectMemberForQa(member.Id);
            HistoryQaPump(management.RemoveMemberAsync());
            Require(!management.MembersForQa.Contains(member.Id) && management.StatusForQa.Contains("çıkarıldı") &&
                management.StatusForQa.Contains("yenilenemedi"), "A failed reload never resurrects an already-removed row or suggests the write failed");
        }

        using (var capturedHandler = new GroupMemberRemovalQaHandler(room, member.Id))
        using (var capturedApi = new ChatApiClient("https://member-captured-qa.invalid/chat/", capturedHandler))
        {
            GroupManagementForm? management = null;
            management = new GroupManagementForm(capturedApi, owner, room.Id, snapshot: true, confirmRemovalForQa: _ =>
            { management!.SelectMemberForQa(alternate.Id); return true; });
            using (management)
            {
                management.SetGroupsForQa([room]); management.Show(); management.SelectMemberForQa(member.Id);
                HistoryQaPump(management.RemoveMemberAsync());
                Require(capturedHandler.LastTarget == member.Id && management.MembersForQa.Contains(alternate.Id),
                    "Focus changes during confirmation cannot redirect removal to another user");
            }
        }

        using (var heldHandler = new GroupMemberRemovalQaHandler(room, member.Id) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://member-held-qa.invalid/chat/", heldHandler))
        using (var management = new GroupManagementForm(heldApi, owner, room.Id, snapshot: true, confirmRemovalForQa: _ => true))
        {
            management.SetGroupsForQa([room]); management.Show(); management.SelectMemberForQa(member.Id);
            var removing = management.RemoveMemberAsync(); HistoryQaUntil(() => heldHandler.Removes == 1);
            HistoryQaPump(management.RemoveMemberAsync()); management.Close(); Application.DoEvents();
            Require(management.BusyForQa && !management.RemoveEnabledForQa && management.Visible && heldHandler.Removes == 1,
                "An in-flight removal blocks duplicate requests and accidental window close");
            heldHandler.Hold!.SetResult(true); HistoryQaPump(removing);
            Require(!management.BusyForQa && !management.MembersForQa.Contains(member.Id), "A held removal completes once on the owning UI thread");
        }
        using (var cancelHandler = new GroupMemberRemovalQaHandler(room, member.Id) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var cancelApi = new ChatApiClient("https://member-cancel-qa.invalid/chat/", cancelHandler))
        {
            var management = new GroupManagementForm(cancelApi, owner, room.Id, snapshot: true, confirmRemovalForQa: _ => true);
            management.SetGroupsForQa([room]); management.Show(); management.SelectMemberForQa(member.Id);
            var removing = management.RemoveMemberAsync(); HistoryQaUntil(() => cancelHandler.Removes == 1);
            management.Dispose(); HistoryQaPump(removing);
            Require(removing.IsCompletedSuccessfully && cancelHandler.Canceled, "Disposal cancels pending removal without accessing disposed controls or leaking an async exception");
        }
        using (var confirmation = new GroupMemberRemovalConfirmationForm("Deniz Yılmaz adlı kullanıcı MTK Tasarım Ekibi grubundan çıkarılsın mı?"))
        {
            confirmation.Show(); Application.DoEvents(); Capture(confirmation, "group-member-removal-confirmation.png");
            ManagementActionQA.Verify(confirmation.Controls.Cast<Control>().SelectMany(Descendants).OfType<ModernButton>(), Require,
                "Member-removal confirmation");
            Require(confirmation.AcceptButton is null && confirmation.CancelButton is not null,
                "The themed confirmation requires an explicit destructive click; Enter does not silently accept");
            confirmation.Close(); Require(confirmation.DialogResult != DialogResult.OK, "Window close cancels the removal confirmation");
        }
        return checks;

        void Capture(Form form, string name)
        {
            form.PerformLayout(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(directory, name));
        }
        static IEnumerable<Control> Descendants(Control control)
        {
            yield return control;
            foreach (var child in control.Controls.Cast<Control>())
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class GroupMemberRemovalQaHandler(GroupManagementView room, Guid target) : HttpMessageHandler
    {
        internal bool FailRemove, FailReload, Canceled;
        internal int Removes, Gets;
        internal Guid? LastGroup, LastTarget;
        internal TaskCompletionSource<bool>? Hold;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/chat/api/groups/manageable")
            {
                Gets++;
                if (FailReload) return Denied();
                var other = room with { Id = Guid.NewGuid(), Title = "Başka grup" };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { other,
                    room with { Members = room.Members.Where(member => member.User.Id != target).ToArray() } }) };
            }
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != $"/chat/api/conversations/{room.Id}/members/{target}/remove")
                throw new InvalidOperationException("Unexpected member removal QA route.");
            Removes++; LastGroup = room.Id; LastTarget = target;
            if (Hold is not null)
            {
                try { await Hold.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Canceled = true; throw; }
            }
            return FailRemove ? Denied() : new(HttpStatusCode.NoContent);
        }
        private static HttpResponseMessage Denied() => new(HttpStatusCode.Forbidden)
        { Content = JsonContent.Create(new ApiError("group_member_remove_denied", "Grup yöneticisi yetkisi gerekli.")) };
    }
}
