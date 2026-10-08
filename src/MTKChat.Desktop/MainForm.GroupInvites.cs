using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private bool _editingGroupInvite;

    private async Task OpenGroupInviteAsync(ConversationSummary group)
    {
        // Group moderators cannot issue entrance capabilities or global admin access.
        if (_editingGroupInvite || !CanRenameGroup(_session?.User, group)) return;
        _editingGroupInvite = true;
        try { using var editor = new GroupInviteForm(_api, group); editor.ShowDialog(this); }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
        finally { _editingGroupInvite = false; }
        await Task.CompletedTask;
    }

    private async Task OpenJoinGroupInviteAsync()
    {
        if (_session is null || _openingConversation || _sending || _voiceRecorder is not null || _voiceStopping) return;
        _openingConversation = true;
        try
        {
            using var join = new JoinGroupInviteForm(_api, _session.User.Id);
            if (join.ShowDialog(this) != DialogResult.OK || join.JoinedConversation is not { } group || IsDisposed) return;
            await ApplyJoinedGroupAsync(group.Id);
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
        finally { _openingConversation = false; }
    }

    private bool CanSelectJoinedGroup() => !_sending && _voiceRecorder is null && !_voiceStopping &&
        _pendingImage is null && _pendingFile is null && _pendingVoice is null && string.IsNullOrWhiteSpace(_premiumComposer.Text);

    private Task ApplyJoinedGroupAsync(Guid groupId)
    {
        // Joining must never clear an existing text/image/file/voice draft or switch
        // the recipient of an in-flight send. With a draft, just refresh the list.
        return LoadConversationsAsync(CanSelectJoinedGroup() ? groupId : null);
    }
}
