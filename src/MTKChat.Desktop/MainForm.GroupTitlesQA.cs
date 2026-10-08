using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyGroupTitles(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form; var room = fixture.InitialRoom; var owner = form._session!.User;
        var checks = new List<string>();
        void Require(bool value, string check)
        {
            if (!value) throw new InvalidOperationException("Group title QA: " + check);
            checks.Add(check);
        }
        var normal = owner with { Role = "user" };
        Require(!CanRenameGroup(normal, room) && !CanRenameGroup(owner with { IsAgent = true }, room) &&
            !CanRenameGroup(owner, room with { Kind = "direct" }), "Plain users, bots and direct chats never expose rename");
        Require(CanRenameGroup(normal, room with { GroupRoles = new Dictionary<Guid, string> { [owner.Id] = "admin" } }) &&
            !CanRenameGroup(normal, room with { GroupRoles = new Dictionary<Guid, string> { [owner.Id] = "mod" } }),
            "Only the group's admin role, not mod, exposes rename for a normal site user");
        using var handler = new GroupTitleQaHandler(room.Id);
        using var api = new ChatApiClient("https://rename-qa.invalid/", handler);
        using (var management = new GroupManagementForm(api, normal, room.Id, snapshot: true))
        {
            Require(management.RenameEnabledForQa, "Group admins can rename from the group management panel without global admin access");
            management.SetGroupsForQa([new(room.Id, room.Title, "mod", [], false)]);
            Require(!management.RenameEnabledForQa, "Group mods cannot rename through the management panel");
        }
        using (var editor = new GroupTitleEditorForm(api, room))
        {
            editor.Show(); Application.DoEvents();
            Require(!editor.SaveEnabledForQa, "An unchanged group title cannot issue a write");
            editor.NameForQa = "   "; Require(!editor.SaveEnabledForQa, "A whitespace title cannot be saved");
            editor.NameForQa = "Yeni grup ✨";
            Require(editor.SaveEnabledForQa, "A valid changed name enables save");
            editor.PerformLayout(); Application.DoEvents();
            using var bitmap = new Bitmap(editor.Width, editor.Height);
            editor.DrawToBitmap(bitmap, editor.ClientRectangle); bitmap.Save(Path.Combine(directory, "group-title-editor.png"));
            handler.Fail = true; HistoryQaPump(editor.SaveAsync());
            Require(editor.Visible && editor.UpdatedTitle is null && editor.NameForQa == "Yeni grup ✨" &&
                editor.SaveEnabledForQa && editor.StatusForQa.Length > 0,
                "A denied rename stays in the editor with its draft and retry available, without a message box");
            handler.Fail = false; HistoryQaPump(editor.SaveAsync());
            Require(editor.UpdatedTitle == new GroupTitleResult(room.Id, "Yeni grup ✨") && editor.DialogResult == DialogResult.OK,
                "Successful save returns only the authoritative title for the intended room");
            Require(handler.Writes == 2 && handler.LastTitle == "Yeni grup ✨", "Each deliberate save issues exactly one PUT with the selected room and title");
        }
        using (var heldHandler = new GroupTitleQaHandler(room.Id) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var heldApi = new ChatApiClient("https://rename-held-qa.invalid/", heldHandler))
        using (var editor = new GroupTitleEditorForm(heldApi, room))
        {
            editor.Show(); editor.NameForQa = "Bekleyen ad";
            var saving = editor.SaveAsync(); HistoryQaUntil(() => heldHandler.Writes == 1);
            HistoryQaPump(editor.SaveAsync()); editor.Close(); Application.DoEvents();
            Require(editor.Visible && !editor.SaveEnabledForQa && heldHandler.Writes == 1,
                "An in-flight rename blocks duplicate saves and accidental close until its result is known");
            heldHandler.Hold!.SetResult(true); HistoryQaPump(saving);
            Require(editor.UpdatedTitle?.Title == "Bekleyen ad", "A held write finishes on the UI thread with the accepted title");
        }
        using (var cancelHandler = new GroupTitleQaHandler(room.Id) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var cancelApi = new ChatApiClient("https://rename-cancel-qa.invalid/", cancelHandler))
        {
            var closing = new GroupTitleEditorForm(cancelApi, room); closing.Show(); closing.NameForQa = "İptal edilen taslak";
            var saving = closing.SaveAsync(); HistoryQaUntil(() => cancelHandler.Writes == 1);
            closing.Dispose(); HistoryQaPump(saving);
            Require(saving.IsCompletedSuccessfully && cancelHandler.Canceled,
                "Disposal cancels a pending rename without touching disposed UI or leaking an unhandled continuation");
        }
        form._premiumComposer.Text = "Korunacak taslak 😊";
        var messages = form._messageList.Controls.Cast<Control>().ToArray(); var version = form._conversationVersion;
        var identity = form._identity;
        HistoryQaPump(form.ApplyGroupTitleAsync(new(room.Id, "Yeni grup ✨")));
        Require(form._selectedConversation?.Id == room.Id && form._selectedConversation.Title == "Yeni grup ✨" &&
            form._conversationTitle.Text == "Yeni grup ✨" && ((ConversationSummary)form._selectedConversationCard!.Tag!).Title == "Yeni grup ✨",
            "Rename updates the selected group's header and list card together");
        Require(form._premiumComposer.Text == "Korunacak taslak 😊" && version == form._conversationVersion &&
            messages.SequenceEqual(form._messageList.Controls.Cast<Control>()) && ReferenceEquals(identity, form._identity),
            "Renaming preserves drafts, rendered history, selection version and encryption identity");
        HistoryQaPump(form._conversationListGate.WaitAsync());
        var pending = form.ApplyGroupTitleAsync(new(room.Id, "Son ad"));
        Require(!pending.IsCompleted, "An authoritative rename waits behind older list requests");
        form._conversationListGate.Release(); HistoryQaPump(pending);
        Require(form._conversationTitle.Text == "Son ad", "After an older list request finishes the newest title wins");
        Require(fixture.Dialogs == 0, "The rename regression causes no unrelated modal errors");
        return checks;
    }

    private sealed class GroupTitleQaHandler(Guid roomId) : HttpMessageHandler
    {
        internal bool Fail;
        internal int Writes;
        internal string? LastTitle;
        internal TaskCompletionSource<bool>? Hold;
        internal bool Canceled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Put || request.RequestUri!.AbsolutePath != $"/api/conversations/{roomId}/title")
                throw new InvalidOperationException("Unexpected group title QA route.");
            Writes++; LastTitle = (await request.Content!.ReadFromJsonAsync<RenameConversationRequest>(cancellationToken))!.Title;
            if (Hold is not null)
            {
                try { await Hold.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Canceled = true; throw; }
            }
            return Fail
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new ApiError("group_rename_denied", "Grup yöneticisi yetkisi gerekli.")) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new GroupTitleResult(roomId, LastTitle)) };
        }
    }
}
