using System.Runtime.InteropServices;
using System.Diagnostics;
using DevExpress.Utils;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;

namespace MTKChat.Desktop;

// Synthetic app-owned windows only: no user clipboard, network or live account.
internal static class ComposerEmojiQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("Composer emoji QA: " + label); checks.Add(label); }
        var beforeTargets = EmojiAnimationScheduler.RegisteredTargetCount;
        ComposerEmojiRendering rendering;
        using (var host = new Form
        {
            ClientSize = new Size(620, 184), BackColor = Theme.Canvas, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen, Text = "MTK Chat · Unicode composer QA",
            Padding = new Padding(16)
        })
        {
            using var font = Theme.Font(12);
            var editor = new MemoEdit { Dock = DockStyle.Fill, Font = font };
            editor.Properties.BorderStyle = BorderStyles.NoBorder;
            editor.Properties.ScrollBars = ScrollBars.None;
            editor.Properties.Appearance.BackColor = Theme.Surface;
            editor.Properties.Appearance.ForeColor = Theme.Text;
            editor.Properties.Appearance.Options.UseBackColor = true;
            editor.Properties.Appearance.Options.UseForeColor = true;
            editor.Properties.NullValuePrompt = "Mesajınızı yazın...";
            editor.Properties.NullValuePromptForeColor = Theme.Muted;
            editor.Properties.ShowNullValuePrompt = ShowNullValuePromptOptions.EmptyValue | ShowNullValuePromptOptions.EditorFocused;
            rendering = ComposerEmojiRendering.Attach(editor);
            host.Controls.Add(editor);
            host.Show(); host.Activate(); editor.Focus(); Application.DoEvents();
            Require(editor.Properties.UseAdvancedMode == DefaultBoolean.True &&
                editor.Properties.AdvancedModeOptions.UseDirectXPaint == DefaultBoolean.True,
                "The real MemoEdit enables Advanced Mode and its supported DirectX colour fallback");

            const string mixed = "Merhaba 😊 dünya ❤️\r\nİkinci satır 😉 ve 👍";
            editor.Text = mixed; editor.SelectionStart = 8; editor.SelectionLength = 2;
            editor.UpdateTextHighlight(); editor.Refresh(); Application.DoEvents();
            Require(editor.Text == mixed && editor.EditValue?.ToString() == mixed && editor.SelectedText == "😊",
                "Inline drawing retains exact multiline Unicode, edit value and selected emoji");
            Require(rendering.PaintCountForQa >= 4 && rendering.FrameRendersForQa >= 4,
                "The native advanced editor actually invokes the original catalog bitmap painter for mixed multiline text");
            editor.SelectedText = "😂"; Application.DoEvents();
            Require(editor.Text == mixed.Replace("😊", "😂", StringComparison.Ordinal),
                "Replacing an emoji selection uses the normal MemoEdit editing path");
            Require(editor.CanUndo, "The native editing operation remains undoable");
            editor.Undo(); Application.DoEvents();
            Require(editor.Text == mixed, "Native Undo restores the exact Unicode draft after an emoji replacement");

            editor.SelectionStart = editor.Text.Length; editor.SelectionLength = 0;
            editor.SelectedText = "!"; Application.DoEvents();
            Require(editor.Text == mixed + "!" && editor.SelectionStart == editor.Text.Length,
                "Text can be appended immediately after a coloured emoji without losing the caret");
            editor.SelectionStart = 0; editor.SelectionLength = editor.Text.Length;
            editor.SelectedText = ""; Application.DoEvents();
            Require(editor.Text.Length == 0 && !rendering.AnimatedDraftForQa,
                "Deleting all mixed emoji text restores an empty non-animated native draft");
            foreach (var text in new[] { "👩‍💻 👍🏽 🇹🇷", "A\u0301, العربية, 日本語, 😀", "", "😊😊😊😊", "🙂\n❤️" })
            {
                editor.Text = text; editor.UpdateTextHighlight(); editor.Refresh(); Application.DoEvents();
                Require(editor.Text == text && editor.EditValue?.ToString() == text,
                    "Unsupported joined/modifier/flag sequences and ordinary scripts remain exact Unicode: " + (text.Length == 0 ? "empty" : text));
            }
            editor.Text = "Merhaba 😊  ❤️  😉"; editor.SelectionStart = editor.Text.Length; editor.SelectionLength = 0;
            editor.UpdateTextHighlight(); editor.Refresh(); Application.DoEvents();
            PumpPresentation(editor, TimeSpan.FromMilliseconds(180));
            using (var bitmap = CaptureOwnWindow(host))
            {
                bitmap.Save(Path.Combine(directory, "emoji-composer-native.png"));
                var colours = 0;
                for (var y = editor.Top; y < Math.Min(bitmap.Height, editor.Bottom); y++)
                    for (var x = editor.Left; x < Math.Min(bitmap.Width, editor.Right); x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.R > 170 && pixel.G > 110 && pixel.B < 130) colours++;
                    }
                Require(colours > 30, "A capture of the actual QA editor contains coloured emoji artwork rather than monochrome text");
            }
            var textBefore = editor.Text; var startBefore = editor.SelectionStart; var lengthBefore = editor.SelectionLength;
            var layoutCount = 0; editor.Layout += (_, _) => layoutCount++;
            for (var frame = 0; frame < 30; frame++) EmojiAnimationScheduler.TickForQa(true, true);
            Require(editor.Text == textBefore && editor.SelectionStart == startBefore && editor.SelectionLength == lengthBefore && layoutCount == 0,
                "Animation invalidation never changes the draft, native selection or editor layout");
            Require(rendering.CachedFramesForQa <= 128, "The per-editor bitmap cache has a hard size bound");
            var paintsBefore = rendering.PaintCountForQa; var rendersBefore = rendering.FrameRendersForQa;
            var animationPermitted = EmojiAnimationScheduler.CanAnimate(rendering);
            PumpPresentation(editor, TimeSpan.FromMilliseconds(220));
            Require(rendering.PaintCountForQa > paintsBefore,
                "Pumped native repaint calls draw catalog blocks, not only invalidate the editor");
            Require(animationPermitted ? rendering.FrameRendersForQa > rendersBefore : rendering.FrameRendersForQa == rendersBefore,
                animationPermitted
                    ? "Elapsed-time native paints generate new animated bitmap frames while retaining the actual Unicode editor"
                    : "Windows reduced-motion or inactive-window policy reuses static bitmap frames during pumped native paints");
            editor.Properties.ReadOnly = true;
            Require(!EmojiAnimationScheduler.IsActiveForQa(rendering), "A read-only composer is not animated");
            editor.Properties.ReadOnly = false;
            Require(EmojiAnimationScheduler.IsActiveForQa(rendering) == EmojiAnimationScheduler.CanAnimate(rendering),
                "Making a focused composer writable restores its eligible animation state without a forced scheduler refresh");
            editor.Enabled = false; EmojiAnimationScheduler.RefreshVisibilityForQa(true, true);
            Require(!EmojiAnimationScheduler.IsActiveForQa(rendering), "A disabled composer is not animated");
            editor.Enabled = true; editor.Text = "sade metin"; Application.DoEvents();
            Require(!rendering.AnimatedDraftForQa && EmojiAnimationScheduler.RegisteredTargetCount == beforeTargets,
                "An emoji-free draft immediately unregisters its animation target");
            editor.Text = "😊"; Application.DoEvents();
            Require(rendering.AnimatedDraftForQa, "An animated emoji draft is detected without converting the draft to images");
            var longDraft = string.Concat(Enumerable.Repeat("😊", ComposerEmojiRendering.MaximumCatalogDraftLength));
            editor.Text = longDraft; editor.UpdateTextHighlight(); editor.Refresh(); Application.DoEvents();
            Require(editor.Text == longDraft && editor.EditValue?.ToString() == longDraft &&
                !rendering.AnimatedDraftForQa && EmojiAnimationScheduler.RegisteredTargetCount == beforeTargets,
                "An oversized pasted draft retains exact Unicode/native colour fallback without custom animation work");
            var longPaints = rendering.PaintCountForQa;
            editor.Refresh(); Application.DoEvents();
            Require(rendering.PaintCountForQa == longPaints,
                "Oversized drafts skip custom catalog block drawing rather than allocating a block per grapheme");
            editor.Text = "😊"; editor.UpdateTextHighlight(); editor.Refresh(); Application.DoEvents();
            Require(rendering.AnimatedDraftForQa && rendering.PaintCountForQa > longPaints,
                "Shortening an oversized draft restores catalog rendering and animation eligibility");
            host.Close();
        }
        Require(rendering.IsDisposedForQa && rendering.CachedFramesForQa == 0 &&
            EmojiAnimationScheduler.RegisteredTargetCount == beforeTargets,
            "Closing the editor releases bitmap cache and shared animation subscription");
        return checks;
    }

    private static void PumpPresentation(Control editor, TimeSpan duration)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            // DirectX presentation and WM_PAINT are asynchronous. Pump the QA
            // window for a bounded interval instead of assuming Refresh() has
            // completed GPU presentation or calling invalidation a rendered frame.
            editor.Invalidate(true); editor.Update(); Application.DoEvents();
            Thread.Sleep(10);
        } while (elapsed.Elapsed < duration);
        DwmFlush();
    }

    private static Bitmap CaptureOwnWindow(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc();
        try
        {
            // A child DirectX surface is not part of GetDC's GDI backing bitmap.
            // Request rendering of this QA-owned HWND's complete client content;
            // never read desktop pixels or replace the capture with painter output.
            if (!PrintWindow(control.Handle, destination, 0x00000001 | 0x00000002))
                throw new InvalidOperationException("Cannot capture the QA-owned composer window.");
        }
        catch { bitmap.Dispose(); throw; }
        finally { graphics.ReleaseHdc(destination); }
        return bitmap;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr destination, uint flags);
}
