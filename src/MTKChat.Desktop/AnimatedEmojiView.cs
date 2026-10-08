using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// The original Unicode text remains in MessageRow. This small vector surface is
// merely a local presentation of a verified standalone emoji-only text message.
internal sealed class AnimatedEmojiView : Control, IEmojiAnimationTarget
{
    private readonly IReadOnlyList<EmojiDefinition> _emoji;
    internal int AnimationInvalidationsForQa { get; private set; }
    internal int EmojiCount => _emoji.Count;
    Control IEmojiAnimationTarget.AnimationControl => this;
    bool IEmojiAnimationTarget.WantsAnimation => _emoji.Any(emoji => emoji.Animated);

    internal AnimatedEmojiView(IReadOnlyList<EmojiDefinition> emoji)
    {
        if (emoji.Count is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(emoji));
        _emoji = emoji.ToArray();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = string.Join(", ", _emoji.Select(item => item.Name));
        AccessibleDescription = string.Join(" ", _emoji.Select(item => item.Symbol));
        Size = PreferredSize;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var logicalWidth = _emoji.Count switch { 1 => 82, 2 => 142, _ => 200 };
        return new Size(Scale(logicalWidth), Scale(82));
    }

    void IEmojiAnimationTarget.InvalidateAnimation()
    {
        AnimationInvalidationsForQa++;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); EmojiAnimationScheduler.Subscribe(this); }
    protected override void OnHandleDestroyed(EventArgs e)
    { EmojiAnimationScheduler.Unsubscribe(this); base.OnHandleDestroyed(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Parent is null) { e.Graphics.Clear(Theme.Canvas); return; }
        // Match the whole parent gradient, including translucent square corners.
        var state = e.Graphics.Save();
        e.Graphics.TranslateTransform(-Left, -Top);
        InvokePaintBackground(Parent, new PaintEventArgs(e.Graphics, Bounds));
        e.Graphics.Restore(state);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var gap = Math.Min(Scale(5), Width / Math.Max(1, _emoji.Count * 5));
        var edge = Math.Max(1, Math.Min(Height - Scale(4), (Width - gap * (_emoji.Count - 1)) / _emoji.Count));
        var total = edge * _emoji.Count + gap * (_emoji.Count - 1);
        var left = (Width - total) / 2f;
        var top = (Height - edge) / 2f;
        var animate = EmojiAnimationScheduler.CanAnimate(this);
        for (var index = 0; index < _emoji.Count; index++)
            EmojiPainter.Draw(e.Graphics, new RectangleF(left + index * (edge + gap), top, edge, edge),
                _emoji[index], EmojiAnimationScheduler.Seconds + index * .35, animate);
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    protected override void Dispose(bool disposing)
    {
        if (disposing) EmojiAnimationScheduler.Unsubscribe(this);
        base.Dispose(disposing);
    }
}
