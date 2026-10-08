using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class BlockedUsersQA
{
    internal static IReadOnlyList<string> Verify(string directory) => MainForm.VerifyBlockedUsers(directory);
}

internal sealed partial class MainForm
{
    // Fake authenticated block responses, synthetic avatars and owned WinForms
    // windows only. No live account is logged in, blocked, unblocked or messaged.
    internal static IReadOnlyList<string> VerifyBlockedUsers(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool value, string title)
        { if (!value) throw new InvalidOperationException("Blocked users QA: " + title); checks.Add(title); }
        static Guid Id(int number) => Guid.Parse($"b10c0000-0000-0000-0000-{number:000000000000}");
        static ChatUser User(int number, string name, string role = "user", bool bot = false) =>
            new(Id(number), name, "", bot, null, role);
        var owner = User(1, "QA owner");
        var user = User(2, "Ayşe Deniz");
        var admin = User(3, "MTK account", "admin");
        var bot = User(4, "Gemini Agent", bot: true);
        var unknownId = Id(5);
        var longName = User(6, "Çok uzun kullanıcı adı burada butonla veya avatarla çakışmamalı");
        using var handler = new BlockedUsersQaHandler
        {
            Users = [owner, user, admin, bot, longName],
            Blocked = [user.Id, admin.Id, bot.Id, unknownId, longName.Id, user.Id, Guid.Empty, owner.Id]
        };
        using var api = new ChatApiClient("https://blocked-users-qa.invalid/chat/", handler);
        using var window = new Form
        {
            Text = "MTK Chat · Synthetic blocked users QA", ShowInTaskbar = false,
            BackColor = Theme.Sidebar, AutoScaleMode = AutoScaleMode.None,
            ClientSize = new Size(375, 740)
        };
        using var panel = new BlockedUsersPanel(api, owner.Id, new AvatarCache(api)) { Dock = DockStyle.Fill };
        window.Controls.Add(panel); window.Show(); Pump();
        Require(handler.Requests.Count == 0 && panel.BlockedIds.Count == 0 && !panel.IsBusy,
            "Constructing/showing the block drawer never silently calls the server");
        HistoryQaPump(panel.LoadAsync()); Pump();
        Require(panel.HasLoadedForQa && panel.BlockedIds.Count == 5 && panel.BlockedIds.Distinct().Count() == 5,
            "The authenticated block response is deduplicated into five real account IDs");
        Require(!panel.BlockedIds.Contains(owner.Id) && !panel.BlockedIds.Contains(Guid.Empty),
            "Malformed self/empty IDs cannot appear as unblock actions");
        Require(handler.BlockLoads == 1 && handler.UserLoads == 1 && handler.Deletes.Count == 0,
            "One opening fetches blocks and visible account metadata once with zero writes");
        Require(panel.BlockedIds.Contains(bot.Id) && panel.BlockedIds.Contains(unknownId),
            "Blocked bots and accounts absent from the directory remain visible and removable");
        Require(FindRow(unknownId).AccessibleName == "Kullanıcı · User",
            "Unavailable metadata uses a neutral heading while preserving the original target ID");
        Require(FindRow(user.Id).Controls.OfType<Label>().Single(label => label.Name == "BlockedUserName").ForeColor == Theme.Text &&
            FindRow(admin.Id).Controls.OfType<Label>().Single(label => label.Name == "BlockedUserName").ForeColor == Theme.Warning &&
            FindRow(bot.Id).Controls.OfType<Label>().Single(label => label.Name == "BlockedUserName").ForeColor == Theme.Bot,
            "User/admin/bot headings retain the application's white/gold/violet role colors");
        checks.AddRange(panel.VerifyLayout().Select(check => "Narrow drawer: " + check));
        Capture("blocked-users-300.png");
        window.ClientSize = new Size(525, 740); Pump();
        checks.AddRange(panel.VerifyLayout().Select(check => "Wide drawer: " + check));
        Capture("blocked-users-420.png");
        var confirmed = new List<Guid>(); panel.UserUnblocked += confirmed.Add;
        HistoryQaPump(panel.UnblockAsync(owner.Id));
        HistoryQaPump(panel.UnblockAsync(Id(99)));
        Require(handler.Deletes.Count == 0 && confirmed.Count == 0,
            "Self and absent targets cannot send DELETE or publish a successful unblock");

        handler.UnblockStatus = HttpStatusCode.BadRequest;
        FindRow(user.Id).Controls.OfType<ModernButton>().Single().PerformClick();
        HistoryQaUntil(() => !panel.IsBusy); Pump();
        Require(panel.BlockedIds.Contains(user.Id) && confirmed.Count == 0 && !panel.IsBusy && panel.UnblockButtonsForQa.All(button => button.Enabled),
            "Failed DELETE preserves the blocked row, re-enables actions and publishes no success");
        Require(panel.FeedbackForQa.Contains("Engel kaldırılamadı", StringComparison.Ordinal),
            "An unblock failure is shown inside the drawer, not a blocking MessageBox");

        handler.UnblockStatus = HttpStatusCode.OK;
        handler.HoldUnblock = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingUnblock = panel.UnblockAsync(user.Id); Pump();
        Require(panel.IsBusy && panel.UnblockButtonsForQa.All(button => !button.Enabled),
            "Pending unblock visibly disables all row actions and duplicate writes");
        var deleteCount = handler.Deletes.Count;
        HistoryQaPump(panel.UnblockAsync(user.Id)); HistoryQaPump(panel.LoadAsync());
        Require(handler.Deletes.Count == deleteCount && handler.BlockLoads == 1,
            "A pending DELETE cannot be duplicated or raced by refresh");
        handler.HoldUnblock.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        HistoryQaPump(pendingUnblock); handler.HoldUnblock = null; Pump();
        Require(!panel.BlockedIds.Contains(user.Id) && confirmed.SequenceEqual([user.Id]) && !panel.IsBusy,
            "Exactly one acknowledged DELETE removes one row and publishes exactly its own ID");
        Require(panel.FeedbackForQa == "Engel kaldırıldı." && panel.BlockedIds.Count == 4,
            "Successful unblock keeps every other account blocked and reports inline success");
        Require(handler.Deletes.All(id => id == user.Id) && handler.Requests.All(path => !path.Contains("conversations", StringComparison.Ordinal)),
            "The actual unblock path never creates/selects a direct conversation or changes another target");

        handler.Blocked = [];
        HistoryQaPump(panel.LoadAsync()); Pump();
        Require(panel.BlockedIds.Count == 0 && Descendants(panel).Any(control => control.Name == "BlockedUsersEmpty") &&
            panel.UnblockButtonsForQa.Count == 0,
            "An empty authenticated block list shows a useful empty state with no invented people");
        Capture("blocked-users-empty.png");
        handler.Blocked = [user.Id]; handler.UsersStatus = HttpStatusCode.BadRequest;
        HistoryQaPump(panel.LoadAsync()); Pump();
        Require(panel.BlockedIds.Count == 0 && panel.FeedbackForQa.Contains("Engel listesi alınamadı", StringComparison.Ordinal),
            "A partial GET failure cannot merge an incomplete directory into the last confirmed list");
        handler.UsersStatus = HttpStatusCode.OK;
        HistoryQaPump(panel.LoadAsync()); Pump();
        Require(panel.BlockedIds.SequenceEqual([user.Id]) && panel.FeedbackForQa == "",
            "Explicit refresh recovers from a failed GET with a fully confirmed response");

        handler.HoldBlocks = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.IgnoreBlockCancellation = true;
        var oldLoad = panel.LoadAsync(); Pump();
        var loadsBefore = handler.BlockLoads;
        HistoryQaPump(panel.LoadAsync());
        Require(panel.IsBusy && handler.BlockLoads == loadsBefore,
            "Repeated opening during a load does not start repeated background requests");
        panel.CancelPending();
        Require(!panel.IsBusy, "Closing/canceling the drawer immediately releases busy UI state");
        var oldHold = handler.HoldBlocks; handler.HoldBlocks = null;
        handler.Blocked = [bot.Id];
        HistoryQaPump(panel.LoadAsync()); Pump();
        oldHold.SetResult(BlockedQaJson(new[] { admin.Id }));
        HistoryQaPump(oldLoad); Pump();
        Require(panel.BlockedIds.SequenceEqual([bot.Id]) && confirmed.SequenceEqual([user.Id]),
            "A late canceled response cannot repaint a reopened drawer or emit an unblock event");

        handler.Blocked = [user.Id];
        HistoryQaPump(panel.LoadAsync());
        handler.HoldUnblock = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedUnblock = panel.UnblockAsync(user.Id); Pump();
        panel.Dispose();
        HistoryQaPump(disposedUnblock);
        Require(confirmed.SequenceEqual([user.Id]),
            "Disposing during a pending DELETE cancels callbacks and cannot publish success to a different account");
        handler.HoldUnblock.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK));

        using var errorPanel = new BlockedUsersPanel(api, owner.Id, new AvatarCache(api)) { Dock = DockStyle.Fill };
        window.Controls.Clear(); window.Controls.Add(errorPanel); Pump();
        handler.UsersStatus = HttpStatusCode.BadRequest;
        HistoryQaPump(errorPanel.LoadAsync()); Pump();
        Require(!errorPanel.HasLoadedForQa && Descendants(errorPanel).Any(control => control.Name == "BlockedUsersLoadError") && !errorPanel.IsBusy,
            "An initial list failure offers an honest retry state rather than falsely claiming nobody is blocked");
        Require(handler.Requests.All(path => path.StartsWith("GET /chat/api/blocks", StringComparison.Ordinal) ||
            path.StartsWith("GET /chat/api/users", StringComparison.Ordinal) || path.StartsWith("DELETE /chat/api/blocks/", StringComparison.Ordinal)),
            "All QA traffic stays in the in-process handler and only uses existing block/directory routes");
        window.Hide();
        return checks;

        void Pump()
        { for (var i = 0; i < 3; i++) { context.AssertOwner(); window.PerformLayout(); Application.DoEvents(); window.Update(); } }
        Control FindRow(Guid id) => Descendants(panel).Single(control => control.Name == "BlockedUser_" + id.ToString("N"));
        void Capture(string file)
        {
            using var image = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(image, panel.ClientRectangle);
            image.Save(Path.Combine(directory, file), ImageFormat.Png);
        }
        static IEnumerable<Control> Descendants(Control control)
        {
            foreach (Control child in control.Controls)
            { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
        }
    }

    private static HttpResponseMessage BlockedQaJson<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class BlockedUsersQaHandler : HttpMessageHandler
    {
        internal ChatUser[] Users = [];
        internal Guid[] Blocked = [];
        internal HttpStatusCode UsersStatus = HttpStatusCode.OK;
        internal HttpStatusCode UnblockStatus = HttpStatusCode.OK;
        internal TaskCompletionSource<HttpResponseMessage>? HoldUnblock;
        internal TaskCompletionSource<HttpResponseMessage>? HoldBlocks;
        internal bool IgnoreBlockCancellation;
        internal int BlockLoads;
        internal int UserLoads;
        internal List<Guid> Deletes { get; } = [];
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Get && path == "/chat/api/blocks")
            {
                BlockLoads++;
                if (HoldBlocks is { } hold) return IgnoreBlockCancellation ? await hold.Task.ConfigureAwait(false) :
                    await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return BlockedQaJson(Blocked);
            }
            if (request.Method == HttpMethod.Get && path == "/chat/api/users")
            {
                UserLoads++;
                return UsersStatus == HttpStatusCode.OK ? BlockedQaJson(Users) : Failure(UsersStatus);
            }
            if (request.Method == HttpMethod.Delete && path.StartsWith("/chat/api/blocks/", StringComparison.Ordinal) &&
                Guid.TryParse(path.Split('/')[^1], out var user))
            {
                Deletes.Add(user);
                if (HoldUnblock is { } hold) return await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return UnblockStatus == HttpStatusCode.OK ? new HttpResponseMessage(HttpStatusCode.OK) : Failure(UnblockStatus);
            }
            throw new InvalidOperationException("Blocked users QA unexpectedly requested another route.");
        }
        private static HttpResponseMessage Failure(HttpStatusCode code) => new(code)
        { Content = JsonContent.Create(new ApiError("synthetic_block_failure", "Synthetic error is not reflected in the drawer.")) };
    }
}
