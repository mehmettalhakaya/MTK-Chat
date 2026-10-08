using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Used by the standalone screenshot runner. Production opens the same editor
// inside the collapsible sidebar rather than placing another modal over chat.
internal sealed class ProfileForm : ModernForm
{
    internal ProfileForm(ChatApiClient api, ChatUser user, AvatarCache cache, bool snapshotMode = false)
    {
        Text = "MTK Chat · Profilim"; Icon = Theme.AppIcon();
        ClientSize = new Size(420, 580); MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var editor = new ProfileEditor(api, user, cache, snapshotMode) { Dock = DockStyle.Fill };
        Controls.Add(editor);
    }
}
