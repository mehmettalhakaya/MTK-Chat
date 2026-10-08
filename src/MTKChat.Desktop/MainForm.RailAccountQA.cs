using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // The real rail/account update and authorization paths run against a strict
    // in-process GET-only handler. No real account, VDS or desktop input is used.
    internal static IReadOnlyList<string> VerifyRailAccountFeatures()
    {
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool valid, string check)
        {
            HistoryQaAssertUiThread();
            if (!valid) throw new InvalidOperationException("Rail account QA: " + check);
            checks.Add(check);
        }
        var handler = new RailAccountHandler();
        using var api = new ChatApiClient("https://rail-account-qa.invalid/", handler, new ChatRequestPolicy
        { RetryDelay = TimeSpan.Zero, MetadataTimeout = TimeSpan.FromSeconds(2) });
        using var form = new MainForm(snapshotMode: true, api);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        form._refreshTimer.Stop(); form._readTimer.Stop();
        var dialogs = 0;
        form._errorObserverForQa = _ => dialogs++;
        var me = form._session!.User;
        var photoField = typeof(AvatarView).GetField("_photo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Image? Photo() => (Image?)photoField.GetValue(form._accountAvatar);
        bool IsPhotoColor(Color expected) => Photo() is Bitmap image &&
            image.GetPixel(image.Width / 2, image.Height / 2).ToArgb() == expected.ToArgb();
        void Apply(ChatUser user)
        {
            form._session = form._session! with { User = user };
            form.RefreshRailAccount(user);
            form.PerformLayout(); Application.DoEvents();
        }

        handler.Photo = RailAccountPhoto(Color.Red);
        var withPhoto = me with { PhotoVersion = "rail-before-open" };
        Apply(withPhoto);
        HistoryQaUntil(() => IsPhotoColor(Color.Red));
        Require(form._sidebarPage == SidebarPage.Chats && form._sidebarProfileEditor is null &&
            form._accountAvatar.Visible && form._accountAvatar.Parent == form._railProfile && handler.PhotoReads == 1,
            "Own photo GET is applied to the visible bottom rail before Profile has ever opened");
        form._accountAvatar.Update(); Application.DoEvents();
        using (var painted = RailAccountCapture(form._accountAvatar))
        {
            // GetDC/BitBlt defines the visible RGB channels, not the copied alpha byte.
            var pixel = painted.GetPixel(painted.Width / 2, painted.Height / 2);
            Require(pixel.R == Color.Red.R && pixel.G == Color.Red.G && pixel.B == Color.Red.B,
                "The loaded profile photo is painted by the real avatar HWND before a profile click");
            Require(new[] { new Point(0, 0), new Point(painted.Width - 1, 0),
                new Point(0, painted.Height - 1), new Point(painted.Width - 1, painted.Height - 1) }
                .All(point => { var corner = painted.GetPixel(point.X, point.Y);
                    return corner.R == Theme.Rail.R && corner.G == Theme.Rail.G && corner.B == Theme.Rail.B; }),
                "The real unopened profile photo's square corners contain rail pixels, not stale button background");
        }
        var sameImage = Photo();
        Apply(withPhoto);
        Require(handler.PhotoReads == 1 && ReferenceEquals(Photo(), sameImage),
            "Unchanged own-profile presence reuses the loaded image without refetching or flashing initials");

        handler.Photo = RailAccountPhoto(Color.Lime);
        var changedPhoto = me with { PhotoVersion = "rail-replacement" };
        form.ApplySidebarProfileUpdate(changedPhoto);
        HistoryQaUntil(() => IsPhotoColor(Color.Lime));
        Require(handler.PhotoReads == 2 && form._session!.User.PhotoVersion == changedPhoto.PhotoVersion,
            "Confirmed profile photo replacement updates the rail through the shared account update path");
        form.ApplySidebarProfileUpdate(me with { PhotoVersion = null }); Application.DoEvents();
        Require(Photo() is null && form._accountAvatar.Initials == UserPresentation.Initials(me.DisplayName) && handler.PhotoReads == 2,
            "Confirmed photo removal releases the rail bitmap and immediately restores initials without a GET");

        // Capture the old application task so the stale result really completes;
        // a changed Tag alone is not evidence that a late callback is harmless.
        var hold = handler.HoldPhoto();
        var pendingPhoto = form._avatars.ApplyAsync(form._accountAvatar, me with { PhotoVersion = "rail-late-old" });
        HistoryQaUntil(() => handler.PhotoReads == 3);
        handler.Photo = RailAccountPhoto(Color.Blue);
        var newest = me with { PhotoVersion = "rail-late-new" };
        Apply(newest); HistoryQaUntil(() => IsPhotoColor(Color.Blue));
        hold.SetResult(RailAccountHandler.Binary(RailAccountPhoto(Color.Red)));
        HistoryQaPump(pendingPhoto);
        Require(IsPhotoColor(Color.Blue) && Equals(form._accountAvatar.Tag, (newest.Id, newest.PhotoVersion)),
            "A late previous photo revision cannot overwrite the newer rail photo or its ownership tag");

        var oldMissingHold = handler.HoldPhoto();
        var oldMissing = form._avatars.ApplyAsync(form._accountAvatar, me with { PhotoVersion = "rail-late-missing" });
        HistoryQaUntil(() => handler.PhotoReads == 5);
        var newestHold = handler.HoldPhoto();
        var afterMissing = me with { PhotoVersion = "rail-after-late-missing" };
        Apply(afterMissing); HistoryQaUntil(() => handler.PhotoReads == 6);
        oldMissingHold.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        HistoryQaPump(oldMissing);
        Require(Photo() is null && Equals(form._accountAvatar.Tag, (afterMissing.Id, afterMissing.PhotoVersion)),
            "An older delayed 404 cannot clear the newer pending profile photo's ownership tag");
        newestHold.SetResult(RailAccountHandler.Binary(RailAccountPhoto(Color.Magenta)));
        HistoryQaUntil(() => IsPhotoColor(Color.Magenta));
        Require(Equals(form._accountAvatar.Tag, (afterMissing.Id, afterMissing.PhotoVersion)),
            "The newest held photo still applies after the previous revision completed with 404");

        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form._accountAvatar, [EventArgs.Empty]); Application.DoEvents();
        Require(form._sidebarPage == SidebarPage.Profile && form._railProfile!.Active,
            "Clicking the photo itself opens the real in-window Profile drawer");
        form._railProfile!.PerformClick(); Application.DoEvents();
        Require(form._sidebarPage == SidebarPage.Chats && !form._railProfile.Active,
            "Clicking the containing profile button collapses the same drawer");
        Require(form._railProfile.AccessibleName == "Profilim" && form._railProfile.AccessibleRole == AccessibleRole.PushButton &&
            form._railProfile.TabStop && !form._accountAvatar.TabStop,
            "The rail photo has one named keyboard-accessible push-button target, not a second avatar tab stop");
        Require(form._railProfile.Focus(), "The real profile push button can acquire keyboard focus");
        typeof(RailProfileButton).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form._railProfile, [new KeyEventArgs(Keys.Space)]);
        typeof(RailProfileButton).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form._railProfile, [new KeyEventArgs(Keys.Space)]); Application.DoEvents();
        Require(form._sidebarPage == SidebarPage.Profile,
            "The standard Space key path activates the focused photo button exactly like a click");
        form._railProfile.PerformClick(); Application.DoEvents();

        var rail = (TableLayoutPanel)form._railProfile.Parent!;
        var sidebarLayout = form._sidebarChats!.Controls.OfType<TableLayoutPanel>().Single();
        var conversationHost = form._conversationFilterEmpty!.Parent!;
        Require(sidebarLayout.RowCount == 5 && sidebarLayout.RowStyles.Count == 5 &&
            sidebarLayout.GetRow(conversationHost) == 4 && sidebarLayout.RowStyles[4].SizeType == SizeType.Percent,
            "The chat-list sidebar has no old footer or reserved 146px footer row");
        Require(!HistoryQaControls(form._sidebarChats!).Any(control => control == form._premiumAdminButton ||
            control == form._accountAvatar || control.AccessibleName == "Profilini düzenle" || control.Text == "⚙  Ayarlar    ›"),
            "Profile, settings and management controls are absent from the chat-list content tree");
        Require(rail.GetRow(form._premiumAdminButton) < rail.GetRow(form._railSettings!) &&
            rail.GetRow(form._railSettings!) < rail.GetRow(form._railProfile),
            "Site management is above Settings, with own Profile as the lowest rail target");
        foreach (var width in new[] { 1120, 1380, 1536, 1920 })
        {
            form.Size = new Size(width, 860); form.PerformLayout(); Application.DoEvents();
            var avatar = form._accountAvatar;
            var button = form._railProfile!;
            Require(button.Parent!.ClientRectangle.Contains(button.Bounds) && button.ClientRectangle.Contains(avatar.Bounds) &&
                avatar.Width == avatar.Height && avatar.Width > 16 &&
                Math.Abs(avatar.Left + avatar.Width / 2f - button.ClientSize.Width / 2f) <= .5f &&
                Math.Abs(avatar.Top + avatar.Height / 2f - button.ClientSize.Height / 2f) <= .5f,
                $"The real square rail photo and hit target fit and are centered at {width}px / {form.DeviceDpi} DPI");
            Require(conversationHost.Bottom >= sidebarLayout.ClientSize.Height - sidebarLayout.Padding.Bottom - 2,
                $"The conversation viewport uses the former footer area at {width}px");
        }

        foreach (var (label, user, groupRole) in new[]
        {
            ("normal user", me with { Role = "user", IsAgent = false, PhotoVersion = null }, "user"),
            ("group administrator", me with { Role = "user", IsAgent = false, PhotoVersion = null }, "admin"),
            ("group moderator", me with { Role = "user", IsAgent = false, PhotoVersion = null }, "mod"),
            ("bot with a forged admin label", me with { Role = "admin", IsAgent = true, PhotoVersion = null }, "admin")
        })
        {
            form._selectedConversation = form._selectedConversation! with { GroupRoles = new Dictionary<Guid, string> { [user.Id] = groupRole } };
            Apply(user);
            var before = handler.AccessReads + handler.ProfileReads;
            var denied = form.ValidateRailAdminAsync(); HistoryQaPump(denied);
            Require(denied.Result is null && !form._premiumAdminButton.Visible && form._railAdminRow!.Height == 0 &&
                rail.GetRowHeights()[rail.GetRow(form._premiumAdminButton)] == 0 && handler.AccessReads + handler.ProfileReads == before,
                $"The {label} has no management target/slot and is locally denied without an admin API read");
        }

        var humanAdmin = me with { Role = "admin", IsAgent = false, PhotoVersion = null };
        Apply(humanAdmin); handler.Profile = humanAdmin;
        var authorized = form.ValidateRailAdminAsync(); HistoryQaPump(authorized);
        Require(authorized.Result == humanAdmin && form._premiumAdminButton.Visible && form._railAdminRow!.Height > 0 &&
            handler.AccessReads == 1 && handler.ProfileReads == 1,
            "A human site-admin validates both server access and own profile before management can open");

        handler.Profile = humanAdmin with { Role = "user" };
        var revoked = form.ValidateRailAdminAsync(); HistoryQaPump(revoked); Application.DoEvents();
        Require(revoked.Result is null && form._session!.User.Role == "user" && !form._premiumAdminButton.Visible && form._railAdminRow!.Height == 0,
            "Server-side site-role revocation updates the session and removes the management slot");

        Apply(humanAdmin); handler.DenyAccess = true;
        var profileBeforeDenial = handler.ProfileReads;
        HistoryQaPump(form.OpenAdministrationAsync()); Application.DoEvents();
        Require(!form._premiumAdminButton.Visible && form._railAdminRow!.Height == 0 && handler.ProfileReads == profileBeforeDenial && dialogs == 1,
            "A 403 access check cannot open management; its existing error flow hides the rail target without reading the profile");
        handler.DenyAccess = false;

        Apply(humanAdmin); handler.Profile = humanAdmin with { Id = Guid.NewGuid() };
        var foreign = form.ValidateRailAdminAsync(); HistoryQaPump(foreign);
        Require(foreign.Result is null && form._session!.User.Id == humanAdmin.Id && !form._premiumAdminButton.Visible,
            "A foreign account returned by the profile endpoint cannot replace the session or authorize management");

        Apply(humanAdmin); handler.Profile = humanAdmin;
        var profileHold = handler.HoldProfile();
        var profileBeforeHold = handler.ProfileReads;
        var pendingSameOwner = form.ValidateRailAdminAsync();
        HistoryQaUntil(() => handler.ProfileReads > profileBeforeHold);
        // Presence can refresh the same user's site role while an older own-profile
        // response is in flight. The UUID check alone would not prevent elevation.
        Apply(humanAdmin with { Role = "user" });
        profileHold.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(humanAdmin) });
        HistoryQaPump(pendingSameOwner); Application.DoEvents();
        Require(pendingSameOwner.Result is null && form._session!.User.Id == humanAdmin.Id && form._session!.User.Role == "user" &&
            !form._premiumAdminButton.Visible && form._railAdminRow!.Height == 0,
            "A held older admin profile cannot undo a same-account presence role downgrade or reopen management");

        Apply(humanAdmin); handler.Profile = humanAdmin with { Role = "user" };
        var accessHold = handler.HoldAccess();
        var accessBeforeHold = handler.AccessReads;
        var pendingAdmin = form.ValidateRailAdminAsync();
        HistoryQaUntil(() => handler.AccessReads > accessBeforeHold);
        var nextAccount = humanAdmin with { Id = Guid.NewGuid(), DisplayName = "Diğer QA yönetici" };
        Apply(nextAccount);
        accessHold.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        HistoryQaPump(pendingAdmin);
        Require(pendingAdmin.Result is null && form._session!.User.Id == nextAccount.Id && form._session!.User.Role == "admin" &&
            form._premiumAdminButton.Visible && Equals(form._accountAvatar.Tag, (nextAccount.Id, nextAccount.PhotoVersion)),
            "A previous owner's late authorization result cannot mutate or hide the new owner's valid rail state");

        Require(handler.Writes == 0 && dialogs == 1,
            "Rail regression uses only synthetic GET requests and intercepts the one expected denial error without a modal window");
        form.Hide(); Application.DoEvents();
        checks.AddRange(RailProfilePaintQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")));
        return checks;
    }

    private static byte[] RailAccountPhoto(Color color)
    {
        using var image = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        using var encoded = new MemoryStream(); image.Save(encoded, ImageFormat.Png);
        return encoded.ToArray();
    }

    private sealed class RailAccountHandler : HttpMessageHandler
    {
        internal byte[] Photo = [];
        internal ChatUser? Profile;
        internal int PhotoReads, AccessReads, ProfileReads, Writes;
        internal bool DenyAccess;
        private TaskCompletionSource<HttpResponseMessage>? _photoHold;
        private TaskCompletionSource<HttpResponseMessage>? _accessHold;
        private TaskCompletionSource<HttpResponseMessage>? _profileHold;
        internal TaskCompletionSource<HttpResponseMessage> HoldPhoto() =>
            _photoHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HttpResponseMessage> HoldAccess() =>
            _accessHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HttpResponseMessage> HoldProfile() =>
            _profileHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static HttpResponseMessage Binary(byte[] bytes) => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(bytes) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            { Writes++; throw new InvalidOperationException("Rail QA forbids remote writes."); }
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/users/", StringComparison.Ordinal) && path.EndsWith("/photo", StringComparison.Ordinal))
            {
                PhotoReads++;
                if (_photoHold is { } hold) { _photoHold = null; return hold.Task; }
                return Task.FromResult(Binary(Photo));
            }
            if (path == "/api/admin/access")
            {
                AccessReads++;
                if (_accessHold is { } hold) { _accessHold = null; return hold.Task; }
                return Task.FromResult(new HttpResponseMessage(DenyAccess ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent));
            }
            if (path == "/api/profile")
            {
                ProfileReads++;
                if (_profileHold is { } hold) { _profileHold = null; return hold.Task; }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Profile) });
            }
            throw new InvalidOperationException("Unexpected synthetic rail GET route: " + path);
        }
    }

    private static Bitmap RailAccountCapture(Control control)
    {
        var result = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        var source = RailAccountGetDC(control.Handle);
        try
        {
            using var graphics = Graphics.FromImage(result);
            var target = graphics.GetHdc();
            try
            {
                if (source == IntPtr.Zero || !RailAccountBitBlt(target, 0, 0, result.Width, result.Height, source, 0, 0, 0x00CC0020))
                    throw new InvalidOperationException("Cannot capture the synthetic rail avatar's own native surface.");
            }
            finally { graphics.ReleaseHdc(target); }
        }
        catch { result.Dispose(); throw; }
        finally { if (source != IntPtr.Zero) RailAccountReleaseDC(control.Handle, source); }
        return result;
    }

    [DllImport("user32.dll", EntryPoint = "GetDC")] private static extern IntPtr RailAccountGetDC(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] private static extern int RailAccountReleaseDC(IntPtr window, IntPtr context);
    [DllImport("gdi32.dll", EntryPoint = "BitBlt")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RailAccountBitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
}
