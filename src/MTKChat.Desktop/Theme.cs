namespace MTKChat.Desktop;

internal static class Theme
{
    public static readonly Color Canvas = Color.FromArgb(11, 16, 40);
    public static readonly Color Rail = Color.FromArgb(20, 25, 54);
    public static readonly Color Sidebar = Color.FromArgb(13, 18, 40);
    public static readonly Color Surface = Color.FromArgb(28, 34, 60);
    public static readonly Color SurfaceRaised = Color.FromArgb(35, 41, 72);
    public static readonly Color SurfaceHover = Color.FromArgb(48, 49, 88);
    public static readonly Color Divider = Color.FromArgb(48, 54, 87);
    public static readonly Color Accent = Color.FromArgb(100, 64, 255);
    public static readonly Color AccentHover = Color.FromArgb(118, 83, 255);
    public static readonly Color AccentInk = Color.White;
    public static readonly Color Violet = Color.FromArgb(165, 144, 255);
    public static readonly Color Cyan = Color.FromArgb(71, 200, 239);
    public static readonly Color GradientEnd = Color.FromArgb(51, 70, 255);
    public static readonly Color Outgoing = Color.FromArgb(52, 52, 222);
    public static readonly Color Text = Color.FromArgb(244, 245, 252);
    public static readonly Color Muted = Color.FromArgb(165, 174, 211);
    public static readonly Color Success = Color.FromArgb(78, 220, 169);
    public static readonly Color Bot = Color.FromArgb(185, 157, 255);
    public static readonly Color Warning = Color.FromArgb(247, 192, 88);
    public static readonly Color Danger = Color.FromArgb(246, 99, 113);

    // Segoe UI Variable is absent on Windows 10 and silently fell back to a wider
    // family. Use the installed Windows UI family for identical measurement/paint.
    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
    public static Font Glyph(float size) => new("Segoe MDL2 Assets", size, FontStyle.Regular);

    public static Icon AppIcon() => new(Path.Combine(AppContext.BaseDirectory, "Assets", "mtk-chat.ico"));

    public static ModernContextMenu ContextMenu() => new();

    public static ModernButton Button(string text, ButtonKind kind = ButtonKind.Secondary) => new()
    {
        Text = text,
        Kind = kind,
        Height = 42,
        Font = Font(9.25f),
        Cursor = Cursors.Hand
    };

    public static ModernButton GlyphButton(string glyph, string tooltip, ButtonKind kind = ButtonKind.Ghost)
    {
        // Existing call sites use MDL2 codes, but these affordances must also
        // survive different Windows font versions and compact high-DPI slots.
        var vector = glyph switch
        {
            "\uE724" => ModernButtonIcon.Send,
            "\uE76E" => ModernButtonIcon.Smile,
            "\uE723" => ModernButtonIcon.Attachment,
            "\uE717" => ModernButtonIcon.Phone,
            "\uE716" => ModernButtonIcon.Participants,
            "\uE712" => ModernButtonIcon.More,
            "\uE711" => ModernButtonIcon.Close,
            "\uE721" => ModernButtonIcon.Search,
            _ => ModernButtonIcon.None
        };
        var button = new ModernButton
        {
            Text = vector == ModernButtonIcon.None ? glyph : string.Empty,
            VectorIcon = vector,
            CircularSurface = vector == ModernButtonIcon.Send,
            Kind = kind,
            Font = vector == ModernButtonIcon.None ? Glyph(13f) : Font(9.25f),
            Size = new Size(42, 42),
            Cursor = Cursors.Hand,
            AccessibleName = tooltip
        };
        // The button owns this helper just as it owns its popup. Reopening dialogs
        // must not defer release of tooltip windows/resources until a future GC.
        var tip = new ToolTip { InitialDelay = 250, ReshowDelay = 100 };
        tip.SetToolTip(button, tooltip);
        button.Disposed += (_, _) => tip.Dispose();
        return button;
    }
}
