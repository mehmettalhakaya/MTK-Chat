using System.Net;
using System.Security.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyEmojiPickerIntegration(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var handler = new EmojiNoNetworkHandler();
        using var api = new ChatApiClient("https://emoji-qa.invalid/", handler);
        using var form = new MainForm(snapshotMode: true, api);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var checks = new List<string>();
        void Require(bool value, string name)
        { if (!value) throw new InvalidOperationException("Emoji picker QA: " + name); checks.Add(name); }
        // A collapsed but visible native preview can still receive a one-pixel
        // HWND from TableLayoutPanel. Exercise the real draft lifecycle, not
        // only its row height, so an inactive preview cannot cover the editor.
        Require(form._pendingImage is null && form._imageDraftCard is { Visible: false } &&
            form._imageDraftRowStyle?.Height == 0,
            "The initial image preview is hidden as well as occupying a zero-height row");
        form.PopulateImageDraftSnapshot(); Application.DoEvents();
        Require(form._pendingImage is not null && form._imageDraftCard is { Visible: true } &&
            form._imageDraftPicture.Image is not null && form._imageDraftRowStyle?.Height > 0,
            "Staging a synthetic image shows the real preview and expands its draft row");
        var stagedImageBytes = form._pendingImage!.Bytes;
        form.ClearPendingImage(); Application.DoEvents();
        Require(form._pendingImage is null && form._imageDraftCard is { Visible: false } &&
            form._imageDraftPicture.Image is null && form._imageDraftName.Text == "" &&
            form._imageDraftRowStyle?.Height == 0 && form._composerHeightStyle?.Height == ComposerBaseHeight,
            "Clearing an image hides its native preview, releases its image and restores the empty composer row");
        Require(stagedImageBytes.All(value => value == 0),
            "Clearing the synthetic image also zeroes the pending plaintext byte buffer");
        var composer = form._premiumComposer;
        void VerifyMainFormEmptyHint(string phase)
        {
            var hint = form._composerHint;
            Require(hint is { Visible: true, TabStop: false } && hint.Parent == composer.Parent &&
                hint.Bounds == composer.Bounds && composer.Text.Length == 0 && !composer.ContainsFocus,
                "The actual MainForm exposes a separate, aligned empty/unfocused composer hint: " + phase);
            var textBeforeHint = composer.Text; var valueBeforeHint = composer.EditValue;
            var startBeforeHint = composer.SelectionStart; var lengthBeforeHint = composer.SelectionLength;
            var frame = hint!.Parent!;
            // Standalone MemoEdit's NullValuePrompt is suppressed by the vendor
            // custom highlighter. Test the actual shipped MainForm affordance,
            // including its native painted text, rather than that unused path.
            try
            {
                using var withHint = NativeQaCapture.Capture(frame);
                withHint.Save(Path.Combine(directory, $"emoji-composer-main-hint-{phase}.png"));
                hint.Hide();
                using var withoutHint = NativeQaCapture.Capture(frame);
                withoutHint.Save(Path.Combine(directory, $"emoji-composer-main-no-hint-{phase}.png"));
                var differences = 0;
                var textArea = Rectangle.Intersect(frame.ClientRectangle,
                    new Rectangle(hint.Left + 2, hint.Top + 1,
                        Math.Max(1, hint.Width - 4),
                        Math.Min(Math.Max(1, hint.Height - 2), (int)Math.Ceiling(hint.Font.GetHeight() * 2 + 8))));
                for (var y = textArea.Top; y < textArea.Bottom; y++)
                    for (var x = textArea.Left; x < textArea.Right; x++)
                    {
                        var visible = withHint.GetPixel(x, y); var empty = withoutHint.GetPixel(x, y);
                        if (Math.Abs(visible.R - empty.R) + Math.Abs(visible.G - empty.G) + Math.Abs(visible.B - empty.B) > 48)
                            differences++;
                    }
                Require(differences > 30,
                    $"The actual empty composer hint paints visible native text ({phase}, changed pixels={differences})");
            }
            finally { form.UpdateComposerPrompt(); Application.DoEvents(); }
            Require(hint.Visible && composer.Text == textBeforeHint && Equals(composer.EditValue, valueBeforeHint) &&
                composer.SelectionStart == startBeforeHint && composer.SelectionLength == lengthBeforeHint,
                "Actual hint capture restores visibility without changing draft data or selection: " + phase);
        }
        composer.EditValue = null; form._premiumSearch.Focus(); Application.DoEvents();
        VerifyMainFormEmptyHint("initial");
        form.FocusComposerFromHint(); Application.DoEvents();
        Require(composer.ContainsFocus && form._composerHint is { Visible: false } && composer.Text.Length == 0,
            "The real hint activation focuses the native editor and removes the label before typing");
        form.InsertComposerEmoji("😊"); Application.DoEvents();
        Require(composer.Text == "😊" && composer.EditValue?.ToString() == "😊" &&
            form._composerHint is { Visible: false },
            "Typing an emoji leaves only actual Unicode data with no label over the native caret");
        composer.SelectionStart = 0; composer.SelectionLength = composer.Text.Length;
        composer.SelectedText = ""; Application.DoEvents();
        Require(composer.Text.Length == 0 && composer.ContainsFocus && form._composerHint is { Visible: false },
            "Clearing the focused native editor does not cover its caret or IME with a hint label");
        form._premiumSearch.Focus(); Application.DoEvents();
        VerifyMainFormEmptyHint("after-emoji-clear");
        form.FocusComposerFromHint(); Application.DoEvents();
        Require(composer.ContainsFocus && form._composerHint is { Visible: false },
            "Reactivating the restored empty hint again removes it before native input");
        var savedDraft = "Merhaba dünya";
        composer.Text = savedDraft; composer.SelectionStart = 8; composer.SelectionLength = 5;
        form.ShowComposerEmojis(composer); Application.DoEvents();
        var picker = form._composerEmojiPicker!;
        var grid = picker.Panel.Grid;
        Require(picker.Visible && picker.Panel.ContainsFocus, "Actual popup opens with its own searchable focus");
        Require(Screen.FromControl(form).WorkingArea.Contains(picker.Bounds), "Popup is clamped inside the working area");
        Require(grid.Items.Count == EmojiCatalog.All.Count && grid.ColumnsForQa >= 5, "Virtual grid shows the full catalog without one native button per emoji");
        picker.Panel.SetSearchForQa("kalp"); Application.DoEvents();
        Require(grid.Items.Count > 0 && grid.Items.Count < EmojiCatalog.All.Count, "Turkish search filters the catalog");
        var heart = grid.Items.ToList().FindIndex(emoji => emoji.Symbol == "❤️");
        Require(heart >= 0, "Heart search retains the standard Unicode heart");
        grid.SelectForQa(heart); grid.ChooseSelected(); Application.DoEvents();
        Require(composer.Text == "Merhaba ❤️" && !picker.Visible, "Search focus cannot lose the original replacement range");
        Require(composer.Properties.NullValuePrompt == "" && composer.ContainsFocus, "Choosing returns focus and does not append placeholder data");

        composer.Text = ""; composer.EditValue = null; composer.SelectionStart = 0; composer.SelectionLength = 0;
        form.ShowComposerEmojis(composer); Application.DoEvents();
        grid.SelectForQa(grid.Items.ToList().FindIndex(emoji => emoji.Symbol == "😊")); grid.ChooseSelected(); Application.DoEvents();
        Require(composer.EditValue?.ToString() == "😊" && composer.Properties.NullValuePrompt == "", "A null editor commits Unicode as real data, never as a hint");
        foreach (var check in form.VerifyComposerEditing()) checks.Add(check);

        composer.Text = "Korunan taslak"; composer.SelectionStart = 3; composer.SelectionLength = 4;
        form.ShowComposerEmojis(composer); Application.DoEvents();
        picker.Close(ToolStripDropDownCloseReason.Keyboard); Application.DoEvents();
        Require(composer.Text == "Korunan taslak" && composer.SelectionStart == 3 && composer.SelectionLength == 4,
            "Escape-style dismissal preserves the draft and caret without sending");

        form.ShowComposerEmojis(composer); Application.DoEvents();
        var downHandled = picker.Panel.InvokeSearchKeyForQa(Keys.Down); Application.DoEvents();
        Require(downHandled && grid.ContainsFocus && grid.SelectedIndex >= 0 && grid.SelectedIndex < grid.Items.Count,
            $"The real search Down-key handler transfers keyboard selection to the grid (handled={downHandled}, focus={grid.ContainsFocus}, selected={grid.SelectedIndex}, popup={picker.Visible}, gridVisible={grid.Visible})");
        Require(picker.Panel.InvokeSearchKeyForQa(Keys.Escape) && !picker.Visible,
            "The real search Escape-key handler dismisses without changing conversations");
        composer.Text = ""; composer.SelectionStart = 0; composer.SelectionLength = 0;
        form.ShowComposerEmojis(composer); Application.DoEvents(); picker.Panel.SetSearchForQa("İçten gülümseme");
        Require(picker.Panel.InvokeSearchKeyForQa(Keys.Enter) && composer.Text == "😊" && !picker.Visible,
            "The real search Enter-key handler inserts the filtered result without sending");
        var hiddenAccessible = grid.AccessibilityObject.GetChild(0)!;
        hiddenAccessible.DoDefaultAction();
        Require(hiddenAccessible.Bounds.IsEmpty && hiddenAccessible.State.HasFlag(AccessibleStates.Offscreen) && composer.Text == "😊",
            "Hidden picker accessibility items are offscreen and cannot edit a draft");
        composer.Text = "Korunan taslak"; composer.SelectionStart = 3; composer.SelectionLength = 4;

        form.ShowComposerEmojis(composer); Application.DoEvents();
        var stale = form._emojiInsertion; form._conversationVersion++;
        form.AcceptComposerEmoji("😊");
        Require(composer.Text == "Korunan taslak", "A late old-conversation choice cannot modify a new scope");
        form.CloseComposerEmojis(); form._emojiInsertion = stale;
        form.AcceptComposerEmoji("😊"); form._emojiInsertion = null;
        Require(composer.Text == "Korunan taslak", "Closed callbacks cannot revive an expired range");

        form.ShowComposerEmojis(composer); Application.DoEvents(); composer.Text = "Yeni taslak";
        form.AcceptComposerEmoji("😊"); form.CloseComposerEmojis();
        Require(composer.Text == "Yeni taslak", "An externally changed draft invalidates the old insertion anchor");
        form.ShowComposerEmojis(composer); Application.DoEvents(); composer.Properties.ReadOnly = true;
        form.AcceptComposerEmoji("😊"); form.CloseComposerEmojis(); composer.Properties.ReadOnly = false;
        Require(composer.Text == "Yeni taslak", "A read-only composer cannot be changed by a late selection");
        form.ShowComposerEmojis(composer); Application.DoEvents();
        form._session = form._session! with { User = form._session!.User with { Id = Guid.NewGuid() } };
        form.AcceptComposerEmoji("😊"); form.CloseComposerEmojis();
        Require(composer.Text == "Yeni taslak", "An account switch rejects a stale emoji selection");

        form.ShowComposerEmojis(composer); Application.DoEvents();
        picker.Panel.SetCategoryForQa(EmojiCategory.Faces); Application.DoEvents();
        Require(grid.Items.Count > 0 && grid.Items.All(emoji => emoji.Category == EmojiCategory.Faces), "Category selection is a real filter");
        var categories = picker.Panel.Controls.OfType<EmojiCategoryBar>().Single();
        categories.DragOutsideForQa();
        Require(categories.SelectedCategory == EmojiCategory.Faces, "Captured mouse drags outside category bounds never index or select an invalid category");
        grid.SelectForQa(0);
        using (var image = new Bitmap(picker.Width, picker.Height)) { picker.DrawToBitmap(image, picker.ClientRectangle); image.Save(Path.Combine(directory, "emoji-picker-modern.png")); }
        picker.Refresh(); Application.DoEvents();
        using (var image = CaptureEmojiPopup(picker)) image.Save(Path.Combine(directory, "emoji-picker-native.png"));
        picker.Panel.SetSearchForQa("no-matching-emoji-qa");
        Require(grid.Items.Count == 0, "No-match search has a safe empty state");
        var accessible = grid.AccessibilityObject;
        Require(accessible.GetChildCount() == 0, "Accessibility list tracks the filtered result count");
        picker.Panel.Reset(); grid.SelectForQa(0);
        var oldAccessible = grid.AccessibilityObject.GetChild(0)!;
        picker.Panel.SetSearchForQa("no-matching-emoji-qa"); oldAccessible.DoDefaultAction();
        Require(oldAccessible.State.HasFlag(AccessibleStates.Unavailable), "Stale accessibility children cannot select a different filtered emoji");
        picker.Panel.Reset();
        foreach (var size in new[] { new Size(300, 360), new Size(392, 444), new Size(510, 500) })
        {
            picker.Size = size; picker.Panel.Size = new Size(size.Width - 2, size.Height - 2); Application.DoEvents();
            Require(picker.Panel.ClientRectangle.Contains(grid.Bounds) && grid.Width > 0 && grid.Height > 0, $"Picker grid remains inside {size.Width}×{size.Height}");
        }
        form.CloseComposerEmojis();
        Require(handler.Requests == 0, "Emoji search, selection and cancellation never use HTTP or send messages");
        RenderEmojiCatalog(directory, Require);
        form.Dispose(); Application.DoEvents();
        Require(picker.IsDisposed, "Main form disposal owns the hosted picker and its controls");
        return checks;
    }

    private static void RenderEmojiCatalog(string directory, Action<bool, string> require)
    {
        const int columns = 8, cell = 96;
        using var sheet = new Bitmap(columns * cell, ((EmojiCatalog.All.Count + columns - 1) / columns) * cell);
        using var graphics = Graphics.FromImage(sheet); graphics.Clear(Theme.Canvas); using var font = Theme.Font(8);
        for (var index = 0; index < EmojiCatalog.All.Count; index++)
        {
            var emoji = EmojiCatalog.All[index]; var x = index % columns * cell; var y = index / columns * cell;
            EmojiPainter.Draw(graphics, new RectangleF(x + 18, y + 5, 60, 60), emoji, .7, true);
            TextRenderer.DrawText(graphics, emoji.Name, font, new Rectangle(x + 2, y + 68, 92, 26), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            static byte[] Pixels(EmojiDefinition entry, double seconds, bool animate)
            {
                using var bitmap = new Bitmap(80, 80); using var g = Graphics.FromImage(bitmap); g.Clear(Color.Transparent);
                EmojiPainter.Draw(g, new RectangleF(4, 4, 72, 72), entry, seconds, animate);
                using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return SHA256.HashData(stream.ToArray());
            }
            var staticPixels = Pixels(emoji, 0, false);
            require(staticPixels.SequenceEqual(Pixels(emoji, 2.1, false)), $"Reduced-motion render is deterministic: {emoji.Name}");
            if (emoji.Animated)
                require(new[] { .4, .6, .8, 1d, 1.2, 1.6, 2d, 2.4, 2.55, 2.8, 3.2 }.Any(frame => !Pixels(emoji, frame, true).SequenceEqual(Pixels(emoji, 0, true))),
                    $"Animated emoji has genuine different expression frames: {emoji.Name}");
        }
        sheet.Save(Path.Combine(directory, "emoji-catalog-original.png"));
    }

    // Only this synthetic QA-owned popup is captured. Native DevExpress edit
    // children can be omitted by WM_PRINT/DrawToBitmap, concealing the search hint.
    private static Bitmap CaptureEmojiPopup(Control popup)
    {
        var bitmap = new Bitmap(popup.ClientSize.Width, popup.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc(); var source = InfoGetDC(popup.Handle);
        try
        {
            if (source == IntPtr.Zero || !InfoBitBlt(destination, 0, 0, bitmap.Width, bitmap.Height, source, 0, 0, 0x00cc0020))
                throw new InvalidOperationException("Cannot capture the QA-owned emoji popup.");
        }
        catch { bitmap.Dispose(); throw; }
        finally { if (source != IntPtr.Zero) InfoReleaseDC(popup.Handle, source); graphics.ReleaseHdc(destination); }
        return bitmap;
    }

    private sealed class EmojiNoNetworkHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }
}
