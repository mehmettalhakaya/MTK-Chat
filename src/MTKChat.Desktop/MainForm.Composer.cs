using DevExpress.XtraEditors;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private const string ComposerPrompt = "Mesajınızı yazın...";
    private EmojiPicker? _composerEmojiPicker;
    private IDisposable? _composerEmojiRendering;
    private Label? _composerHint;
    private EmojiInsertionAnchor? _emojiInsertion;
    private sealed record EmojiInsertionAnchor(Guid? User, Guid? Conversation, int Version, string Text, int Start, int Length);

    private void ShowComposerEmojis(Control anchor)
    {
        if (!_premiumComposer.Enabled || _premiumComposer.Properties.ReadOnly || IsDisposed) return;
        if (_composerEmojiPicker?.Visible == true) { CloseComposerEmojis(); return; }
        _emojiInsertion = new(_session?.User.Id, _selectedConversation?.Id, _conversationVersion,
            _premiumComposer.Text, _premiumComposer.SelectionStart, _premiumComposer.SelectionLength);
        if (_composerEmojiPicker is null)
        {
            _composerEmojiPicker = new EmojiPicker();
            _composerEmojiPicker.EmojiChosen += emoji => AcceptComposerEmoji(emoji.Symbol);
            _composerEmojiPicker.Closed += (_, args) =>
            {
                var saved = _emojiInsertion; _emojiInsertion = null;
                // An outside click must retain its own destination focus. Only
                // keyboard dismissal returns the original caret to the editor.
                if (args.CloseReason == ToolStripDropDownCloseReason.Keyboard && EmojiAnchorIsCurrent(saved))
                {
                    _premiumComposer.Focus(); RestoreEmojiSelection(saved!);
                }
            };
        }
        _composerEmojiPicker.ShowFor(anchor);
    }

    private bool EmojiAnchorIsCurrent(EmojiInsertionAnchor? saved) => saved is not null && !IsDisposed &&
        _premiumComposer.Enabled && !_premiumComposer.Properties.ReadOnly &&
        saved.User == _session?.User.Id && saved.Conversation == _selectedConversation?.Id &&
        saved.Version == _conversationVersion && saved.Text == _premiumComposer.Text;

    private void RestoreEmojiSelection(EmojiInsertionAnchor saved)
    {
        _premiumComposer.SelectionStart = Math.Clamp(saved.Start, 0, _premiumComposer.Text.Length);
        _premiumComposer.SelectionLength = Math.Clamp(saved.Length, 0, _premiumComposer.Text.Length - _premiumComposer.SelectionStart);
    }

    private void AcceptComposerEmoji(string symbol)
    {
        var saved = _emojiInsertion;
        if (!EmojiAnchorIsCurrent(saved) || !EmojiCatalog.TryGet(symbol, out _)) return;
        RestoreEmojiSelection(saved!); _emojiInsertion = null;
        InsertComposerEmoji(symbol);
    }

    private void CloseComposerEmojis()
    {
        // Clear first: a synchronous Closed callback must not move focus or
        // revive a range after a conversation/account reset.
        _emojiInsertion = null; _composerEmojiPicker?.Close();
    }

    private void DisposeComposerEmojis()
    {
        CloseComposerEmojis(); _composerEmojiPicker?.Dispose(); _composerEmojiPicker = null;
        _composerEmojiRendering?.Dispose(); _composerEmojiRendering = null;
    }

    private void ConfigureComposerPrompt()
    {
        // DevExpress 26.1 documents these flags as the empty-string and focused-editor
        // cases. The hint is never message data and is disabled once real text exists.
        _premiumComposer.Properties.ShowNullValuePrompt = ShowNullValuePromptOptions.EmptyValue |
            ShowNullValuePromptOptions.EditorFocused;
        _premiumComposer.Properties.NullValuePromptForeColor = Theme.Muted;
        _premiumComposer.TextChanged += (_, _) => UpdateComposerPrompt();
        UpdateComposerPrompt();
    }

    private void UpdateComposerPrompt()
    {
        if (_composerHint is { IsDisposed: false } hint)
            hint.Visible = _premiumComposer.Enabled && _premiumComposer.Text.Length == 0 && !_premiumComposer.ContainsFocus;
        var prompt = _premiumComposer.Text.Length == 0 ? ComposerPrompt : "";
        if (_premiumComposer.Properties.NullValuePrompt == prompt) return;
        _premiumComposer.Properties.NullValuePrompt = prompt;
        // Repaint the entire native editor, rather than only the inserted glyph. This
        // also erases a previously drawn null-value hint after a menu selection.
        _premiumComposer.Invalidate(true);
    }

    private void AddComposerHint(Control frame)
    {
        // DevExpress Advanced Mode suppresses NullValuePrompt when a custom
        // highlighter is present. This is an empty/unfocused affordance only,
        // never draft data, a preview editor or a layer over native caret/IME.
        _composerHint = new Label { Text = ComposerPrompt, BackColor = Theme.Surface,
            ForeColor = Theme.Muted, Font = _premiumComposer.Properties.Appearance.Font,
            TextAlign = ContentAlignment.TopLeft, TabStop = false, AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText };
        frame.Controls.Add(_composerHint); _composerHint.BringToFront();
        void FitHint() { if (_composerHint is { IsDisposed: false }) _composerHint.Bounds = _premiumComposer.Bounds; }
        _premiumComposer.Layout += (_, _) => FitHint();
        _premiumComposer.LocationChanged += (_, _) => FitHint();
        _premiumComposer.SizeChanged += (_, _) => FitHint();
        _premiumComposer.Enter += (_, _) => _composerHint?.Hide();
        _premiumComposer.Leave += (_, _) =>
        {
            // Leave precedes assignment of the next native child focus.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) UpdateComposerPrompt(); })); }
            catch (InvalidOperationException) { /* Closing the form. */ }
        };
        _premiumComposer.EnabledChanged += (_, _) => UpdateComposerPrompt();
        _composerHint.MouseDown += (_, _) => FocusComposerFromHint();
        _premiumComposer.AccessibleName = ComposerPrompt;
        FitHint(); UpdateComposerPrompt();
    }

    private void FocusComposerFromHint()
    {
        if (!_premiumComposer.Enabled || IsDisposed) return;
        _composerHint?.Hide();
        if (!_premiumComposer.Focus()) UpdateComposerPrompt();
    }

    private void InsertComposerEmoji(string symbol)
    {
        if (!_premiumComposer.Enabled || _premiumComposer.Properties.ReadOnly) return;
        var selectionStart = _premiumComposer.SelectionStart;
        var selectionLength = _premiumComposer.SelectionLength;
        _premiumComposer.Focus();
        // Returning from the popup must not let focus-time selection policy replace
        // the user's original caret/range before the selected emoji is inserted.
        _premiumComposer.SelectionStart = selectionStart;
        _premiumComposer.SelectionLength = selectionLength;
        if (_premiumComposer.Text.Length == 0)
        {
            // Commit the first emoji as the editor's actual value. SelectedText alone
            // is not used to initialize a previously null, unfocused MemoEdit.
            _premiumComposer.Text = symbol;
            _premiumComposer.SelectionStart = symbol.Length;
            _premiumComposer.SelectionLength = 0;
        }
        else _premiumComposer.SelectedText = symbol; // Retain native selection/undo behavior for existing text.
        UpdateComposerPrompt();
        _premiumComposer.Refresh();
    }

    // Only SnapshotRenderer invokes this deterministic editing probe. It uses the real
    // picker/search/click handlers, with no clipboard, microphone, send or network action.
    internal IReadOnlyList<string> VerifyComposerEditing()
    {
        if (!IsHandleCreated || !_premiumComposer.IsHandleCreated)
            throw new InvalidOperationException("Composer QA requires a shown snapshot form.");
        var previousText = _premiumComposer.Text;
        var previousStart = _premiumComposer.SelectionStart;
        var previousLength = _premiumComposer.SelectionLength;
        var previousFocus = ActiveControl;
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Composer editing regression: " + check);
            checks.Add(check);
        }
        void Choose(string symbol)
        {
            ShowComposerEmojis(_premiumComposer);
            Application.DoEvents();
            var picker = _composerEmojiPicker!;
            picker.Panel.SetSearchForQa(EmojiCatalog.All.Single(emoji => emoji.Symbol == symbol).Name);
            picker.Panel.Grid.SelectForQa(picker.Panel.Grid.Items.ToList().FindIndex(emoji => emoji.Symbol == symbol));
            picker.Panel.Grid.ChooseSelected();
            Application.DoEvents();
        }
        try
        {
            _premiumComposer.EditValue = null;
            _premiumComposer.Focus(); Application.DoEvents();
            Require(_premiumComposer.Text == "" && _premiumComposer.Properties.NullValuePrompt == ComposerPrompt,
                "Empty/null composer retains a separate hint");
            Require(_premiumComposer.Properties.ShowNullValuePrompt.HasFlag(ShowNullValuePromptOptions.EmptyValue) &&
                _premiumComposer.Properties.ShowNullValuePrompt.HasFlag(ShowNullValuePromptOptions.EditorFocused),
                "Hint policy supports empty and focused editors");
            Choose("😊");
            Require(_premiumComposer.Text == "😊" && _premiumComposer.EditValue?.ToString() == "😊",
                "Real emoji-menu click initializes the actual edit value");
            Require(_premiumComposer.Properties.NullValuePrompt.Length == 0 &&
                !_premiumComposer.Text.Contains(ComposerPrompt, StringComparison.Ordinal),
                "Emoji-only content has no configured hint or appended placeholder data");
            _premiumComposer.SelectionStart = _premiumComposer.Text.Length; _premiumComposer.SelectionLength = 0;
            Choose("👍");
            Require(_premiumComposer.Text == "😊👍", "Emoji menu appends at the caret");
            _premiumComposer.Text = "Merhaba dünya";
            _premiumComposer.SelectionStart = 8; _premiumComposer.SelectionLength = 5;
            Choose("❤️");
            Require(_premiumComposer.Text == "Merhaba ❤️", "Emoji menu replaces the current selection");
            _premiumComposer.SelectionStart = 0; _premiumComposer.SelectionLength = _premiumComposer.Text.Length;
            _premiumComposer.SelectedText = ""; Application.DoEvents();
            Require(_premiumComposer.Text == "" && _premiumComposer.Properties.NullValuePrompt == ComposerPrompt,
                "Deleting all text restores only the empty-editor hint");
            return checks;
        }
        finally
        {
            CloseComposerEmojis();
            _premiumComposer.Text = previousText;
            _premiumComposer.SelectionStart = Math.Clamp(previousStart, 0, previousText.Length);
            _premiumComposer.SelectionLength = Math.Clamp(previousLength, 0, previousText.Length - _premiumComposer.SelectionStart);
            UpdateComposerPrompt();
            if (previousFocus is { IsDisposed: false, CanFocus: true }) previousFocus.Focus();
        }
    }
}
