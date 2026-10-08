using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private bool _editingGroupTitle;
    internal static bool CanRenameGroup(ChatUser? actor, ConversationSummary group) =>
        actor is { IsAgent: false } && group.Kind != "direct" &&
        (actor.Role == "admin" || group.Participants.Any(user => user.Id == actor.Id) &&
            group.GroupRoles?.GetValueOrDefault(actor.Id) == "admin");

    private async Task OpenGroupTitleAsync(ConversationSummary group)
    {
        if (_editingGroupTitle || !CanRenameGroup(_session?.User, group)) return;
        _editingGroupTitle = true;
        try
        {
            using var editor = new GroupTitleEditorForm(_api, group);
            if (editor.ShowDialog(this) != DialogResult.OK || editor.UpdatedTitle is not { } update || IsDisposed) return;
            await ApplyGroupTitleAsync(update);
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
        finally { _editingGroupTitle = false; }
    }

    private async Task ApplyGroupTitleAsync(GroupTitleResult update)
    {
        // Wait behind any older list GET before applying the authoritative PUT result.
        // Do not reselect the room: that would discard drafts and rebuild its history.
        await _conversationListGate.WaitAsync();
        try
        {
            if (IsDisposed) return;
            var rooms = _conversationList.Controls.OfType<RoundedPanel>().Select(card => (ConversationSummary)card.Tag!).ToArray();
            ReconcileConversationCards(rooms.Select(room => room.Id == update.ConversationId ? room with { Title = update.Title } : room).ToArray());
            if (_selectedConversation?.Id == update.ConversationId)
            {
                _selectedConversation = _selectedConversation with { Title = update.Title };
                _selectedConversationCard = _conversationList.Controls.OfType<RoundedPanel>().Single(card => ((ConversationSummary)card.Tag!).Id == update.ConversationId);
                SetConversationCardSelected(_selectedConversationCard, true);
                _conversationTitle.Text = update.Title;
                _ = ApplyConversationAvatarAsync(_headerAvatar, _selectedConversation);
            }
        }
        finally { _conversationListGate.Release(); }
    }
}
