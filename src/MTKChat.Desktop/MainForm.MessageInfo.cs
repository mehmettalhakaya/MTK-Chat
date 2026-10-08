using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private MessageInfoView? _messageInfoSelection;
    private MessageInfoPanel? _messageInfoPanel;
    private Control? _detailsHost;
    private Control? _presencePanel;
    private Control? _chatSurface;
    private bool _messageInfoOverlayMode;
    private bool _arrangingDetails;
    private bool _detailsLayoutQueued;
    internal bool HasOpenMessageInfo => _messageInfoSelection is not null;

    private Control BuildDetailsHost()
    {
        _detailsHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Sidebar };
        _presencePanel = BuildPremiumPresence();
        _messageInfoPanel = new MessageInfoPanel(_avatars) { Dock = DockStyle.Fill, Visible = false };
        _messageInfoPanel.CloseRequested += (_, _) => CloseMessageInfo(focusComposer: true);
        _detailsHost.Controls.Add(_presencePanel);
        _detailsHost.Controls.Add(_messageInfoPanel);
        return _detailsHost;
    }

    private void ShowMessageInfo(StoredMessage message, MessageRow row)
    {
        // Menus may outlive a selection change. Only the selected room's own,
        // non-deleted message can open sender receipt metadata; never keep a row
        // reference, because a later history render disposes/replaces rows.
        if (_session is null || _selectedConversation?.Id != message.ConversationId ||
            message.SenderId != _session.User.Id || message.DeletedForEveryone ||
            row.IsDisposed || row.MessageId != message.Id) return;
        _messageInfoSelection = new MessageInfoView(message.Id, message.ConversationId,
            message.CreatedAt, message.Kind, row.Delivery, _selectedConversation.Participants.ToArray(),
            _selectedConversation.GroupRoles);
        _messageInfoPanel!.Render(_messageInfoSelection);
        UpdatePresenceVisibility();
    }

    private void RefreshOpenMessageInfo(IReadOnlyList<StoredMessage> messages)
    {
        if (_messageInfoSelection is not { } current) return;
        var message = messages.FirstOrDefault(item => item.Id == current.MessageId);
        if (_selectedConversation?.Id != current.ConversationId || message is null ||
            message.ConversationId != current.ConversationId || message.DeletedForEveryone ||
            message.SenderId != _session?.User.Id)
        {
            CloseMessageInfo();
            return;
        }
        // Preserve known names for recipients who left. The panel still uses only
        // Delivery.Recipients, not the current group's membership, as its audience.
        var people = current.Participants.Concat(_selectedConversation.Participants)
            .GroupBy(person => person.Id).Select(group => group.Last()).ToArray();
        _messageInfoSelection = current with { Delivery = message.Delivery, Participants = people,
            GroupRoles = _selectedConversation.GroupRoles };
        _messageInfoPanel!.Render(_messageInfoSelection);
    }

    private void CloseMessageInfo(bool focusComposer = false)
    {
        if (_messageInfoSelection is null) return;
        _messageInfoSelection = null;
        UpdatePresenceVisibility();
        if (focusComposer && _selectedConversation is not null && _premiumComposer.CanFocus)
            _premiumComposer.Focus();
    }

    private void ArrangeDetails()
    {
        if (_arrangingDetails || _detailsHost is null || _presencePanel is null ||
            _messageInfoPanel is null || _chatSurface is null || IsDisposed || Disposing ||
            _detailsHost.IsDisposed || _messageInfoPanel.IsDisposed || _chatSurface.IsDisposed) return;
        _arrangingDetails = true;
        try
        {
            var open = _messageInfoSelection is not null;
            _messageInfoOverlayMode = open && ClientSize.Width < 1280;
            var parent = _messageInfoOverlayMode ? _chatSurface : _detailsHost;
            if (_messageInfoPanel.Parent != parent)
            {
                _messageInfoPanel.Visible = false;
                parent.Controls.Add(_messageInfoPanel);
            }
            _messageInfoPanel.Dock = _messageInfoOverlayMode ? DockStyle.None : DockStyle.Fill;
            var width = open && !_messageInfoOverlayMode ? 340 :
                !open && _participantsShown && ClientSize.Width >= 1280 ? 280 : 0;
            var widthChanged = Math.Abs(_premiumRoot.ColumnStyles[2].Width - width) > .1f;
            _premiumRoot.ColumnStyles[2].Width = width;
            _detailsHost.Visible = width > 0;
            _presencePanel.Visible = !open && width > 0;
            if (_messageInfoOverlayMode)
            {
                // Overlay in narrow windows: opening info must not reduce the text
                // editor to a few pixels or disturb the user's unsent draft.
                var panelWidth = Math.Min(340, _chatSurface.ClientSize.Width);
                _messageInfoPanel.SetBounds(_chatSurface.ClientSize.Width - panelWidth,
                    0, panelWidth, _chatSurface.ClientSize.Height);
            }
            _messageInfoPanel.Visible = open;
            if (open) _messageInfoPanel.BringToFront();
            else _presencePanel.BringToFront();
            if (widthChanged) { ResizeBubbles(); QueueDetailsLayout(); }
        }
        finally { _arrangingDetails = false; }
    }

    private void QueueDetailsLayout()
    {
        if (_detailsLayoutQueued || IsDisposed || Disposing || !IsHandleCreated) return;
        _detailsLayoutQueued = true;
        try
        {
            BeginInvoke((Action)(() =>
            {
                _detailsLayoutQueued = false;
                if (IsDisposed || Disposing || _premiumRoot.IsDisposed) return;
                // ColumnStyles can change inside TableLayoutPanel's own resize
                // pass. Its cached widths can otherwise retain a closed 340px
                // detail column even though the style says zero.
                _premiumRoot.PerformLayout(); ArrangeDetails();
            }));
        }
        catch (InvalidOperationException) { _detailsLayoutQueued = false; }
    }
}
