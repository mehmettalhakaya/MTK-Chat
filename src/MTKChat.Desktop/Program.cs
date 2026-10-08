namespace MTKChat.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RuntimeDiagnostics.Install();
        ApplicationConfiguration.Initialize();
        // App-owned menus use our renderer; DevExpress editor/grid-generated popups
        // also need a dark base skin so their margins and command panels stay dark.
        DevExpress.LookAndFeel.UserLookAndFeel.Default.SetSkinStyle("Office 2019 Black");
        if (args.Contains("--verify-emojis", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in MainForm.VerifyEmojiPickerIntegration(directory)) Console.WriteLine("Emoji picker QA: " + check);
            foreach (var check in EmojiMessageQA.Verify(directory)) Console.WriteLine("Emoji message QA: " + check);
            foreach (var check in InlineEmojiTextQA.Verify(directory)) Console.WriteLine("Inline emoji QA: " + check);
            foreach (var check in ComposerEmojiQA.Verify(directory)) Console.WriteLine("Composer emoji QA: " + check);
            foreach (var check in MainForm.VerifyEncryptionRecipientStatus()) Console.WriteLine("Recipient key QA: " + check);
            foreach (var check in MainForm.VerifySendEncryptionRecipients()) Console.WriteLine("Send encryption QA: " + check);
            return;
        }
        if (args.Contains("--verify-pins-calls", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in MainForm.VerifyPinnedMessagesIntegration(directory)) Console.WriteLine("Pinned message QA: " + check);
            foreach (var check in CallUIQA.Verify(directory)) Console.WriteLine("Call UI QA: " + check);
            return;
        }
        if (args.Contains("--verify-conversation-menu", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyConversationMenus(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Conversation menu QA: " + check);
            return;
        }
        if (args.Contains("--verify-status-publish", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in StatusPublishQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Status publish QA: " + check);
            return;
        }
        if (args.Contains("--verify-social", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in MainForm.VerifyArchiveMute(directory)) Console.WriteLine("Archive/mute QA: " + check);
            foreach (var check in MainForm.VerifyChatNotifications()) Console.WriteLine("Notification QA: " + check);
            foreach (var check in BlockedUsersQA.Verify(directory)) Console.WriteLine("Blocked users QA: " + check);
            foreach (var check in StatusFeaturesQA.Verify(directory)) Console.WriteLine("Status features QA: " + check);
            foreach (var check in StatusPublishQA.Verify(directory)) Console.WriteLine("Status publish QA: " + check);
            return;
        }
        if (args.Contains("--verify-profile-names", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in ProfileNamesQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Profile names QA: " + check);
            return;
        }
        if (args.Contains("--verify-member-removal", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in GroupMemberRemovalQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Group member removal QA: " + check);
            return;
        }
        if (args.Contains("--verify-invites", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyGroupInvites(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Group invite QA: " + check);
            return;
        }
        if (args.Contains("--verify-conversation-cards", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in MainForm.VerifyConversationCards(directory)) Console.WriteLine("Conversation card QA: " + check);
            foreach (var check in AvatarHoverPaintQA.Verify(directory)) Console.WriteLine("Avatar hover QA: " + check);
            return;
        }
        if (args.Contains("--verify-avatar-hover", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in AvatarHoverPaintQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Avatar hover QA: " + check);
            return;
        }
        if (args.Contains("--verify-previews", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in UserPresentationQA.Verify()) Console.WriteLine("Presence presentation QA: " + check);
            foreach (var check in MainForm.VerifyConversationPreviews(directory)) Console.WriteLine("Conversation preview QA: " + check);
            return;
        }
        if (args.Contains("--verify-presence", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in UserPresentationQA.Verify()) Console.WriteLine("Presence presentation QA: " + check);
            return;
        }
        if (args.Contains("--verify-groups", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in ConversationPickerQA.Verify(directory)) Console.WriteLine("Conversation picker QA: " + check);
            foreach (var check in MainForm.VerifyGroupTitles(directory)) Console.WriteLine("Group title QA: " + check);
            return;
        }
        if (args.Contains("--verify-sidebar", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyRailAccountFeatures()) Console.WriteLine("Rail account QA: " + check);
            foreach (var check in MainForm.VerifySidebarFeatures()) Console.WriteLine("Sidebar drawer QA: " + check);
            return;
        }
        if (args.Contains("--verify-bookmarks", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in LocalChatPreferencesQA.Verify(directory)) Console.WriteLine("Personal preferences QA: " + check);
            foreach (var check in MainForm.VerifyStarredMessages(directory)) Console.WriteLine("Starred message QA: " + check);
            return;
        }
        if (args.Contains("--verify-filters", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyFilterFeatures()) Console.WriteLine("Conversation filter QA: " + check);
            return;
        }
        if (args.Contains("--verify-send", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in SendButtonGeometryQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Send geometry QA: " + check);
            return;
        }
        if (args.Contains("--verify-images", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MessageImagePreviewQA.Verify()) Console.WriteLine("Image preview QA: " + check);
            return;
        }
        if (args.Contains("--verify-avatar", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in AvatarLifecycleQA.Verify()) Console.WriteLine("Avatar lifecycle QA: " + check);
            return;
        }
        if (args.Contains("--verify-runtime", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in RuntimeDiagnosticsQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Runtime diagnostics QA: " + check);
            return;
        }
        if (args.Contains("--verify-popup", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyPopupLifecycle()) Console.WriteLine("Popup lifecycle QA: " + check);
            return;
        }
        if (args.Contains("--verify-icons", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in IconPortabilityQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Portable icon QA: " + check);
            using var form = new MainForm(snapshotMode: true);
            foreach (var check in form.VerifyToolbarIcons()) Console.WriteLine("Toolbar icon QA: " + check);
            return;
        }
        if (args.Contains("--verify-performance", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in ViewportPerformanceQA.Verify()) Console.WriteLine("Viewport performance QA: " + check);
            foreach (var check in MainForm.VerifyPerformance()) Console.WriteLine("History performance QA: " + check);
            return;
        }
        if (args.Contains("--verify-panels", StringComparer.OrdinalIgnoreCase))
        {
            RoundedPanelVisualQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots"));
            return;
        }
        if (args.Contains("--verify-conversation-native", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in ConversationViewportNativeQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Native sidebar QA: " + check);
            return;
        }
        if (args.Contains("--verify-message-info", StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            foreach (var check in MessageInfoPanelQA.Verify(directory))
                Console.WriteLine("Message info panel QA: " + check);
            foreach (var check in MainForm.VerifyMessageInfoIntegration(directory))
                Console.WriteLine("Message info QA: " + check);
            return;
        }
        if (args.Contains("--verify-wallpaper", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in WallpaperScrollQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots")))
                Console.WriteLine("Wallpaper scroll QA: " + check);
            return;
        }
        if (args.Contains("--verify-buttons", StringComparer.OrdinalIgnoreCase))
        {
            ModernButtonVisualQA.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots"));
            return;
        }
        if (args.Contains("--verify-history", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyHistoryLoading()) Console.WriteLine("History UI QA: " + check);
            return;
        }
        if (args.Contains("--verify-network", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var check in MainForm.VerifyNetworkRecovery()) Console.WriteLine("Network UI QA: " + check);
            return;
        }
        if (args.Contains("--verify-ui", StringComparer.OrdinalIgnoreCase))
        {
            UiRepaintRegression.Verify(Path.Combine(AppContext.BaseDirectory, "snapshots"));
            foreach (var result in CaptionRepaintQA.Verify()) Console.WriteLine("Caption repaint QA: " + result);
            return;
        }
        if (args.Contains("--snapshot", StringComparer.OrdinalIgnoreCase))
        {
            SnapshotRenderer.Render();
            return;
        }
#if DEBUG
        if (args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            var preview = new MainForm(snapshotMode: true) { Text = "MTK Chat · Tasarım Önizlemesi" };
            preview.Shown += (_, _) => preview.PopulateSnapshot();
            Application.Run(preview);
            return;
        }
#endif
        Application.Run(new MainForm());
    }
}
