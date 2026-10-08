using System.Net;
using System.Net.Http.Json;
using DevExpress.XtraEditors;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifySidebarFeatures()
    {
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var checks = new List<string>();
        void Require(bool valid, string check)
        {
            if (!valid) throw new InvalidOperationException("Sidebar drawer QA: " + check);
            checks.Add(check);
        }
        form._premiumComposer.Text = "Kapanıp açılınca korunacak 😊";
        var selected = form._selectedConversation;
        var card = form._selectedConversationCard;
        var messages = form._messageList.Controls.Cast<Control>().ToArray();
        var chatBounds = form._chatSurface!.Bounds;
        var identity = form._identity;
        foreach (var width in new[] { 1120, 1380, 1536, 1920 })
        {
            form.Size = new Size(width, 860); form.PerformLayout(); Application.DoEvents();
            foreach (var button in new Button[] { form._railChat!, form._premiumAdminButton, form._railSettings!, form._railProfile! })
            {
                var parent = button.Parent!;
                Require(parent.ClientRectangle.Contains(button.Bounds) &&
                    button.Width <= parent.ClientSize.Width - parent.Padding.Horizontal &&
                    Math.Abs(button.Left + button.Width / 2f - parent.ClientSize.Width / 2f) <= 1,
                    $"Rail '{button.AccessibleName}' has a fully visible centered native hit target at {width}px: {button.Bounds}, parent={parent.ClientRectangle}");
            }
            chatBounds = form._chatSurface.Bounds;
            for (var cycle = 0; cycle < 8; cycle++)
            {
                form._railProfile!.PerformClick(); Application.DoEvents();
                Require(form._sidebarPage == SidebarPage.Profile && form._sidebarDrawer!.Visible && !form._sidebarChats!.Visible,
                    $"Profile rail opens an in-window drawer at {width}px, cycle {cycle + 1}");
                Require(form._sidebarProfileEditor!.Visible && form._sidebarPageHost!.ClientRectangle.Contains(form._sidebarDrawer!.Bounds),
                    "The expanded profile fits its sidebar host");
                Require(form._chatSurface.Bounds == chatBounds && form._premiumComposer.Text == "Kapanıp açılınca korunacak 😊" &&
                    ReferenceEquals(form._selectedConversation, selected) && ReferenceEquals(form._selectedConversationCard, card) &&
                    ReferenceEquals(form._identity, identity) && messages.SequenceEqual(form._messageList.Controls.Cast<Control>()),
                    "Opening profile preserves chat width, draft, selection, identity and message controls");
                form._railProfile.PerformClick(); Application.DoEvents();
                Require(form._sidebarPage == SidebarPage.Chats && !form._sidebarDrawer!.Visible && form._sidebarChats!.Visible,
                    "Clicking the active profile icon collapses it back to chat");
            }
            form._railSettings!.PerformClick(); Application.DoEvents();
            Require(form._sidebarPage == SidebarPage.Settings && form._sidebarSettings!.Visible,
                "Settings rail opens real settings, not the profile modal");
            var privacyCard = HistoryQaControls(form._sidebarSettings!).Single(control => control is RoundedPanel && control.AccessibleName == "Gizlilik");
            var privacyAction = privacyCard.Controls.OfType<SimpleButton>().Single();
            Require(privacyCard.Visible && privacyAction.Visible && privacyAction.AllowFocus && privacyAction.TabStop &&
                privacyCard.ClientRectangle.Contains(privacyAction.Bounds) &&
                form._sidebarDrawerBody!.ClientRectangle.Contains(privacyCard.Bounds),
                $"The actual DevExpress Privacy settings action is fully reachable and keyboard-focusable at {width}px: card={privacyCard.Bounds}, body={form._sidebarDrawerBody!.ClientRectangle}");
            privacyAction.PerformClick();
            Application.DoEvents();
            Require(form._sidebarPage == SidebarPage.Privacy && form._sidebarDrawerTitle!.Text == "Gizlilik",
                $"Clicking the real DevExpress Privacy action opens Privacy without returning to Chats: page={form._sidebarPage}, heading={form._sidebarDrawerTitle!.Text}, drawer={form._sidebarDrawer!.Visible}, chats={form._sidebarChats!.Visible}, editor={form._sidebarProfileEditor!.Visible}");
            var back = form._sidebarDrawer!.Controls.OfType<TableLayoutPanel>().Single()
                .Controls.OfType<ModernButton>().Single();
            back.PerformClick(); Application.DoEvents();
            Require(form._sidebarPage == SidebarPage.Settings && form._sidebarSettings!.Visible,
                $"Privacy's actual Back button returns to its Settings parent, not Chats: page={form._sidebarPage}");
            var profileAction = HistoryQaControls(form._sidebarSettings!).OfType<SimpleButton>()
                .Single(control => control.AccessibleName == "Profilim");
            profileAction.PerformClick(); Application.DoEvents();
            Require(form._sidebarPage == SidebarPage.Profile && form._sidebarBack!.AccessibleName == "Ayarlara dön",
                "Profile entered through Settings keeps the Settings return path");
            back.PerformClick(); Application.DoEvents();
            Require(form._sidebarPage == SidebarPage.Settings && form._sidebarSettings!.Visible,
                "Profile's actual Back button returns to Settings without changing conversation or draft");
            var key = new Message();
            Require(form.ProcessCmdKey(ref key, Keys.Control | Keys.K) && form._sidebarPage == SidebarPage.Chats && form._premiumSearch.ContainsFocus,
                "Ctrl+K collapses the drawer and focuses the visible search editor");
            HistoryQaPump(form.OpenSidebarAsync(SidebarPage.Settings));
            Require(form.ProcessCmdKey(ref key, Keys.Escape) && form._sidebarPage == SidebarPage.Chats,
                "Escape returns to the conversation list");
        }
        Require(new[] { form._railChat!, form._premiumAdminButton, form._railSettings! }.All(button => button.Text.Length == 0 && button.VectorIcon != ModernButtonIcon.None),
            "Chat, management and settings rail controls are font-independent vector icons");
        Require(form._railProfile!.Text.Length == 0 && ReferenceEquals(form._accountAvatar.Parent, form._railProfile) &&
            form._railProfile.ClientRectangle.Contains(form._accountAvatar.Bounds) && form._accountAvatar.Width == form._accountAvatar.Height,
            "The profile rail shows its real centered square avatar, not a font icon or duplicate footer card");

        var user = form._session!.User with { PhotoVersion = null };
        var handler = new SidebarProfileHandler(user);
        using var api = new ChatApiClient("https://sidebar-qa.invalid/", handler);
        using var editor = new ProfileEditor(api, user, new AvatarCache(api));
        using var window = new Form { ClientSize = new Size(280, 600) };
        editor.Dock = DockStyle.Fill; window.Controls.Add(editor); window.Show(); Application.DoEvents();
        Require(!editor.SaveEnabledForQa, "An unchanged profile never writes to the server");
        editor.SetMode(privacy: true);
        Require(!editor.SaveEnabledForQa, "Privacy cannot be saved before its server values load");
        handler.FailPrivacy = true;
        HistoryQaPump(editor.LoadPrivacyAsync());
        Require(!editor.SaveEnabledForQa, "A failed privacy GET cannot overwrite existing server settings with defaults");
        handler.FailPrivacy = false;
        HistoryQaPump(editor.LoadPrivacyAsync());
        Require(!editor.SaveEnabledForQa, "Reopening privacy recovers but unchanged values still do not write");
        editor.ChangePrivacyForQa(false, false);
        var readsBeforeDirtyReopen = handler.PrivacyReads;
        HistoryQaPump(editor.LoadPrivacyAsync(refresh: true));
        Require(editor.SaveEnabledForQa && handler.PrivacyReads == readsBeforeDirtyReopen,
            "Reopening privacy preserves explicitly staged unsaved choices");
        HistoryQaPump(editor.SaveAsync());
        Require(handler.SavedPrivacy == new PrivacySettings(false, false) && handler.PrivacyWrites == 1 && !editor.SaveEnabledForQa,
            "Privacy save uses the authenticated endpoint exactly once and resets dirty state");
        handler.PrivacyValue = new PrivacySettings(false, true);
        HistoryQaPump(editor.LoadPrivacyAsync(refresh: true));
        Require(handler.PrivacyReads == readsBeforeDirtyReopen + 1 && !editor.SaveEnabledForQa,
            "Reopening clean privacy fetches external changes without an automatic write");
        editor.RefreshUser(user with { DisplayName = "Güncel ad", Role = "user" });
        Require(editor.HeadingForQa == "Güncel ad · User", "Reopened profile refreshes the account's current name and site role");
        editor.SetMode(privacy: false); editor.StageRemovalForQa(); HistoryQaPump(editor.SaveAsync());
        Require(handler.PhotoDeletes == 1 && editor.UpdatedUser?.Id == user.Id && handler.PrivacyWrites == 1,
            "Photo removal writes only the own-profile endpoint, independently of privacy");
        var cancelHandler = new SidebarProfileHandler(user) { HoldPrivacy = true };
        using var cancelApi = new ChatApiClient("https://sidebar-qa.invalid/", cancelHandler);
        var closing = new ProfileEditor(cancelApi, user, new AvatarCache(cancelApi));
        var loading = closing.LoadPrivacyAsync();
        HistoryQaUntil(() => cancelHandler.PrivacyReads > 0);
        closing.Dispose(); HistoryQaPump(loading);
        Require(cancelHandler.Canceled && loading.IsCompletedSuccessfully,
            "Disposing a drawer cancels an in-flight privacy load without an unhandled continuation");
        window.Hide();
        VerifySidebarNavigation(Require, user);
        return checks;
    }

    private static void VerifySidebarNavigation(Action<bool, string> require, ChatUser template)
    {
        // This is the production-mode click/load/save path, with Shown login
        // detached before showing an app-owned fake window. Every endpoint is
        // handled in process; no production login, account or network is used.
        var handler = new SidebarProfileHandler(template) { FailPrivacy = true };
        using var api = new ChatApiClient("https://sidebar-live-qa.invalid/", handler);
        using var form = new MainForm(snapshotMode: false, api);
        form.Shown -= form.OnShownAsync;
        form.Opacity = 1;
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        handler.ReturnUser = form._session!.User;
        form._refreshTimer.Stop(); form._readTimer.Stop();
        var dialogs = 0;
        form._errorObserverForQa = _ => dialogs++;
        form._premiumComposer.Text = "Gizlilik değişirken korunacak taslak 😊";
        var selected = form._selectedConversation;
        var rows = form._messageList.Controls.Cast<Control>().ToArray();
        SimpleButton Action(string name) => HistoryQaControls(form._sidebarSettings!).OfType<SimpleButton>()
            .Single(button => button.AccessibleName == name);
        void OpenPrivacy()
        {
            form._railSettings!.PerformClick(); Application.DoEvents();
            Action("Gizlilik").PerformClick(); Application.DoEvents();
        }
        OpenPrivacy();
        HistoryQaUntil(() => form._sidebarProfileEditor is { PrivacyLoadingForQa: false });
        var editor = form._sidebarProfileEditor!;
        require(form._sidebarPage == SidebarPage.Privacy && editor.StatusForQa.Contains("alınamadı", StringComparison.Ordinal) &&
            editor.PrivacyRetryForQa.Visible && editor.PrivacyRetryForQa.Enabled && !editor.SaveEnabledForQa && dialogs == 0,
            "The real Privacy action keeps a failed GET inside Privacy with an enabled inline retry, no modal and no stale-default save");
        handler.FailPrivacy = false;
        var reads = handler.PrivacyReads;
        editor.PrivacyRetryForQa.PerformClick();
        HistoryQaUntil(() => !editor.PrivacyLoadingForQa);
        require(handler.PrivacyReads == reads + 1 && editor.PrivacyChoiceForQa == handler.PrivacyValue &&
            !editor.PrivacyRetryForQa.Visible && !editor.SaveEnabledForQa && form._sidebarPage == SidebarPage.Privacy,
            "The actual Privacy retry loads the server's choices without navigating or writing defaults");

        form._sidebarBack!.PerformClick(); Application.DoEvents();
        handler.PrivacyResponse = new TaskCompletionSource<PrivacySettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action("Gizlilik").PerformClick();
        HistoryQaUntil(() => editor.PrivacyLoadingForQa);
        form._sidebarBack!.PerformClick(); Application.DoEvents();
        require(form._sidebarPage == SidebarPage.Settings && !form._sidebarChats!.Visible,
            "Back during a pending Privacy read returns immediately to Settings, not Chats");
        Action("Profilim").PerformClick(); Application.DoEvents();
        editor.StageRemovalForQa();
        var photoStatus = editor.StatusForQa;
        handler.PrivacyResponse!.SetResult(new PrivacySettings(false, true));
        HistoryQaUntil(() => !editor.PrivacyLoadingForQa);
        handler.PrivacyResponse = null;
        require(form._sidebarPage == SidebarPage.Profile && editor.StatusForQa == photoStatus &&
            editor.SaveEnabledForQa && ReferenceEquals(form._sidebarProfileEditor, editor),
            "A late Privacy response cannot reopen its page or erase the current unsaved photo status");

        form._sidebarBack!.PerformClick(); Application.DoEvents();
        Action("Gizlilik").PerformClick(); Application.DoEvents();
        HistoryQaUntil(() => !editor.PrivacyLoadingForQa);
        editor.ChangePrivacyForQa(false, false);
        handler.FailPrivacySave = true;
        var writes = handler.PrivacyWrites;
        HistoryQaPump(editor.SaveAsync());
        require(handler.PrivacyWrites == writes + 1 && !editor.IsBusy && editor.SaveEnabledForQa &&
            editor.StatusForQa.Contains("Kaydedilemedi", StringComparison.Ordinal) && form._sidebarPage == SidebarPage.Privacy,
            "A failed Privacy save keeps choices dirty, stays on Privacy and releases its navigation lock");
        handler.FailPrivacySave = false;
        handler.PrivacySaveResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        writes = handler.PrivacyWrites;
        var save = editor.SaveAsync();
        HistoryQaUntil(() => handler.PrivacyWrites == writes + 1);
        require(editor.IsBusy && !form._sidebarBack!.Enabled && !form._railChat!.Enabled &&
            !form._railSettings!.Enabled && !form._railProfile!.Enabled,
            "The actual navigation controls are visibly disabled while an authenticated save is pending");
        form._sidebarBack!.PerformClick(); form._railChat!.PerformClick(); form._railProfile!.PerformClick();
        var key = new Message(); form.ProcessCmdKey(ref key, Keys.Escape);
        HistoryQaPump(editor.SaveAsync());
        require(form._sidebarPage == SidebarPage.Privacy && handler.PrivacyWrites == writes + 1 && !save.IsCompleted,
            "Back, rail actions, Escape and a second Save cannot interrupt or replay a pending write");
        handler.PrivacySaveResponse!.SetResult(true); HistoryQaPump(save);
        require(!editor.IsBusy && !editor.SaveEnabledForQa && form._sidebarBack!.Enabled && form._railChat!.Enabled &&
            form._railSettings!.Enabled && form._railProfile!.Enabled && form._sidebarPage == SidebarPage.Privacy &&
            handler.SavedPrivacy == new PrivacySettings(false, false),
            "A confirmed save unlocks navigation without changing the active page or resubmitting its choices");
        require(form._premiumComposer.Text == "Gizlilik değişirken korunacak taslak 😊" &&
            ReferenceEquals(selected, form._selectedConversation) && rows.SequenceEqual(form._messageList.Controls.Cast<Control>()) && dialogs == 0,
            "Read retry, nested Back, late completion and failed/successful save preserve the draft, conversation and message controls");
        form.Hide();
    }

    private sealed class SidebarProfileHandler(ChatUser user) : HttpMessageHandler
    {
        internal bool FailPrivacy, FailPrivacySave, HoldPrivacy, Canceled;
        internal int PrivacyReads, PrivacyWrites, PhotoDeletes;
        internal ChatUser ReturnUser = user;
        internal TaskCompletionSource<PrivacySettings>? PrivacyResponse;
        internal TaskCompletionSource<bool>? PrivacySaveResponse;
        internal PrivacySettings? SavedPrivacy;
        internal PrivacySettings PrivacyValue = new(true, true);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/profile/privacy", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                PrivacyReads++;
                if (HoldPrivacy)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { Canceled = true; throw; }
                }
                if (PrivacyResponse is { } hold)
                    PrivacyValue = await hold.Task.WaitAsync(cancellationToken);
                return FailPrivacy ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(PrivacyValue) };
            }
            if (path.EndsWith("/profile/privacy", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                PrivacyWrites++; SavedPrivacy = await request.Content!.ReadFromJsonAsync<PrivacySettings>(cancellationToken);
                if (PrivacySaveResponse is { } hold) await hold.Task.WaitAsync(cancellationToken);
                if (FailPrivacySave) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (SavedPrivacy is { } saved) PrivacyValue = saved;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/profile/photo", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                PhotoDeletes++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ReturnUser) };
            }
            throw new InvalidOperationException("Unexpected synthetic profile route.");
        }
    }
}
