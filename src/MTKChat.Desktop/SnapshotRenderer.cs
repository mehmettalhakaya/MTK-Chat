namespace MTKChat.Desktop;

internal static class SnapshotRenderer
{
    public static void Render()
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(outputDirectory);
        foreach (var check in MainForm.VerifyMessageActions(outputDirectory)) Console.WriteLine("Message actions QA: " + check);
        foreach (var check in LoginRegistrationQA.Verify(outputDirectory)) Console.WriteLine("Login registration QA: " + check);
        foreach (var check in MainForm.VerifyEmojiPickerIntegration(outputDirectory)) Console.WriteLine("Emoji picker QA: " + check);
        foreach (var check in EmojiMessageQA.Verify(outputDirectory)) Console.WriteLine("Emoji message QA: " + check);
        foreach (var check in InlineEmojiTextQA.Verify(outputDirectory)) Console.WriteLine("Inline emoji QA: " + check);
        foreach (var check in ComposerEmojiQA.Verify(outputDirectory)) Console.WriteLine("Composer emoji QA: " + check);
        foreach (var check in MainForm.VerifyEncryptionRecipientStatus()) Console.WriteLine("Recipient key QA: " + check);
        foreach (var check in MainForm.VerifySendEncryptionRecipients()) Console.WriteLine("Send encryption QA: " + check);
        foreach (var check in MainForm.VerifyPinnedMessagesIntegration(outputDirectory)) Console.WriteLine("Pinned message QA: " + check);
        foreach (var check in CallUIQA.Verify(outputDirectory)) Console.WriteLine("Call UI QA: " + check);
        foreach (var check in MainForm.VerifyConversationMenus(outputDirectory)) Console.WriteLine("Conversation menu QA: " + check);
        foreach (var check in MainForm.VerifyArchiveMute(outputDirectory)) Console.WriteLine("Archive/mute QA: " + check);
        foreach (var check in MainForm.VerifyChatNotifications()) Console.WriteLine("Notification QA: " + check);
        foreach (var check in BlockedUsersQA.Verify(outputDirectory)) Console.WriteLine("Blocked users QA: " + check);
        foreach (var check in StatusFeaturesQA.Verify(outputDirectory)) Console.WriteLine("Status features QA: " + check);
        foreach (var check in StatusPublishQA.Verify(outputDirectory)) Console.WriteLine("Status publish QA: " + check);
        foreach (var check in ProfileNamesQA.Verify(outputDirectory)) Console.WriteLine("Profile names QA: " + check);
        foreach (var check in AuxiliaryActionButtonsQA.Verify(outputDirectory)) Console.WriteLine("Auxiliary action QA: " + check);
        foreach (var check in GroupMemberRemovalQA.Verify(outputDirectory)) Console.WriteLine("Group member removal QA: " + check);
        foreach (var check in MainForm.VerifyGroupInvites(outputDirectory)) Console.WriteLine("Group invite QA: " + check);
        foreach (var check in MainForm.VerifyConversationCards(outputDirectory)) Console.WriteLine("Conversation card QA: " + check);
        foreach (var check in MainForm.VerifyConversationOrdering(outputDirectory)) Console.WriteLine("Conversation ordering QA: " + check);
        foreach (var check in AvatarHoverPaintQA.Verify(outputDirectory)) Console.WriteLine("Avatar hover QA: " + check);
        foreach (var check in UserPresentationQA.Verify()) Console.WriteLine("Presence presentation QA: " + check);
        foreach (var check in MainForm.VerifyConversationPreviews(outputDirectory)) Console.WriteLine("Conversation preview QA: " + check);
        foreach (var check in MainForm.VerifyDeletedConversationPreviews(outputDirectory)) Console.WriteLine("Deleted conversation preview QA: " + check);
        foreach (var check in ConversationPickerQA.Verify(outputDirectory)) Console.WriteLine("Conversation picker QA: " + check);
        foreach (var check in MainForm.VerifyGroupTitles(outputDirectory)) Console.WriteLine("Group title QA: " + check);
        foreach (var check in MainForm.VerifyRailAccountFeatures()) Console.WriteLine("Rail account QA: " + check);
        foreach (var check in MainForm.VerifySidebarFeatures()) Console.WriteLine("Sidebar drawer QA: " + check);
        foreach (var check in SendButtonGeometryQA.Verify(outputDirectory)) Console.WriteLine("Send geometry QA: " + check);
        foreach (var check in LocalChatPreferencesQA.Verify(outputDirectory)) Console.WriteLine("Personal preferences QA: " + check);
        foreach (var check in MainForm.VerifyStarredMessages(outputDirectory)) Console.WriteLine("Starred message QA: " + check);
        foreach (var check in MessageImagePreviewQA.Verify()) Console.WriteLine("Image preview QA: " + check);
        foreach (var check in RuntimeDiagnosticsQA.Verify(outputDirectory)) Console.WriteLine("Runtime diagnostics QA: " + check);
        foreach (var check in MainForm.VerifyPopupLifecycle()) Console.WriteLine("Popup lifecycle QA: " + check);
        foreach (var check in AvatarLifecycleQA.Verify()) Console.WriteLine("Avatar lifecycle QA: " + check);
        foreach (var check in IconPortabilityQA.Verify(outputDirectory)) Console.WriteLine("Portable icon QA: " + check);
        using (var toolbar = new MainForm(snapshotMode: true))
            foreach (var check in toolbar.VerifyToolbarIcons()) Console.WriteLine("Toolbar icon QA: " + check);
        foreach (var check in ViewportPerformanceQA.Verify()) Console.WriteLine("Viewport performance QA: " + check);
        foreach (var check in MainForm.VerifyPerformance()) Console.WriteLine("History performance QA: " + check);
        RoundedPanelVisualQA.Verify(outputDirectory);
        foreach (var check in ConversationViewportNativeQA.Verify(outputDirectory)) Console.WriteLine("Native sidebar QA: " + check);
        foreach (var check in MessageInfoPanelQA.Verify(outputDirectory)) Console.WriteLine("Message info panel QA: " + check);
        foreach (var check in MainForm.VerifyMessageInfoIntegration(outputDirectory)) Console.WriteLine("Message info QA: " + check);
        ModernButtonVisualQA.Verify(outputDirectory);
        foreach (var check in WallpaperScrollQA.Verify(outputDirectory)) Console.WriteLine("Wallpaper scroll QA: " + check);
        foreach (var check in MainForm.VerifyHistoryLoading()) Console.WriteLine("History UI QA: " + check);
        foreach (var check in MainForm.VerifyNetworkRecovery()) Console.WriteLine("Network UI QA: " + check);
        UiRepaintRegression.Verify(outputDirectory);
        foreach (var result in CaptionRepaintQA.Verify()) Console.WriteLine("Caption repaint QA: " + result);
        foreach (var result in WallpaperCacheQA.Verify()) Console.WriteLine("Wallpaper QA: " + result);
        VerifyAnimatedAvatar(outputDirectory);
        RenderMenuSnapshots(outputDirectory);
        VerifyRoleColors();
        VerifyEditorPopupTheme();
        VerifyMessageDeleteWindow();
        VerifyTypography();
        using (var filters = new MainForm(snapshotMode: true))
        {
            filters.Show(); filters.PopulateSnapshot();
            filters.VerifyConversationFilters(); filters.Hide();
        }
        using (var composer = new MainForm(snapshotMode: true))
        {
            composer.Show(); composer.PopulateSnapshot(); Application.DoEvents();
            foreach (var result in composer.VerifyComposerEditing()) Console.WriteLine("Composer QA: " + result);
            composer.VerifySearchShortcut();
            foreach (var result in composer.VerifyPresenceContinuity()) Console.WriteLine("Presence QA: " + result);
            composer.Hide();
        }
        RenderForm(new LoginForm(), Path.Combine(outputDirectory, "login.png"));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-profile-rail-admin.png"),
            form => ((MainForm)form).PopulateRailPhotoSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-profile-rail-user.png"),
            form => ((MainForm)form).PopulateRailPhotoSnapshot(admin: false));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-settings-drawer.png"),
            form => ((MainForm)form).PopulateSidebarSnapshot(settings: true));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-profile-drawer.png"),
            form => ((MainForm)form).PopulateSidebarSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-privacy-drawer.png"),
            form => ((MainForm)form).PopulateSidebarSnapshot(privacy: true));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-unread-empty.png"),
            form => ((MainForm)form).PopulateFilterSnapshot("unread", empty: true));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-favorites-empty.png"),
            form => ((MainForm)form).PopulateFilterSnapshot("favorites", empty: true));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-favorites.png"),
            form => ((MainForm)form).PopulateFilterSnapshot("favorites", empty: false));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-starred.png"),
            form => ((MainForm)form).PopulateStarredSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-history-loading.png"),
            form => ((MainForm)form).PopulateHistoryStateSnapshot(failed: false));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-history-retry.png"),
            form => ((MainForm)form).PopulateHistoryStateSnapshot(failed: true));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-emoji-draft.png"),
            form => ((MainForm)form).PopulateEmojiDraftSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-reference-direct.png"),
            form => ((MainForm)form).PopulateReferenceSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-reference-group.png"),
            form => ((MainForm)form).PopulateReferenceSnapshot(direct: false));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-participants.png"),
            form => { ((MainForm)form).PopulateReferenceSnapshot(direct: false); ((MainForm)form).ShowParticipantsSnapshot(); });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-shell.png"),
            form => ((MainForm)form).PopulateSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-user.png"),
            form => ((MainForm)form).PopulateSnapshot(admin: false));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-no-conversation.png"),
            form => ((MainForm)form).PopulateNoConversationSnapshot());
        foreach (var width in new[] { 1120, 1380, 1920 })
            RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, $"chat-scroll-{width}.png"),
                form =>
                {
                    form.Size = new Size(width, width == 1120 ? 720 : 860);
                    var chat = (MainForm)form; chat.PopulateMessageScrollSnapshot();
                    Application.DoEvents(); form.PerformLayout();
                    chat.VerifyMessageScrolling();
                });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-receipts.png"),
            form => ((MainForm)form).PopulateReceiptSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-message-info-direct.png"),
            form => ((MainForm)form).PopulateMessageInfoSnapshot(direct: true));
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-message-info-group.png"),
            form => ((MainForm)form).PopulateMessageInfoSnapshot());
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-message-info-compact.png"),
            form => { form.Size = new Size(1120, 720); ((MainForm)form).PopulateMessageInfoSnapshot(); });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-receipts-compact.png"),
            form => { form.Size = new Size(1120, 720); ((MainForm)form).PopulateReceiptSnapshot(); });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-image-draft.png"),
            form =>
            {
                ((MainForm)form).PopulateSnapshot();
                ((MainForm)form).PopulateImageDraftSnapshot();
            });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-file-draft.png"),
            form => { ((MainForm)form).PopulateSnapshot(); ((MainForm)form).PopulateFileDraftSnapshot(); });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-voice-draft.png"),
            form =>
            {
                ((MainForm)form).PopulateSnapshot();
                ((MainForm)form).PopulateVoiceDraftSnapshot();
            });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-voice-message.png"),
            form =>
            {
                ((MainForm)form).PopulateSnapshot();
                ((MainForm)form).PopulateVoiceMessageSnapshot();
            });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-wide.png"),
            form =>
            {
                form.Size = new Size(1920, 1040);
                ((MainForm)form).PopulateSnapshot();
            });
        RenderForm(new MainForm(snapshotMode: true), Path.Combine(outputDirectory, "chat-compact.png"),
            form =>
            {
                form.Size = new Size(1120, 720);
                ((MainForm)form).PopulateSnapshot();
                Application.DoEvents();
                ((MainForm)form).VerifyConversationScrolling();
            });
        using var api = new ChatApiClient("http://127.0.0.1:5088/");
        var admin = new MTKChat.Contracts.ChatUser(Guid.NewGuid(), "MTK Demo", "demo@mtkaya.me", false, null, "admin");
        RenderForm(new ProfileForm(api, admin, new AvatarCache(api), snapshotMode: true), Path.Combine(outputDirectory, "profile.png"));
        RenderForm(new GroupManagementForm(api, admin with { Role = "user" }, null, snapshot: true), Path.Combine(outputDirectory, "group-management.png"));
        RenderForm(new GroupManagementForm(api, admin with { Role = "user" }, null, snapshot: true), Path.Combine(outputDirectory, "group-management-compact.png"),
            form => form.Size = new Size(720, 590));
        var conversation = new MTKChat.Contracts.ConversationSummary(Guid.NewGuid(), "MTK Lounge", new[] { admin }, "", DateTimeOffset.Now, 0);
        using var identity = MTKChat.Cryptography.DeviceIdentity.Create();
        var guest = admin with { Id = Guid.NewGuid(), DisplayName = "Ayşe Demir", Role = "user" };
        RenderForm(new ConversationPickerForm([guest, admin with { Id = Guid.NewGuid(), DisplayName = "Mehmet Kaya", Role = "user" }], group: false), Path.Combine(outputDirectory, "new-chat.png"));
        RenderForm(new ConversationPickerForm([guest, admin with { Id = Guid.NewGuid(), DisplayName = "Mehmet Kaya", Role = "user" }], group: true), Path.Combine(outputDirectory, "new-group.png"));
        var call = new MTKChat.Contracts.CallView(Guid.NewGuid(), conversation.Id, "MTK Lounge", guest.Id,
            DateTimeOffset.UtcNow, [admin, guest], []);
        RenderForm(new CallForm(api, identity, admin.Id, call, incoming: true, snapshot: true), Path.Combine(outputDirectory, "incoming-call.png"));
        RenderForm(new GroupPhotoForm(api, conversation, new AvatarCache(api), snapshotMode: true), Path.Combine(outputDirectory, "group-photo.png"));
        var memberRoom = conversation with { Participants = [admin, guest,
            guest with { Id = Guid.NewGuid(), DisplayName = "Mehmet Kaya" }],
            GroupRoles = new Dictionary<Guid, string> { [guest.Id] = "mod" } };
        RenderForm(new ConversationMembersForm(api, memberRoom, snapshot: true), Path.Combine(outputDirectory, "group-members.png"));
        RenderForm(new UserManagementForm(api, admin, conversation, snapshotMode: true), Path.Combine(outputDirectory, "admin.png"),
            form => ((UserManagementForm)form).PopulateSnapshot());
    }

    private static void RenderForm(Form form, string path, Action<Form>? prepare = null)
    {
        using (form)
        {
            form.Show();
            Application.DoEvents();
            prepare?.Invoke(form);
            Application.DoEvents();
            form.PerformLayout();
            Thread.Sleep(250);
            Application.DoEvents();
            if (form is ModernForm framed)
                Console.WriteLine($"Caption QA: {form.GetType().Name}, {framed.VerifyCaptionLayout().Count} checks passed.");
            if (form is MainForm main) main.VerifyReferenceLayout();
            VerifyRows(form);
            // DirectX composer surfaces are absent from DrawToBitmap/GDI backing
            // copies. Ordinary MainForm snapshots include the real HWND content.
            // Keep the established native DC path for overlapping receipt/empty
            // overlays: WM_PRINT can flatten those siblings in the wrong order.
            var overlayCapture = form is MainForm mainForm && (mainForm.HasOpenMessageInfo || mainForm.HasVisibleFilterEmpty);
            var nativeCapture = form is MainForm;
            using var bitmap = overlayCapture ? MainForm.CaptureInfoClient((MainForm)form) :
                nativeCapture ? NativeQaCapture.Capture(form) : new Bitmap(form.Width, form.Height);
            if (!nativeCapture)
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            form.Hide();
        }
    }

    private static void VerifyRows(Control control)
    {
        if (control is MessageRow row) row.VerifyLayout();
        foreach (Control child in control.Controls) VerifyRows(child);
    }

    private static void VerifyTypography()
    {
        using var font = Theme.Font(10);
        if (font.Name != "Segoe UI") throw new InvalidOperationException("Windows UI font silently fell back.");
        using var host = new ModernForm { Size = new Size(1000, 780), BackColor = Theme.Canvas };
        var user = new MTKChat.Contracts.ChatUser(Guid.NewGuid(), "Özgür Şahin", "", false, null, "user");
        host.Show(); Application.DoEvents();
        Console.WriteLine($"Typography QA: DeviceDpi={host.DeviceDpi}; 24 row combinations.");
        var texts = new[] {
            "İstanbul, özgün tasarım ve tutarlı tipografi.\nTürkçe karakterler: İ ı Ş ş Ğ ğ Ü ü Ö ö Ç ç.\nSon satır görünmeli.",
            string.Join(" ", Enumerable.Repeat("Kullanıcı mesajı dar ve geniş pencerelerde eksilmeden yeniden akmalıdır.", 20)),
            "https://mtkaya.me/" + new string('a', 180),
            "Çok kısa." };
        foreach (var width in new[] { 360, 480, 900 })
        foreach (var mine in new[] { false, true })
        foreach (var text in texts)
        {
            var message = new MTKChat.Contracts.StoredMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                user.Id, "text", DateTimeOffset.UtcNow, null, false, [], null);
            using var row = new MessageRow(user, mine, message, text, null);
            host.Controls.Add(row); row.Width = width; row.PerformLayout(); Application.DoEvents();
            row.VerifyLayout();
            if (!mine)
            {
                var fullViewport = row.Bounds;
                if (!row.IsContentVisible(fullViewport) ||
                    row.IsContentVisible(new Rectangle(row.Left, row.Bottom + 20, width, row.Height)))
                    throw new InvalidOperationException("Gelen kısa/uzun mesajın okunma görünürlüğü bozuldu.");
                row.CanMarkRead = false;
                if (row.IsContentVisible(fullViewport))
                    throw new InvalidOperationException("Okunmaya uygun olmayan mesaj görünürlük kontrolünü geçti.");
            }
            host.Controls.Remove(row);
        }
        host.Hide();
    }

    private static void VerifyAnimatedAvatar(string directory)
    {
        // Two 1px red/blue frames exercise the real Windows ImageAnimator + AvatarView
        // path, rather than merely checking that the server kept multiple GIF frames.
        var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0, 255, 0, 0, 0, 0, 255]);
        bytes.AddRange([0x21, 0xff, 11]);
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        bytes.AddRange([3, 1, 0, 0, 0]);
        foreach (var color in new byte[] { 0x44, 0x4c })
        {
            bytes.AddRange([0x21, 0xf9, 4, 0, 10, 0, 0, 0]);
            bytes.AddRange([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, color, 1, 0]);
        }
        bytes.Add(0x3b);
        using var form = new Form { Size = new Size(120, 120), BackColor = Theme.Canvas };
        using var avatar = new AvatarView { Size = new Size(64, 64), Location = new Point(8, 8) };
        form.Controls.Add(avatar); form.Show(); avatar.SetEncodedPhoto(bytes.ToArray());
        var seen = new HashSet<int>();
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(75); Application.DoEvents();
            using var image = new Bitmap(64, 64);
            avatar.DrawToBitmap(image, new Rectangle(0, 0, 64, 64));
            if (seen.Add(image.GetPixel(32, 32).ToArgb()))
                image.Save(Path.Combine(directory, $"gif-avatar-frame-{seen.Count}.png"));
        }
        if (seen.Count < 2) throw new InvalidOperationException("Animated avatar did not advance frames.");
        form.Hide();
    }

    private static void VerifyRoleColors()
    {
        var user = new MTKChat.Contracts.ChatUser(Guid.NewGuid(), "Örnek kullanıcı", "", false, null);
        if (UserPresentation.RoleColor(user) != Theme.Text ||
            UserPresentation.RoleColor(user, "admin") != Theme.Warning ||
            UserPresentation.RoleColor(user, "mod") == Theme.Text ||
            UserPresentation.RoleColor(user with { Role = "admin" }, "user") != Theme.Warning ||
            UserPresentation.RoleColor(user with { IsAgent = true }) != Theme.Bot)
            throw new InvalidOperationException("Role palette regression.");
    }

    private static void RenderMenuSnapshots(string directory)
    {
        using var host = new Form { Size = new Size(620, 430), StartPosition = FormStartPosition.CenterScreen,
            BackColor = Theme.Canvas };
        host.Show(); Application.DoEvents();
        void Capture(ModernContextMenu menu, string name, ToolStripMenuItem? submenu = null)
        {
            using (menu)
            {
                menu.Show(host, new Point(20, 20));
                if (submenu is null) menu.Items[0].Select();
                else { submenu.Select(); submenu.ShowDropDown(); }
                Application.DoEvents();
                Thread.Sleep(75); Application.DoEvents();
                // Compose actual popup bitmap(s), including child-dropdown rendering.
                using var root = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(root, new Rectangle(Point.Empty, menu.Size));
                var child = submenu?.DropDown;
                using var result = new Bitmap(menu.Width + (child?.Width ?? 0) + 34,
                    Math.Max(menu.Height, (submenu?.Bounds.Top ?? 0) + (child?.Height ?? 0)) + 32);
                using var graphics = Graphics.FromImage(result);
                graphics.Clear(Theme.Canvas); graphics.DrawImageUnscaled(root, 16, 16);
                if (child is not null)
                {
                    if (child.Renderer is not ModernMenuRenderer || child.BackColor != menu.BackColor)
                        throw new InvalidOperationException("Submenu escaped the app palette.");
                    using var nested = new Bitmap(child.Width, child.Height);
                    child.DrawToBitmap(nested, new Rectangle(Point.Empty, child.Size));
                    graphics.DrawImageUnscaled(nested, menu.Width + 18, 16 + submenu!.Bounds.Top);
                    child.Hide();
                }
                result.Save(Path.Combine(directory, name));
                menu.Hide();
            }
        }
        ModernContextMenu Labels(params string[] labels)
        {
            var menu = Theme.ContextMenu();
            foreach (var label in labels) menu.Items.Add(label);
            return menu;
        }
        var people = Labels("Mesaj gönder", "Sesli ara");
        var roles = new ToolStripMenuItem("Grup rolü");
        roles.DropDownItems.Add(new ToolStripMenuItem("Grup yöneticisi") { ForeColor = Theme.Warning });
        roles.DropDownItems.Add(new ToolStripMenuItem("Mod") { ForeColor = Color.FromArgb(171, 132, 255) });
        roles.DropDownItems.Add(new ToolStripMenuItem("User") { Checked = true, ForeColor = Theme.Text });
        people.Items.Add(roles); people.Items.Add(new ToolStripSeparator());
        people.Items.Add(new ToolStripMenuItem("Engelle") { ForeColor = Theme.Danger });
        Capture(people, "menu-person-role.png", roles);
        var disabled = Labels("Mesaj gönder", "Sesli ara", "Engelle");
        disabled.Items[2].Enabled = false;
        Capture(disabled, "menu-disabled.png");
        var expiry = Labels("Kalıcı", "30 saniye", "5 dakika", "1 saat", "1 gün");
        ((ToolStripMenuItem)expiry.Items[0]).Checked = true;
        Capture(expiry, "menu-expiry.png");
        var message = Labels("Metni kopyala", "Mesaj bilgisi", "Benden sil", "Herkesten sil");
        message.Items[2].ForeColor = message.Items[3].ForeColor = Theme.Danger;
        Capture(message, "menu-message.png");
        Capture(Labels("Yeni sohbet", "Yeni grup"), "menu-new-chat.png");
        Capture(Labels("Görsel", "Dosya"), "menu-attachment.png");
        // These use actual production card menus, not duplicated sample labels.
        using (var chat = new MainForm(snapshotMode: true))
        {
            chat.Show(); chat.PopulateSnapshot();
            var menu = (ModernContextMenu)chat.ConversationActionsSnapshotMenu;
            Capture(menu, "menu-group-actions-admin.png",
                menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Grup seçenekleri"));
            chat.Hide();
        }
        using (var chat = new MainForm(snapshotMode: true))
        {
            chat.Show(); chat.PopulateSnapshot(admin: false);
            var menu = (ModernContextMenu)chat.ConversationActionsSnapshotMenu;
            Capture(menu, "menu-group-actions-user.png",
                menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Grup seçenekleri"));
            chat.Hide();
        }
        using (var chat = new MainForm(snapshotMode: true))
        {
            chat.Show(); chat.PopulateDirectActionsSnapshot();
            Capture((ModernContextMenu)chat.ConversationActionsSnapshotMenu, "menu-direct-actions.png");
            chat.Hide();
        }
        host.Hide();
    }

    private static void VerifyMessageDeleteWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var actor = Guid.NewGuid();
        var message = new MTKChat.Contracts.StoredMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actor,
            "text", now, null, false, [], null, DeleteForEveryoneUntil: now.AddMinutes(15));
        if (!MainForm.CanDeleteForEveryone(message, actor, now.AddMinutes(14)) ||
            MainForm.CanDeleteForEveryone(message, actor, now.AddMinutes(15)) ||
            MainForm.CanDeleteForEveryone(message, Guid.NewGuid(), now) ||
            MainForm.CanDeleteForEveryone(message with { DeleteForEveryoneUntil = null }, actor, now) ||
            MainForm.CanDeleteForEveryone(message with { DeletedForEveryone = true }, actor, now))
            throw new InvalidOperationException("Message deletion menu did not honor its trusted deadline.");
    }

    private static void VerifyEditorPopupTheme()
    {
        using var host = new ModernForm { Size = new Size(520, 280), BackColor = Theme.Canvas };
        using var editor = new DevExpress.XtraEditors.MemoEdit { Text = "Örnek metin", Dock = DockStyle.Fill };
        host.Controls.Add(editor); host.Show(); editor.Focus(); editor.SelectAll();
        var styled = false;
        editor.Properties.BeforeShowMenu += (_, e) =>
        {
            // ModernForm registered the theme handler before this test observer.
            styled = e.Menu.Appearance.BackColor == Theme.Surface && e.Menu.Items.Count > 0;
            foreach (DevExpress.Utils.Menu.DXMenuItem item in e.Menu.Items)
                styled &= item.Appearance.ForeColor == Theme.Text &&
                    item.AppearanceHovered.BackColor == Theme.SurfaceHover &&
                    item.AppearanceDisabled.ForeColor == Theme.Muted;
        };
        var support = (DevExpress.Utils.Menu.IDXMenuSupport)editor;
        // Some DevExpress versions use a modal native ContextMenu. Dismiss it from
        // its UI message loop, so unattended validation never waits for a real click.
        using var closeMenu = new System.Windows.Forms.Timer { Interval = 250 };
        closeMenu.Tick += (_, _) => { closeMenu.Stop(); support.Menu.HidePopup(); };
        try
        {
            closeMenu.Start();
            support.ShowMenu(new Point(-1, -1)); // Public keyboard/caret popup path.
            Application.DoEvents();
            if (!styled) throw new InvalidOperationException("Editor popup did not receive the app palette.");
        }
        finally { closeMenu.Stop(); support.Menu.HidePopup(); host.Hide(); }
    }
}
