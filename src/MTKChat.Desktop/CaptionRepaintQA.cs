using System.Reflection;

namespace MTKChat.Desktop;

// Regression harness for this application's own private caption controls only.
// It never opens the login flow, touches the user's existing window, injects input,
// calls an API or sends native desktop mouse/keyboard events. Its own no-activate,
// zero-opacity window is shown so Windows processes real maximize/restore states.
internal static class CaptionRepaintQA
{
    internal static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        using var form = new CaptionTestForm
        {
            Text = "Caption QA",
            ShowInTaskbar = false,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            ClientSize = new Size(760, 360)
        };
        // A never-shown Form does not have the same native WindowState/geometry as
        // a live form (documented by WinForms). A no-activate, zero-opacity fixture
        // exercises real lifecycle/state messages without displaying user content.
        form.Show();
        Application.DoEvents();
        var close = GetButton(form, "_close");
        var maximize = GetButton(form, "_maximize");
        var minimize = GetButton(form, "_minimize");

        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Caption repaint QA: " + message);
            checks.Add(message);
        }
        void ProbeLayout(string label)
        {
            IReadOnlyList<string> productionChecks;
            try { productionChecks = form.VerifyCaptionLayout(); }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException($"Caption QA {label}: WindowState={form.WindowState}, RestoreGlyph={GetRestore(maximize)}, " +
                    $"Visible={form.Visible}, Opacity={form.Opacity}, RegionNull={form.Region is null}, ClientSize={form.ClientSize}.", exception);
            }
            if (productionChecks.Count == 0) throw new InvalidOperationException("Caption layout probe returned no checks.");
            checks.Add($"{label}: production caption geometry passed {productionChecks.Count} checks");
        }

        Require(form.Visible && form.Opacity == 0 && !form.ShowInTaskbar,
            "QA owns only a no-activate zero-opacity form outside the taskbar");
        foreach (var canMaximize in new[] { false, true })
        foreach (var canMinimize in new[] { false, true })
        {
            form.MaximizeBox = canMaximize;
            form.MinimizeBox = canMinimize;
            form.PerformLayout();
            // Read bounds before VerifyCaptionLayout refreshes anything: this validates
            // that real capability setters/style changes trigger the production layout.
            Require(canMaximize ? maximize.Width > 0 && maximize.Height > 0 : maximize.Bounds.IsEmpty,
                $"actual capability {canMaximize}/{canMinimize}: maximize bounds update immediately");
            Require(canMinimize ? minimize.Width > 0 && minimize.Height > 0 : minimize.Bounds.IsEmpty,
                $"actual capability {canMaximize}/{canMinimize}: minimize bounds update immediately");
            var live = new[] { close, maximize, minimize }.Where(button => !button.Bounds.IsEmpty).ToArray();
            Require(live.SelectMany((button, index) => live.Skip(index + 1).Select(other => !button.Bounds.IntersectsWith(other.Bounds))).All(value => value),
                $"actual capability {canMaximize}/{canMinimize}: remaining controls do not overlap");
            ProbeLayout($"actual capability {canMaximize}/{canMinimize}");
        }

        form.MaximizeBox = form.MinimizeBox = true;
        foreach (var state in new[] { FormWindowState.Maximized, FormWindowState.Normal })
        {
            form.WindowState = state;
            Application.DoEvents();
            Require(form.WindowState == state, $"actual window state changes to {state}");
            ProbeLayout($"actual {state}");
            Require(GetRestore(maximize) == (state == FormWindowState.Maximized), $"{state}: the vector selects the correct maximize/restore shape");
            Require(form.Visible && form.Opacity == 0 && !form.ShowInTaskbar,
                $"{state}: native state QA remains zero-opacity and outside the taskbar");
        }

        foreach (var (button, name) in new[] { (close, "close"), (maximize, "maximize"), (minimize, "minimize") })
        {
            using var reused = NewBitmap(button);
            SetPrivateFlag(button, "_hovered", true);
            SetPrivateFlag(button, "_pressed", false);
            Paint(button, reused);
            Require(!ContainsMarker(reused), $"{name}: hover paint covers its entire opaque client surface");

            SetPrivateFlag(button, "_hovered", false);
            Paint(button, reused); // Deliberately keep the hover/red pixels underneath.
            using var freshIdle = NewBitmap(button);
            Paint(button, freshIdle);
            Require(EqualPixels(reused, freshIdle), $"{name}: hover-to-idle reused pixels equal a fresh idle repaint");
            Require(!ContainsRedHover(reused), $"{name}: idle caption contains no stale close-red hover pixels");
        }

        using (var reused = NewBitmap(maximize))
        {
            SetRestore(maximize, true);
            Paint(maximize, reused);
            SetRestore(maximize, false);
            Paint(maximize, reused); // Deliberately retain the old restore outline.
            using var freshMaximize = NewBitmap(maximize);
            Paint(maximize, freshMaximize);
            Require(EqualPixels(reused, freshMaximize), "restore-to-maximize reused pixels equal a fresh maximize repaint");
        }
        form.Close();
        Require(form.IsDisposed && !form.Visible, "QA closes/disposes its own form without opening a modal or touching another window");
        return checks;
    }

    private sealed class CaptionTestForm : ModernForm
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080; // NOACTIVATE | TOOLWINDOW; QA fixture only.
                return parameters;
            }
        }
    }

    private static Button GetButton(ModernForm form, string field) =>
        typeof(ModernForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as Button
        ?? throw new InvalidOperationException($"Missing app-owned caption field {field}.");

    private static void SetPrivateFlag(Button button, string field, bool value)
    {
        var info = button.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing caption state field {field}.");
        info.SetValue(button, value);
    }

    private static PropertyInfo RestoreProperty(Button button) =>
        button.GetType().GetProperty("RestoreGlyph", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing app-owned RestoreGlyph property.");

    private static bool GetRestore(Button button) => RestoreProperty(button).GetValue(button) is true;
    private static void SetRestore(Button button, bool value) => RestoreProperty(button).SetValue(button, value);

    private static Bitmap NewBitmap(Control control)
    {
        if (control.Width < 2 || control.Height < 2) throw new InvalidOperationException("Caption control has no drawable bounds.");
        var bitmap = new Bitmap(control.Width, control.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Fuchsia);
        return bitmap;
    }

    private static void Paint(Button button, Bitmap target)
    {
        var method = button.GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing app-owned caption paint method.");
        using var graphics = Graphics.FromImage(target);
        using var args = new PaintEventArgs(graphics, button.ClientRectangle);
        // Protected paint is called on this private QA button, never on a live user window.
        method.Invoke(button, [args]);
    }

    private static bool EqualPixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
            if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }

    private static bool ContainsMarker(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).ToArgb() == Color.Fuchsia.ToArgb()) return true;
        return false;
    }

    private static bool ContainsRedHover(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.R >= 180 && pixel.G < 150 && pixel.B < 160) return true;
        }
        return false;
    }
}
