using System.Diagnostics;

namespace MTKChat.Desktop;

internal static class ViewportPerformanceQA
{
    internal static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        void Require(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Viewport performance QA: " + name);
            checks.Add(name);
        }
        using var form = new Form { Size = new Size(430, 640), ShowInTaskbar = false };
        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(8), BackColor = Theme.Sidebar
        };
        using var viewport = new ModernConversationViewport(list);
        form.Controls.Add(viewport);
        list.SuspendLayout();
        for (var index = 0; index < 180; index++)
            list.Controls.Add(new Label { Text = "Sentetik sohbet " + index, Size = new Size(340, 42), Margin = new Padding(0, 0, 0, 4) });
        list.ResumeLayout(true);
        form.Show(); Pump();
        viewport.PerformLayout(); Pump();
        Require(viewport.MaximumOffset > 0 && viewport.ThumbMatchesForQa, "180 cards have synchronized overflow metrics");
        var arrangements = viewport.ArrangementPassesForQa;
        var scans = viewport.SynchronizationPassesForQa;
        var timer = Stopwatch.StartNew();
        for (var frame = 0; frame < 160; frame++)
        {
            list.Invalidate(); list.Update(); Application.DoEvents();
        }
        Pump(); timer.Stop();
        Require(viewport.ArrangementPassesForQa == arrangements && viewport.SynchronizationPassesForQa == scans,
            "160 native paint frames do not reflow or scan the card list");
        Console.WriteLine($"Viewport performance measurement: 160 paints / 180 cards, {timer.Elapsed.TotalMilliseconds:F1} ms; additional arrangements=0, scans=0.");
        list.AutoScrollPosition = new Point(0, viewport.MaximumOffset / 2); Pump();
        Require(viewport.Offset > 0 && viewport.ThumbMatchesForQa, "External native scrolling updates the thumb without a full scan");
        form.Height = 520; Pump();
        Require(viewport.ArrangementPassesForQa > arrangements && viewport.ThumbMatchesForQa,
            "Real window resize still updates geometry and scroll metrics");
        var beforeRemoval = viewport.MaximumOffset;
        foreach (var child in list.Controls.Cast<Control>().Skip(20).ToArray()) child.Visible = false;
        Pump();
        Require(viewport.MaximumOffset < beforeRemoval && viewport.ThumbMatchesForQa,
            "Real card visibility changes still reduce the scroll extent");
        list.Invalidate(); viewport.Dispose(); Pump();
        Require(viewport.IsDisposed, "A queued repaint survives viewport disposal without invoking stale controls");
        form.Hide();
        return checks;
    }

    private static void Pump()
    {
        // Drain posted callbacks with a finite budget. This is not a time-based
        // performance assertion and never controls a user-owned application.
        for (var turn = 0; turn < 12; turn++) Application.DoEvents();
    }
}
