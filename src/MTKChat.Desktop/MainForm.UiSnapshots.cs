namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal void PopulateEmojiDraftSnapshot()
    {
        PopulateSnapshot();
        _premiumComposer.EditValue = null;
        InsertComposerEmoji("😊");
        _premiumComposer.Refresh();
    }

    internal void VerifySearchShortcut()
    {
        var message = Message.Create(Handle, 0x0100, IntPtr.Zero, IntPtr.Zero);
        if (!ProcessCmdKey(ref message, Keys.Control | Keys.K) || !_premiumSearch.ContainsFocus)
            throw new InvalidOperationException("Ctrl+K arama alanına odaklanmadı.");
        Console.WriteLine("Keyboard QA: Ctrl+K focuses the search editor.");
    }
}
