using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private bool _openingConversation;
    private Task ApplyConversationAvatarAsync(AvatarView avatar, ConversationSummary conversation)
    {
        var peer = conversation.Kind == "direct" ? conversation.Participants.FirstOrDefault(u => u.Id != _session?.User.Id) : null;
        return peer is null ? _avatars.ApplyGroupAsync(avatar, conversation) : _avatars.ApplyAsync(avatar, peer);
    }
    private async Task OpenDirectAsync(Guid userId)
    {
        if (_session is null || _openingConversation) return;
        _openingConversation = true;
        try
        {
            var conversation = await _api.OpenDirectAsync(userId);
            await LoadConversationsAsync(conversation.Id);
            if (!IsDisposed) _premiumComposer.Focus();
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
        finally { _openingConversation = false; }
    }
    private async Task NewConversationAsync(bool group)
    {
        if (_session is null || _openingConversation) return;
        _openingConversation = true;
        try
        {
            var users = (await _api.GetUsersAsync()).Where(u => !u.IsAgent && u.Id != _session.User.Id).ToArray();
            if (IsDisposed) return;
            using var picker = new ConversationPickerForm(users, group, _avatars);
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var conversation = group
                ? await _api.CreateConversationAsync(new(picker.GroupTitle, picker.Selected))
                : await _api.OpenDirectAsync(picker.Selected.Single());
            await LoadConversationsAsync(conversation.Id);
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
        finally { _openingConversation = false; }
    }
}
