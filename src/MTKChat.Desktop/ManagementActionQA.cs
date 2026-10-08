using System.Reflection;

namespace MTKChat.Desktop;

// Assert the visible, painted footprint of the real action controls, not merely
// positive self-reported Bounds. A child can have valid Bounds while its parent
// clips its lower edge; a Ghost can have valid geometry but no resting surface.
internal static class ManagementActionQA
{
    internal static void Verify(IEnumerable<ModernButton> source, Action<bool, string> require, string name)
    {
        var buttons = source.ToArray();
        require(buttons.Length > 0 && buttons.All(button => button.Kind is ButtonKind.Primary or ButtonKind.Secondary),
            name + ": every command uses a filled/outlined button, never an invisible idle Ghost");
        require(buttons.All(FitsAncestors), name + ": each entire button footprint fits every ancestor client rectangle without clipping");
        var captionFailures = buttons.Where(button => !CaptionFits(button)).Select(button =>
            $"'{button.Text}' bounds={button.Width}x{button.Height}, dpi={button.DeviceDpi}, " +
            $"font={button.Font.SizeInPoints:0.##}pt/{button.Font.Height}px, " +
            $"textWidth={CaptionWidth(button)}, availableWidth={button.Width - button.Padding.Horizontal}, " +
            $"minimumHeight={Math.Ceiling(34f * button.DeviceDpi / 96f)}, textHeight={button.Font.Height + 12 * button.DeviceDpi / 96}").ToArray();
        require(captionFailures.Length == 0,
            name + ": captions fit their button width and readable height including vertical breathing room" +
            (captionFailures.Length == 0 ? "" : " [" + string.Join("; ", captionFailures) + "]"));
        foreach (var state in new[] { "idle", "hover", "pressed", "disabled" })
        {
            require(buttons.All(button => HasPaintedSurface(button, state)),
                name + ": " + state + " state retains visible fill and secondary outline outside caption ink");
        }
    }

    private static int CaptionWidth(ModernButton button) => TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;

    private static bool CaptionFits(ModernButton button) =>
        button.Height >= Math.Ceiling(34f * button.DeviceDpi / 96f) &&
        CaptionWidth(button) <= button.Width - button.Padding.Horizontal &&
        button.Font.Height + 12 * button.DeviceDpi / 96 <= button.Height;

    private static bool FitsAncestors(Control control)
    {
        if (!control.Visible || control.Width <= 0 || control.Height <= 0) return false;
        var footprint = control.RectangleToScreen(control.ClientRectangle);
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(footprint)) return false;
        return true;
    }

    private static bool HasPaintedSurface(ModernButton button, string state)
    {
        var enabled = button.Enabled;
        try
        {
            // Direct protected-event invocation exercises the owner-draw states
            // without clicking the real destructive command or its HTTP handler.
            button.Enabled = true;
            Invoke(button, "OnMouseLeave", EventArgs.Empty);
            if (state is "hover" or "pressed") Invoke(button, "OnMouseEnter", EventArgs.Empty);
            if (state == "pressed") Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0));
            if (state == "disabled") button.Enabled = false;
            using var image = new Bitmap(button.Width, button.Height);
            using var graphics = Graphics.FromImage(image);
            using var paint = new PaintEventArgs(graphics, button.ClientRectangle);
            Invoke(button, "OnPaintBackground", paint); Invoke(button, "OnPaint", paint);
            var corner = image.GetPixel(0, 0);
            var fill = image.GetPixel(button.Width / 2, Math.Min(5, button.Height - 1));
            var border = image.GetPixel(button.Width / 2, 0);
            return fill != corner && (button.Kind != ButtonKind.Secondary || border != fill);
        }
        finally
        {
            // Disabling clears pressed state without MouseUp dispatching a click.
            button.Enabled = false;
            Invoke(button, "OnMouseLeave", EventArgs.Empty);
            button.Enabled = enabled;
        }
    }

    private static void Invoke(ModernButton button, string method, object args) =>
        typeof(ModernButton).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [args]);
}
