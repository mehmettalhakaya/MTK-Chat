using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private string GroupRole(Guid userId) => _selectedConversation?.Kind == "direct" ? "user" :
        _selectedConversation?.GroupRoles?.GetValueOrDefault(userId) ?? "user";

    private async Task OpenGroupManagementAsync(Guid? conversationId = null)
    {
        if (_session is null) return;
        using var form = new GroupManagementForm(_api, _session.User, conversationId ?? _selectedConversation?.Id);
        form.ShowDialog(this);
        _presenceFingerprint = _renderFingerprint = null;
        await LoadConversationsAsync(silent: true);
        await RefreshPresenceAsync();
        await RefreshMessagesAsync(true);
    }

    private async Task ChangeGroupRoleAsync(Guid room, Guid target, string role)
    {
        try
        {
            await _api.SetGroupRoleAsync(room, target, role);
            _presenceFingerprint = _renderFingerprint = null;
            await LoadConversationsAsync(silent: true);
            await RefreshPresenceAsync();
            await RefreshMessagesAsync(true);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private static string PresenceText(PresenceView person) => UserPresentation.PresenceText(person);
}
