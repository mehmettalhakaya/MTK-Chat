using System.Drawing.Imaging;
using System.Buffers;
using System.Globalization;
using DevExpress.Utils;
using DevExpress.XtraEditors;

namespace MTKChat.Desktop;

// The real MemoEdit remains the editor, including its Unicode value, input method,
// caret, selection and undo stack. Advanced-mode blocks change only presentation;
// there is deliberately no preview overlay or emoji image inside the draft data.
internal sealed class ComposerEmojiRendering : IDisposable, IEmojiAnimationTarget
{
    // A pathological pasted draft must not allocate custom blocks for tens of
    // thousands of graphemes on every keypress. Beyond this presentation budget
    // the native editor still renders the complete original Unicode draft.
    internal const int MaximumCatalogDraftLength = 4096;
    private readonly MemoEdit _editor;
    private static readonly SearchValues<char> EmojiStarters = SearchValues.Create(
        EmojiCatalog.All.Select(emoji => emoji.Symbol[0]).Distinct().ToArray());
    private readonly Dictionary<(string Symbol, int Side), Frame> _frames = new();
    private bool _animated;
    private bool _disposed;
    private bool _subscribed;
    private bool _readOnly;
    private int _paintCount;
    private int _frameRenders;
    private int _invalidations;
    private sealed record Frame(int Index, Bitmap Bitmap);

    internal static ComposerEmojiRendering Attach(MemoEdit editor) => new(editor);

    private ComposerEmojiRendering(MemoEdit editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        if (editor.IsHandleCreated)
            throw new InvalidOperationException("Emoji rendering must be configured before the editor handle is created.");
        // Advanced Mode supplies editable bitmap blocks. DirectX also keeps
        // native colour-font fallback for sequences outside our own catalog.
        editor.Properties.UseAdvancedMode = DefaultBoolean.True;
        editor.Properties.AdvancedModeOptions.UseDirectXPaint = DefaultBoolean.True;
        editor.Properties.AdvancedModeOptions.AllowCaretAnimation = DefaultBoolean.False;
        editor.Properties.AdvancedModeOptions.AllowSelectionAnimation = DefaultBoolean.False;
        editor.CustomHighlightText += HighlightText;
        editor.TextChanged += DraftChanged;
        editor.HandleCreated += HandleCreated;
        editor.HandleDestroyed += HandleDestroyed;
        editor.Enter += FocusChanged;
        editor.Leave += FocusChanged;
        editor.EnabledChanged += FocusChanged;
        _readOnly = editor.Properties.ReadOnly;
        editor.PropertiesChanged += EditorPropertiesChanged;
        editor.DpiChangedAfterParent += DpiChanged;
        editor.Disposed += EditorDisposed;
        UpdateAnimationState();
    }

    Control IEmojiAnimationTarget.AnimationControl => _editor;
    bool IEmojiAnimationTarget.WantsAnimation => !_disposed && _animated &&
        _editor.Enabled && !_editor.Properties.ReadOnly && _editor.ContainsFocus;
    void IEmojiAnimationTarget.InvalidateAnimation()
    {
        if (_disposed || _editor.IsDisposed) return;
        _invalidations++;
        // Repaint the native advanced editor without rebuilding its text blocks,
        // assigning Text or causing a per-frame layout/caret reset.
        _editor.Invalidate(true);
    }

    private void HighlightText(object sender, TextEditCustomHighlightTextEventArgs args)
    {
        if (_disposed || _editor.Text.Length > MaximumCatalogDraftLength ||
            string.IsNullOrEmpty(args.Text) || args.Text.AsSpan().IndexOfAny(EmojiStarters) < 0) return;
        var elements = StringInfo.GetTextElementEnumerator(args.Text);
        while (elements.MoveNext())
        {
            var symbol = elements.GetTextElement();
            if (!EmojiCatalog.TryGet(symbol, out var emoji)) continue;
            var side = EmojiSide;
            args.HighlightRange(elements.ElementIndex, symbol.Length, block =>
            {
                block.Painter = new InlinePainter(this, emoji, side);
                block.ContentSize = new Size(side, side);
                // A grapheme is one visual edit block, not two independent UTF-16
                // surrogate halves. The complete original string stays selectable.
                block.AllowNavigation = false;
            });
        }
    }

    private int EmojiSide => Math.Clamp(Math.Max(_editor.Font.Height + Scale(3), Scale(22)), Scale(18), Scale(40));
    private int Scale(int logical) => Math.Max(1, (int)Math.Round(logical * _editor.DeviceDpi / 96d));

    private Bitmap GetFrame(EmojiDefinition emoji, int side)
    {
        var animate = emoji.Animated && EmojiAnimationScheduler.CanAnimate(this);
        var index = animate ? (int)(EmojiAnimationScheduler.Seconds * 20) % 72 : -1;
        var key = (emoji.Symbol, side);
        if (_frames.TryGetValue(key, out var cached) && cached.Index == index) return cached.Bitmap;
        // Font and DPI changes cannot grow a per-editor image cache indefinitely.
        // At most the small catalog is normally present, and 128 is a hard bound.
        if (_frames.Count >= 128 && !_frames.ContainsKey(key)) ClearFrames();
        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            EmojiPainter.Draw(graphics, new RectangleF(0, 0, side, side), emoji,
                index < 0 ? 0 : index / 20d, animate);
        }
        _frameRenders++;
        if (_frames.Remove(key, out var previous)) previous.Bitmap.Dispose();
        _frames.Add(key, new Frame(index, bitmap));
        return bitmap;
    }

    private sealed class InlinePainter(ComposerEmojiRendering owner, EmojiDefinition emoji, int side)
        : TextEdit.TextEditBlockPainter
    {
        public override bool DrawForeground(TextEdit.Block block)
        {
            if (owner._disposed || owner._editor.IsDisposed) return false;
            var bitmap = owner.GetFrame(emoji, side);
            foreach (var segment in block.Segments)
            {
                var bounds = segment.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                var edge = Math.Min(side, Math.Min(bounds.Width, bounds.Height));
                if (edge <= 0) continue;
                var target = new RectangleF(bounds.X + (bounds.Width - edge) / 2f,
                    bounds.Y + (bounds.Height - edge) / 2f, edge, edge);
                DrawBitmap(bitmap, target, new RectangleF(PointF.Empty, bitmap.Size));
                owner._paintCount++;
            }
            return true;
        }
    }

    private void DraftChanged(object? sender, EventArgs args) => UpdateAnimationState();
    private void UpdateAnimationState()
    {
        _animated = false;
        // Plain drafts are the common case. Avoid allocating one grapheme string
        // per character on every native TextChanged callback. Keep the handler
        // stable for the editor's lifetime: changing it during editing can leave
        // stale native document ranges in DevExpress's advanced controller.
        var useCatalog = _editor.Text.Length is > 0 and <= MaximumCatalogDraftLength &&
            _editor.Text.AsSpan().IndexOfAny(EmojiStarters) >= 0;
        if (!useCatalog) { UpdateSubscription(); return; }
        var elements = StringInfo.GetTextElementEnumerator(_editor.Text);
        while (elements.MoveNext())
            if (EmojiCatalog.TryGet(elements.GetTextElement(), out var emoji) && emoji.Animated)
            { _animated = true; break; }
        UpdateSubscription();
    }
    private void UpdateSubscription()
    {
        var wanted = !_disposed && _animated && _editor.IsHandleCreated && !_editor.IsDisposed;
        if (wanted && !_subscribed) { _subscribed = true; EmojiAnimationScheduler.Subscribe(this); }
        else if (!wanted && _subscribed) { _subscribed = false; EmojiAnimationScheduler.Unsubscribe(this); }
        else if (wanted) EmojiAnimationScheduler.RefreshVisibility();
    }
    private void HandleCreated(object? sender, EventArgs args) => UpdateSubscription();
    private void HandleDestroyed(object? sender, EventArgs args)
    {
        if (_subscribed) { _subscribed = false; EmojiAnimationScheduler.Unsubscribe(this); }
    }
    private void FocusChanged(object? sender, EventArgs args)
    {
        EmojiAnimationScheduler.RefreshVisibility();
        if (_disposed || !_editor.IsHandleCreated || _editor.IsDisposed) return;
        // Enter can precede native child focus assignment. Check once more after
        // that assignment; a destroyed HWND may drop this harmless unflagged call.
        try { _editor.BeginInvoke((Action)(() =>
        { if (!_disposed && !_editor.IsDisposed) EmojiAnimationScheduler.RefreshVisibility(); })); }
        catch (InvalidOperationException) { /* Editor is closing; no work remains. */ }
    }
    private void EditorPropertiesChanged(object? sender, EventArgs args)
    {
        if (_disposed || _readOnly == _editor.Properties.ReadOnly) return;
        _readOnly = _editor.Properties.ReadOnly;
        // ReadOnly is a repository property, not EnabledChanged. Resume eligible
        // motion immediately when an already focused editor becomes writable,
        // even when its shared timer stopped while the editor was read-only.
        EmojiAnimationScheduler.RefreshVisibility();
        _editor.Invalidate(true);
    }
    private void DpiChanged(object? sender, EventArgs args)
    {
        ClearFrames();
        if (!_disposed && _editor.IsHandleCreated) _editor.UpdateTextHighlight();
    }
    private void EditorDisposed(object? sender, EventArgs args) => Dispose();
    private void ClearFrames()
    { foreach (var frame in _frames.Values) frame.Bitmap.Dispose(); _frames.Clear(); }

    internal int PaintCountForQa => _paintCount;
    internal int FrameRendersForQa => _frameRenders;
    internal int CachedFramesForQa => _frames.Count;
    internal int AnimationInvalidationsForQa => _invalidations;
    internal bool IsDisposedForQa => _disposed;
    internal bool AnimatedDraftForQa => _animated;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed) { _subscribed = false; EmojiAnimationScheduler.Unsubscribe(this); }
        _editor.CustomHighlightText -= HighlightText;
        _editor.TextChanged -= DraftChanged;
        _editor.HandleCreated -= HandleCreated;
        _editor.HandleDestroyed -= HandleDestroyed;
        _editor.Enter -= FocusChanged;
        _editor.Leave -= FocusChanged;
        _editor.EnabledChanged -= FocusChanged;
        _editor.PropertiesChanged -= EditorPropertiesChanged;
        _editor.DpiChangedAfterParent -= DpiChanged;
        _editor.Disposed -= EditorDisposed;
        ClearFrames();
    }
}
