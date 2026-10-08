using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static bool CanDeleteForEveryone(StoredMessage message, Guid? actor, DateTimeOffset now) =>
        actor == message.SenderId && !message.DeletedForEveryone &&
        message.DeleteForEveryoneUntil is { } deadline && now < deadline;

    private void AddConversationActions(RoundedPanel card)
    {
        var menu = Theme.ContextMenu();
        ConversationSummary Current() => (ConversationSummary)card.Tag!;
        var group = Current().Kind != "direct";
        var favorite = menu.Items.AddAction("Favorilere ekle", ModernMenuIcon.Favorite, (_, _) =>
        {
            try { SetFavoriteConversation(Current().Id, !IsFavoriteConversation(Current().Id)); }
            catch (Exception ex) { ShowError(ex.Message); }
        });
        menu.Opening += (_, _) => favorite.Text = IsFavoriteConversation(Current().Id) ? "Favorilerden çıkar" : "Favorilere ekle";
        menu.Items.AddAction("Yıldızlı mesajlar", ModernMenuIcon.Star, async (_, _) => await OpenStarredMessagesAsync(Current()));
        AddArchiveMuteActions(menu, Current);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.AddAction(group ? "Grup üyelerini görüntüle" : "Sohbet bilgileri",
            group ? ModernMenuIcon.Participants : ModernMenuIcon.Info, (_, _) =>
        {
            using var dialog = new ConversationMembersForm(_api, Current());
            dialog.ShowDialog(this);
        });
        if (group)
        {
            // Four infrequent group actions form one branch instead of stretching
            // the main popup over the conversation list. Keep the original guards.
            var options = menu.Items.AddAction("Grup seçenekleri", ModernMenuIcon.Settings);
            var rename = options.DropDownItems.AddAction("Grup adını değiştir", ModernMenuIcon.Edit,
                async (_, _) => await OpenGroupTitleAsync(Current()));
            var invite = options.DropDownItems.AddAction("Davet bağlantılarını yönet", ModernMenuIcon.Link,
                async (_, _) => await OpenGroupInviteAsync(Current()));
            options.DropDownItems.AddAction("Grup fotoğrafını düzenle", ModernMenuIcon.Photo,
                async (_, _) => await OpenGroupPhotoAsync(Current()));
            var manage = options.DropDownItems.AddAction("Grup yönetimi", ModernMenuIcon.Shield,
                async (_, _) => await OpenGroupManagementAsync(Current().Id));
            void RefreshGroupOptions()
            {
                rename.Visible = invite.Visible = CanRenameGroup(_session?.User, Current());
                manage.Visible = _session is { } session && (session.User.Role == "admin" ||
                    Current().GroupRoles?.GetValueOrDefault(session.User.Id) is "admin" or "mod");
            }
            menu.Opening += (_, _) => RefreshGroupOptions();
            options.DropDownOpening += (_, _) => RefreshGroupOptions();
        }
        menu.Items.Add(new ToolStripSeparator());
        var clear = menu.Items.AddAction(group ? "Bu grup mesajlarını sil" : "Bu sohbetin mesajlarını sil", ModernMenuIcon.Clear,
            async (_, _) => await ApplyConversationActionAsync(Current(), "clear"));
        var remove = menu.Items.AddAction("Bu sohbeti sil", ModernMenuIcon.Delete,
            async (_, _) => await ApplyConversationActionAsync(Current(), "remove"));
        clear.ForeColor = remove.ForeColor = Theme.Danger;
        ToolStripItem? leave = null;
        if (group)
        {
            leave = menu.Items.AddAction("Gruptan çık", ModernMenuIcon.Leave, async (_, _) => await ApplyConversationActionAsync(Current(), "leave"));
            leave.ForeColor = Theme.Danger;
        }
        menu.Opening += (_, _) =>
        {
            var enabled = !_sending && _voiceRecorder is null && !_voiceStopping;
            clear.Enabled = remove.Enabled = enabled;
            if (leave is not null) leave.Enabled = enabled;
        };
        void Attach(Control control)
        {
            control.ContextMenuStrip = menu;
            foreach (Control child in control.Controls) Attach(child);
        }
        Attach(card);
        card.Disposed += (_, _) => menu.Dispose();
    }

    private async Task ApplyConversationActionAsync(ConversationSummary conversation, string action)
    {
        var prompt = action switch
        {
            "clear" => $"“{conversation.Title}” mesajları yalnızca senden silinsin mi? Diğer katılımcıların mesajları korunur.",
            "remove" => $"“{conversation.Title}” sohbeti ve mevcut mesajları yalnızca senden silinsin mi? Yeni mesaj gelirse sohbet tekrar görünür.",
            _ => $"“{conversation.Title}” grubundan çıkmak istiyor musun? Yeni grup mesajlarını alamayacaksın."
        };
        if (MessageBox.Show(this, prompt, action == "leave" ? "Gruptan çık" : "Yalnızca benden sil",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            if (action == "clear") await _api.ClearConversationMessagesAsync(conversation.Id);
            else if (action == "remove") await _api.RemoveConversationAsync(conversation.Id);
            else await _api.LeaveConversationAsync(conversation.Id);
            if (IsDisposed) return;
            if (action == "clear") InvalidateConversationVisibility(conversation.Id);
            if (action != "clear" && _selectedConversation?.Id == conversation.Id) ResetConversationSelection();
            _renderFingerprint = _presenceFingerprint = null;
            // Wait behind any older polling response, then fetch fresh state. A silent
            // refresh may be skipped while polling and leave a removed card selectable.
            await LoadConversationsAsync();
            if (_selectedConversation?.Id == conversation.Id) await RefreshMessagesAsync(false);
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
    }

    private void ResetConversationSelection()
    {
        CloseComposerEmojis();
        FilterConversations(_premiumSearch.Text);
        CloseMessageInfo();
        ResetPinnedMessages();
        if (_selectedConversation is null && _messageList.Controls.Count == 1 &&
            Equals(_messageList.Controls[0].Tag, "empty")) return;
        _conversationVersion++;
        CancelHistoryLoad();
        ClearRenderedMessageCache();
        _historyLoadedConversation = null;
        ClearPendingImage(); ClearPendingFile(); ClearPendingVoice();
        _premiumComposer.Text = "";
        _selectedConversation = null; _selectedConversationCard = null;
        RefreshEncryptionRecipientStatus();
        _renderFingerprint = _presenceFingerprint = null;
        _conversationTitle.Text = "Sohbet seçin"; _conversationMeta.Text = "";
        _headerAvatar.Tag = null; _headerAvatar.SetEncodedPhoto(null);
        _headerAvatar.Visible = _securityStatus.Visible = false;
        SetConversationControlsEnabled(false);
        _messageList.SuspendLayout();
        try
        {
            foreach (Control row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
            _messageList.Controls.Clear();
            _messageList.Controls.Add(CreateEmptyState("Sohbet seçin"));
        }
        finally { _messageList.ResumeLayout(); }
        foreach (Control person in _presenceList.Controls.Cast<Control>().ToArray()) person.Dispose();
        _presenceList.Controls.Clear(); _presenceCount.Text = "";
        ResizeBubbles();
    }

    internal ContextMenuStrip ConversationActionsSnapshotMenu => _selectedConversationCard!.ContextMenuStrip!;

    internal void PopulateDirectActionsSnapshot()
    {
        PopulateSnapshot();
        var peer = _selectedConversation!.Participants.First(u => !u.IsAgent && u.Id != _session!.User.Id);
        var direct = _selectedConversation with { Kind = "direct", Title = peer.DisplayName,
            Participants = [_session!.User, peer], GroupRoles = null };
        foreach (Control card in _conversationList.Controls.Cast<Control>().ToArray()) card.Dispose();
        _conversationList.Controls.Clear();
        _selectedConversation = direct;
        _selectedConversationCard = CreateConversationCard(direct);
        _conversationList.Controls.Add(_selectedConversationCard);
    }

    internal void PopulateNoConversationSnapshot()
    {
        PopulateSnapshot();
        foreach (Control card in _conversationList.Controls.Cast<Control>().ToArray()) card.Dispose();
        _conversationList.Controls.Clear();
        ResetConversationSelection();
        if (_selectedConversation is not null || _premiumComposer.Enabled || _callButton.Enabled ||
            _presenceList.Controls.Count != 0 || _messageList.Controls.Count != 1)
            throw new InvalidOperationException("Empty conversation retained active chat controls.");
    }
}
