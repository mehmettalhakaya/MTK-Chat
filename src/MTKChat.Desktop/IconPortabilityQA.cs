using System.Drawing.Imaging;
using System.Reflection;

namespace MTKChat.Desktop;

// No change to the desktop's display settings. These are production control
// owner-draw/font-substitution and scaled-pixel geometry tests, not a claim that
// the OS delivered native WM_DPICHANGED on four physical monitors.
internal static class IconPortabilityQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        var icons = new[]
        {
            ("\uE76E", ModernButtonIcon.Smile, "Emoji ekle"),
            ("\uE723", ModernButtonIcon.Attachment, "Görsel veya dosya ekle"),
            ("\uE717", ModernButtonIcon.Phone, "Sesli arama başlat"),
            ("\uE716", ModernButtonIcon.Participants, "Katılımcıları göster"),
            ("\uE712", ModernButtonIcon.More, "Sohbet seçenekleri"),
            ("\uE711", ModernButtonIcon.Close, "Mesaj bilgisini kapat"),
            ("\uE724", ModernButtonIcon.Send, "Mesajı gönder"),
            ("\uE721", ModernButtonIcon.Search, "Sohbetlerde ara")
        };
        // An unshown stock Panel may delegate its background to native WM_ERASE
        // instead of this owner-draw invocation. Use an explicit opaque fixture
        // surface so dirty/clean comparison does not accumulate alpha on corners.
        using var host = new FixtureSurface { Size = new Size(800, 140) };
        using var fallbackSmall = new Font("Arial", 7, FontStyle.Regular);
        using var fallbackLarge = new Font("Times New Roman", 34, FontStyle.Bold);
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            int Scale(int value) => (int)Math.Round(value * dpi / 96f);
            using var strip = new Bitmap(Scale(icons.Length * 48), Scale(62));
            using var stripGraphics = Graphics.FromImage(strip);
            stripGraphics.Clear(Theme.Surface);
            foreach (var (glyph, expected, label) in icons)
            {
                using var button = Theme.GlyphButton(glyph, label, expected == ModernButtonIcon.Send ? ButtonKind.Primary : ButtonKind.Secondary);
                button.CornerRadius = expected == ModernButtonIcon.Send ? 30 : 13;
                button.SetBounds(0, 0, Scale(40), Scale(50));
                host.Controls.Add(button);
                Require(button.VectorIcon == expected && button.Text.Length == 0 && button.AccessibleName == label,
                    $"{label}: font text is absent and accessibility name survives");
                using var original = Render(button);
                var oldFont = button.Font;
                button.Font = fallbackSmall;
                using var smallFont = Render(button);
                button.Font = fallbackLarge;
                using var largeFont = Render(button);
                Require(Equal(original, smallFont) && Equal(original, largeFont),
                    $"{label} at {dpi * 100 / 96}%: glyph metrics cannot change pixels");
                button.Font = oldFont;
                button.VectorIcon = ModernButtonIcon.None;
                using var background = Render(button);
                button.VectorIcon = expected;
                var inkBounds = DifferenceBounds(original, background);
                var safeClient = Rectangle.Inflate(button.ClientRectangle, -Scale(4), -Scale(4));
                Require(!inkBounds.IsEmpty && safeClient.Contains(inkBounds),
                    $"{label} at {dpi * 100 / 96}%: icon ink is nonempty and has a safe inset");
                // Repaint onto the same dirty surface, including a disabled/hover
                // transition. A clean snapshot alone could hide old glyph dots.
                using var dirty = new Bitmap(button.Width, button.Height);
                Paint(button, dirty);
                button.Enabled = false;
                Paint(button, dirty);
                using var cleanDisabled = Render(button);
                if (!Equal(dirty, cleanDisabled))
                {
                    dirty.Save(Path.Combine(directory, "icon-disabled-stale.png"));
                    cleanDisabled.Save(Path.Combine(directory, "icon-disabled-expected.png"));
                }
                Require(Equal(dirty, cleanDisabled), $"{label}: disabling clears previous ink");
                button.Enabled = true;
                Invoke(button, "OnMouseEnter", EventArgs.Empty);
                Paint(button, dirty);
                using var cleanHovered = Render(button);
                Require(Equal(dirty, cleanHovered), $"{label}: hovering clears disabled ink");
                Invoke(button, "OnMouseLeave", EventArgs.Empty);
                results.Add($"{expected}: {dpi * 100 / 96}% scaled-pixel slot, small/large font substitution, inset and dirty hover/disable passed.");
                stripGraphics.DrawImageUnscaled(original, Scale(Array.FindIndex(icons, icon => icon.Item2 == expected) * 48 + 4), Scale(6));
                host.Controls.Remove(button);
            }
            strip.Save(Path.Combine(directory, $"portable-toolbar-{dpi}dpi-geometry.png"), ImageFormat.Png);
            using var scaledTextFont = new Font("Segoe UI", 9f * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
            foreach (var (text, minimum) in new[] { ("●  Ses", 64), ("■  00:00", 64), ("Kalıcı  ▾", 82), ("1 gün  ▾", 82) })
            {
                var width = MainForm.ToolbarTextWidth(stripGraphics, text, scaledTextFont, minimum, dpi, 2);
                var ink = TextRenderer.MeasureText(stripGraphics, text, scaledTextFont, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                Require(width - 2 >= ink && width >= Scale(minimum), $"{text}: measured scaled label fits without ellipsis");
                using var labelButton = Theme.Button(text, ButtonKind.Ghost);
                labelButton.Font = scaledTextFont;
                labelButton.Size = new Size(width - 2, Scale(42));
                host.Controls.Add(labelButton);
                using var actual = Render(labelButton);
                labelButton.Text = "";
                using var untruncated = Render(labelButton);
                using (var graphics = Graphics.FromImage(untruncated))
                    TextRenderer.DrawText(graphics, text, scaledTextFont, labelButton.ClientRectangle, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                Require(Equal(actual, untruncated), $"{text}: production EndEllipsis paint equals complete-label paint");
                host.Controls.Remove(labelButton);
            }
            results.Add($"Voice/expiry text columns at {dpi * 100 / 96}% scaled-pixel font reserve full padded GDI label width.");
        }
        results.Add("100/125/150/200% pixel geometry was exercised without changing Windows display DPI; native cross-monitor behaviour remains a separate manual check.");
        return results;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Portable toolbar icon QA: " + description);
    }

    private static Bitmap Render(ModernButton button)
    {
        var bitmap = new Bitmap(button.Width, button.Height);
        Paint(button, bitmap);
        return bitmap;
    }

    private static void Paint(ModernButton button, Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        using var args = new PaintEventArgs(graphics, button.ClientRectangle);
        Invoke(button, "OnPaint", args);
    }

    private static void Invoke(ModernButton button, string method, object args) =>
        typeof(ModernButton).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [args]);

    private static bool Equal(Bitmap first, Bitmap second) => DifferenceBounds(first, second).IsEmpty;

    private static Rectangle DifferenceBounds(Bitmap first, Bitmap second)
    {
        var left = first.Width; var top = first.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
        {
            if (first.GetPixel(x, y) == second.GetPixel(x, y)) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private sealed class FixtureSurface : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs args) => args.Graphics.Clear(Theme.Surface);
    }
}
