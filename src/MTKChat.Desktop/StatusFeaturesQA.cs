using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class StatusFeaturesQA
{
    internal static IReadOnlyList<string> Verify(string directory) => MainForm.VerifyStatusFeatures(directory);
}

internal sealed partial class MainForm
{
    // Owned synthetic windows and an in-process HTTP handler only. These probes
    // do not publish statuses or fetch another person's encrypted live media.
    internal static IReadOnlyList<string> VerifyStatusFeatures(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool value, string title)
        { if (!value) throw new InvalidOperationException("Status features QA: " + title); checks.Add(title); }
        static Guid Id(int number) => Guid.Parse($"57a70000-0000-0000-0000-{number:000000000000}");
        static ChatUser User(int number, string? name = null, bool bot = false) =>
            new(Id(number), name ?? $"Kullanıcı {number:0000}", "", bot, null);
        var owner = User(1, "MTK örnek hesabı");
        var friend = User(2, "Ayşe Deniz");
        var bot = User(3, "Gemini Agent", true);
        var people = Enumerable.Range(10, 3000).Select(number => User(number)).Prepend(friend).Prepend(bot).Append(friend).ToArray();
        using var handler = new StatusFeaturesQaHandler { Users = [friend, bot] };
        using var api = new ChatApiClient("https://status-features-qa.invalid/chat/", handler);
        var prepares = 0;
        Task<SendStatusRequest> Prepare(StatusDraft draft, CancellationToken token)
        {
            prepares++;
            throw new InvalidOperationException("This non-publishing fixture must never prepare/upload a status.");
        }
        using (var composer = new StatusComposerForm(api, people, new AvatarCache(api), Prepare) { ShowInTaskbar = false })
        {
            composer.Show(); Pump(composer);
            Require(composer.AudienceForQa.Length == 0 && !composer.PublishEnabledForQa,
                "New status has no implicit directory-wide audience and cannot publish an empty draft");
            Require(composer.PeopleForQa.FilteredCountForQa == 3001 && composer.PeopleForQa.RowsForQa.All(row => row.User?.IsAgent != true),
                "Directory duplicates are removed and bot accounts never appear in the audience picker");
            Require(composer.PeopleForQa.VisibleRowCountForQa is > 0 and < 20,
                "A 3,000-account status audience directory keeps a bounded recycled control pool");
            Require(Descendants(composer).OfType<CheckedListBox>().Count() == 0 &&
                Descendants(composer).OfType<CheckEdit>().All(check => check.Properties.CheckBoxOptions.Style == CheckBoxStyle.SvgCheckBox1),
                "Status audience selection uses themed DevExpress SVG controls instead of native checkboxes");
            composer.SelectForQa(bot.Id, true); composer.SelectForQa(Id(9999), true);
            Require(composer.AudienceForQa.Length == 0,
                "Bots and IDs absent from the authenticated directory cannot become status recipients");
            composer.TextForQa("    \r\n  "); composer.SelectForQa(friend.Id, true);
            Require(!composer.PublishEnabledForQa, "A selected audience cannot make whitespace-only text publishable");
            composer.TextForQa(new string('a', 2001));
            Require(Descendants(composer).OfType<MemoEdit>().Single().Text.Length <= 2000 || !composer.PublishEnabledForQa,
                "Programmatic text cannot bypass the 2,000-character status limit enforced for typing/paste");
            composer.TextForQa("Bugün yeni bir başlangıç. ✨"); Pump(composer);
            Require(composer.PublishEnabledForQa && composer.AudienceForQa.SequenceEqual([friend.Id]),
                "Nonempty text and one explicitly selected person enable publish without performing a write");
            var friendRow = composer.PeopleForQa.RowsForQa.Single(row => row.User?.Id == friend.Id);
            friendRow.ClickForQa(); Pump(composer);
            Require(composer.AudienceForQa.Length == 0 && !composer.PublishEnabledForQa,
                "One actual audience-row click removes exactly one selection and disables publish");
            friendRow.ClickForQa(); Pump(composer);
            Require(composer.AudienceForQa.SequenceEqual([friend.Id]) && friendRow.SelectorForQa.Checked,
                "The actual audience-row click restores one checked recipient without a double toggle");
            var search = Descendants(composer).OfType<TextEdit>().Single(edit => edit is not MemoEdit);
            search.Text = "Kullanıcı 3010"; Pump(composer);
            Require(composer.PeopleForQa.FilteredCountForQa == 0 && composer.AudienceForQa.SequenceEqual([friend.Id]),
                "An audience search with no results preserves an explicitly selected hidden recipient");
            search.Text = ""; Pump(composer);
            Require(composer.PeopleForQa.RowsForQa.Any(row => row.User?.Id == friend.Id && row.SelectorForQa.Checked),
                "Clearing audience search restores checked state by immutable account ID");
            foreach (var member in people.Where(person => !person.IsAgent).DistinctBy(person => person.Id).Take(51))
                composer.SelectForQa(member.Id, true);
            Require(composer.AudienceForQa.Length == 50 && composer.PublishEnabledForQa,
                "Audience limit rejects a fifty-first person without losing the first fifty selections");
            composer.PeopleForQa.ScrollToEndForQa(); Pump(composer);
            Require(composer.PeopleForQa.RowsForQa.Any(row => row.User?.Id == Id(3009)) && composer.AudienceForQa.Length == 50,
                "Recycled audience controls reach the final account without losing hidden choices");
            composer.PeopleForQa.NavigateForQa(Id(3009), Keys.Home); Pump(composer);
            foreach (var size in new[] { new Size(620, 800), new Size(760, 900), composer.MinimumSize })
            {
                composer.Size = size; Pump(composer);
                Require(composer.PeopleForQa.ClientSize.Height > 0 && composer.PeopleForQa.ClientSize.Width > 0,
                    "Status audience viewport remains usable at " + size);
                foreach (var row in composer.PeopleForQa.RowsForQa)
                    Require(row.ClientRectangle.Contains(row.SelectorForQa.Bounds) && row.Controls.OfType<Label>().All(label => label.Right <= row.SelectorForQa.Left),
                        "Audience name/role and SVG selection do not intersect at " + size);
                VerifyButtons(composer, size.ToString());
            }
            composer.Size = new Size(620, 800); Pump(composer); Capture(composer, "status-composer.png");
            Require(prepares == 0 && handler.Posts == 0 && handler.Requests.Count == 0,
                "Draft editing, audience selection and virtualization trigger neither cryptographic preparation nor network traffic");
            composer.Hide();
        }

        var now = DateTimeOffset.UtcNow;
        StatusSummary Summary(int id, Guid sender, string kind = "text", DateTimeOffset? expires = null) =>
            new(Id(id), Id(id + 100), sender, kind, now.AddMinutes(-id), expires ?? now.AddHours(23));
        var mine = Summary(20, owner.Id);
        var shared = Summary(21, friend.Id, "image/jpeg");
        handler.Feed = [shared, Summary(22, bot.Id), Summary(23, Id(9999)), Summary(24, friend.Id, expires: now.AddSeconds(-1)), mine,
            new StatusSummary(Guid.Empty, Id(25), friend.Id, "text", now, now.AddHours(23))];
        var views = new List<Guid>(); var creates = 0;
        using var host = new Form { Text = "MTK Chat · Synthetic statuses QA", ClientSize = new Size(375, 700),
            AutoScaleMode = AutoScaleMode.None, BackColor = Theme.Sidebar, ShowInTaskbar = false };
        using var panel = new StatusesPanel(api, owner, new AvatarCache(api), () => { creates++; return Task.CompletedTask; },
            summary => { views.Add(summary.Id); return Task.CompletedTask; }) { Dock = DockStyle.Fill };
        host.Controls.Add(panel); host.Show(); Pump(host);
        Require(handler.Requests.Count == 0, "Opening the status control does not implicitly fetch or post media");
        HistoryQaPump(panel.LoadAsync()); Pump(host);
        Require(panel.FeedCountForQa == 2 && handler.FeedGets == 1 && handler.BodyGets == 0 && handler.Posts == 0,
            "Feed downloads only metadata and filters expired/bot/unknown/empty-ID senders without reading media");
        Require(Descendants(panel).Where(control => control.Tag is StatusSummary).First().Tag is StatusSummary first && first.SenderId == owner.Id,
            "The author's own status appears ahead of shared statuses");
        Require(Descendants(panel).OfType<ModernButton>().Count(button => button.Text == "Sil") == 1,
            "Only the author's own status has a destructive delete action");
        VerifyAvatars("Narrow status drawer");
        VerifyButtons(panel, "Narrow status drawer"); Capture(panel, "status-drawer-300.png");
        host.ClientSize = new Size(525, 700); Pump(host);
        VerifyAvatars("Wide status drawer");
        VerifyButtons(panel, "Wide status drawer"); Capture(panel, "status-drawer-420.png");
        var sharedCard = Descendants(panel).Single(control => control.Tag is StatusSummary summary && summary.Id == shared.Id);
        sharedCard.Controls.Cast<Control>().SelectMany(Descendants).OfType<ModernButton>().Single(button => button.Text == "Göster").PerformClick();
        HistoryQaUntil(() => !panel.IsBusy); Pump(host);
        Require(views.SequenceEqual([shared.Id]) && handler.BodyGets == 0 && handler.Posts == 0,
            "One explicit Göster click invokes only its metadata-selected viewer callback and does not auto-publish");
        Descendants(panel).OfType<ModernButton>().Single(button => button.Text.Contains("Yeni durum", StringComparison.Ordinal)).PerformClick();
        HistoryQaUntil(() => !panel.IsBusy); Pump(host);
        Require(creates == 1 && handler.Posts == 0,
            "One Yeni durum click opens the explicit compose callback without posting a status");

        handler.FeedStatus = HttpStatusCode.BadRequest;
        HistoryQaPump(panel.LoadAsync()); Pump(host);
        Require(panel.FeedCountForQa == 2 && panel.FeedbackForQa.Contains("Durumlar alınamadı", StringComparison.Ordinal),
            "A failed metadata read preserves the last confirmed feed and displays a retryable inline error");
        handler.FeedStatus = HttpStatusCode.OK;
        handler.HoldFeed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.IgnoreCancellation = true;
        var canceledFeed = panel.LoadAsync(); Pump(host); panel.CancelPending();
        var hold = handler.HoldFeed; handler.HoldFeed = null; handler.Feed = [shared];
        HistoryQaPump(panel.LoadAsync());
        hold.SetResult(StatusQaJson(new[] { mine })); HistoryQaPump(canceledFeed); Pump(host);
        Require(panel.FeedCountForQa == 1 && Descendants(panel).Where(control => control.Tag is StatusSummary)
            .All(control => ((StatusSummary)control.Tag!).Id == shared.Id),
            "A late canceled feed cannot replace a newer confirmed response after drawer reopening");
        handler.Feed = [];
        HistoryQaPump(panel.LoadAsync()); Pump(host);
        Require(panel.FeedCountForQa == 0 && Descendants(panel).OfType<Label>().Any(label => label.Text.Contains("Henüz durum yok", StringComparison.Ordinal)),
            "An empty metadata feed displays an honest status empty state");
        handler.Feed = [Summary(22, bot.Id), Summary(23, Id(9999))];
        HistoryQaPump(panel.LoadAsync()); Pump(host);
        Require(panel.FeedCountForQa == 0 && Descendants(panel).OfType<Label>().Any(label => label.Text.Contains("Henüz durum yok", StringComparison.Ordinal)),
            "A feed containing only excluded sender rows also displays the empty state rather than a blank drawer");
        handler.HoldFeed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.IgnoreCancellation = false;
        var disposedFeed = panel.LoadAsync(); Pump(host); panel.Dispose(); HistoryQaPump(disposedFeed);
        Require(handler.Posts == 0 && handler.BodyGets == 0 && views.Count == 1 && creates == 1,
            "Closing during a pending feed cancels safely and never writes or opens an unintended viewer");

        var plainText = "Bugün her şey biraz daha güzel. ✨\nYalnızca seçtiğim kişilerle paylaşıyorum.";
        var fakePayload = new EncryptedPayload("synthetic", "", "", "", "", "", owner.Id);
        var stored = new StoredStatus(mine.Id, mine.ClientStatusId, StatusProtocol.ScopeId, owner.Id, "text", mine.CreatedAt, mine.ExpiresAt, "", fakePayload);
        using (var viewer = new StatusViewerForm(owner, stored, Encoding.UTF8.GetBytes(plainText)) { ShowInTaskbar = false })
        {
            viewer.Show(); Pump(viewer);
            Require(Descendants(viewer).OfType<MemoEdit>().Single().Text == plainText &&
                Descendants(viewer).OfType<MemoEdit>().Single().Properties.ReadOnly,
                "Text viewer renders only provided decrypted content in a read-only DevExpress editor");
            VerifyButtons(viewer, "Status text viewer"); Capture(viewer, "status-text-viewer.png"); viewer.Hide();
        }
        foreach (var invalid in new[] { Encoding.UTF8.GetBytes("  "), Encoding.UTF8.GetBytes(new string('a', 2001)), new byte[] { 0xff, 0xff } })
        {
            var rejected = false;
            try { using var viewer = new StatusViewerForm(owner, stored, invalid); }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException) { rejected = true; }
            Require(rejected, "Status viewer rejects blank, oversized or invalid UTF-8 text (" + invalid.Length + " bytes)");
        }
        Require(handler.BodyGets == 0 && handler.Posts == 0 && prepares == 0,
            "All status UI QA uses local synthetic content and metadata-only fake HTTP, with zero live publish/media reads");
        using (var navigation = new MainForm(snapshotMode: true, api) { ShowInTaskbar = false })
        {
            navigation.Show(); navigation.PopulateSnapshot(); Pump(navigation);
            navigation._refreshTimer.Stop(); navigation._readTimer.Stop();
            navigation._premiumComposer.Text = "Sosyal bölmeler arasında korunacak taslak 😊";
            var selected = navigation._selectedConversation;
            var messages = navigation._messageList.Controls.Cast<Control>().ToArray();
            var bounds = navigation._chatSurface!.Bounds;
            var beforeReads = handler.Requests.Count;
            navigation._railStatus!.PerformClick(); Pump(navigation);
            Require(navigation._sidebarPage == SidebarPage.Statuses && navigation._sidebarDrawerTitle!.Text == "Durumlar" &&
                navigation._sidebarStatuses!.Visible && !navigation._sidebarChats!.Visible,
                "The actual status rail opens the modern in-window Durumlar drawer");
            Require(navigation._railStatus.Parent!.ClientRectangle.Contains(navigation._railStatus.Bounds) &&
                navigation._railStatus.VectorIcon == ModernButtonIcon.Status && navigation._railStatus.Text.Length == 0,
                "The status rail has a fully contained font-independent vector hit target");
            Require(ReferenceEquals(selected, navigation._selectedConversation) && bounds == navigation._chatSurface.Bounds &&
                messages.SequenceEqual(navigation._messageList.Controls.Cast<Control>()) && navigation._premiumComposer.Text == "Sosyal bölmeler arasında korunacak taslak 😊",
                "Opening statuses preserves the selected chat, all message controls, chat width and unsent draft");
            Require(handler.Requests.Count == beforeReads && handler.Posts == 0,
                "Snapshot status navigation does not contact a production service or publish an automatic status");
            handler.Feed = [shared]; handler.HoldFeed = null;
            HistoryQaPump(navigation._sidebarStatuses!.LoadAsync()); Pump(navigation);
            Capture(navigation, "status-sidebar-mainform.png");
            var savedPanel = navigation._sidebarStatuses;
            navigation._railStatus.PerformClick(); Pump(navigation);
            Require(navigation._sidebarPage == SidebarPage.Chats && navigation._sidebarChats!.Visible,
                "Clicking the active status rail collapses to chats without changing the draft");
            navigation._railStatus.PerformClick(); Pump(navigation);
            Require(ReferenceEquals(savedPanel, navigation._sidebarStatuses),
                "Reopening a status drawer reuses its account-bound control instead of recreating it on each click");
            navigation._railSettings!.PerformClick(); Pump(navigation);
            var blockedAction = Descendants(navigation._sidebarSettings!).OfType<SimpleButton>().Single(button => button.AccessibleName == "Engellenenler");
            blockedAction.PerformClick(); Pump(navigation);
            Require(navigation._sidebarPage == SidebarPage.Blocked && navigation._sidebarBack!.AccessibleName == "Ayarlara dön",
                "The actual Engellenenler settings action enters a nested drawer with Settings as its return destination");
            navigation._sidebarBack!.PerformClick(); Pump(navigation);
            Require(navigation._sidebarPage == SidebarPage.Settings && navigation._sidebarSettings!.Visible,
                "Back from blocked accounts returns to Settings rather than incorrectly jumping to chats");
            var privacyAction = Descendants(navigation._sidebarSettings!).OfType<SimpleButton>().Single(button => button.AccessibleName == "Gizlilik");
            privacyAction.PerformClick(); Pump(navigation);
            Require(navigation._sidebarPage == SidebarPage.Privacy && navigation._sidebarProfileEditor!.Visible && !navigation._sidebarStatuses!.Visible,
                "Privacy remains a distinct child settings page while the status drawer is hidden and canceled");
            navigation._sidebarBack!.PerformClick(); Pump(navigation); navigation._railChat!.PerformClick(); Pump(navigation);
            Require(navigation._sidebarPage == SidebarPage.Chats && navigation._premiumComposer.Text == "Sosyal bölmeler arasında korunacak taslak 😊" && handler.Posts == 0,
                "Nested settings/status navigation returns to chats with the original draft and zero publish requests");
            navigation.Hide();
        }
        host.Hide();
        return checks;

        void Pump(Form form)
        { for (var i = 0; i < 3; i++) { context.AssertOwner(); form.PerformLayout(); Application.DoEvents(); form.Update(); } }
        void VerifyAvatars(string scope)
        {
            foreach (var avatar in Descendants(panel).OfType<AvatarView>())
            {
                var grid = (TableLayoutPanel)avatar.Parent!;
                var cellWidth = grid.GetColumnWidths()[0];
                var cellHeight = grid.GetRowHeights()[0];
                Require(avatar.Width == avatar.Height && grid.ClientRectangle.Contains(avatar.Bounds) &&
                    Math.Abs(avatar.Left * 2 + avatar.Width - cellWidth) <= 1 &&
                    Math.Abs(avatar.Top * 2 + avatar.Height - cellHeight) <= 1,
                    scope + ": status-card avatar stays square, contained and centered in its actual native grid cell: " +
                    $"avatar={avatar.Bounds}, parent={grid.ClientRectangle}, cell={cellWidth}x{cellHeight}, dpi={avatar.DeviceDpi}");
            }
        }
        void VerifyButtons(Control control, string scope)
        {
            foreach (var button in Descendants(control).OfType<ModernButton>().Where(button => button.Visible && button.Text.Length > 0))
            {
                Require(button.Parent!.ClientRectangle.Contains(button.Bounds) && button.Height >= Math.Round(32 * button.DeviceDpi / 96d),
                    scope + ": visible native button is contained and has a usable click target: " + button.Text);
                var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                Require(text.Height <= button.Height && text.Width <= button.Width,
                    scope + ": actual font caption fits the button surface: " + button.Text);
            }
        }
        void Capture(Control control, string name)
        {
            using var image = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(image, control.ClientRectangle); image.Save(Path.Combine(directory, name), ImageFormat.Png);
        }
        static IEnumerable<Control> Descendants(Control control)
        { foreach (Control child in control.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    }

    private static HttpResponseMessage StatusQaJson<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class StatusFeaturesQaHandler : HttpMessageHandler
    {
        internal ChatUser[] Users = [];
        internal StatusSummary[] Feed = [];
        internal HttpStatusCode FeedStatus = HttpStatusCode.OK;
        internal TaskCompletionSource<HttpResponseMessage>? HoldFeed;
        internal bool IgnoreCancellation;
        internal int FeedGets, BodyGets, Posts;
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Get && path == "/chat/api/users") return StatusQaJson(Users);
            if (request.Method == HttpMethod.Get && path == "/chat/api/statuses")
            {
                FeedGets++;
                if (HoldFeed is { } hold) return IgnoreCancellation ? await hold.Task.ConfigureAwait(false) : await hold.Task.WaitAsync(token).ConfigureAwait(false);
                return FeedStatus == HttpStatusCode.OK ? StatusQaJson(Feed) : new HttpResponseMessage(FeedStatus)
                { Content = JsonContent.Create(new ApiError("synthetic_status_failure", "Synthetic metadata failure.")) };
            }
            if (request.Method == HttpMethod.Post) Posts++;
            if (request.Method == HttpMethod.Get && path.StartsWith("/chat/api/statuses/", StringComparison.Ordinal)) BodyGets++;
            throw new InvalidOperationException("Status metadata UI unexpectedly requested a write or media route.");
        }
    }
}
