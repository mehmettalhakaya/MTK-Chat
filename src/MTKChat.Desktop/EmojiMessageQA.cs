using MTKChat.Contracts;

namespace MTKChat.Desktop;

// These windows, users and messages are synthetic. No clipboard, account, HTTP,
// audio device or server data is read or changed by emoji presentation QA.
internal static class EmojiMessageQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Emoji message QA: " + message);
            checks.Add(message);
        }
        var registeredBefore = EmojiAnimationScheduler.RegisteredTargetCount;
        var sender = new ChatUser(Guid.NewGuid(), "Emoji QA", "emoji@invalid.test", false, null);
        var room = Guid.NewGuid();
        StoredMessage Message(string kind = "text", bool deleted = false) => new(Guid.NewGuid(), Guid.NewGuid(),
            room, sender.Id, kind, DateTimeOffset.Now, null, deleted, [], null,
            new MessageDelivery("read", [new RecipientReceipt(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now)]));
        using (var host = new Form
        {
            ClientSize = new Size(820, 720), StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false, Text = "MTK Chat · Emoji message QA", BackColor = Theme.Canvas
        })
        {
            var list = new ModernMessageList
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(14), BackColor = Color.Transparent
            };
            var viewport = new ModernMessageViewport(list) { Dock = DockStyle.Fill };
            host.Controls.Add(viewport);
            var samples = new[] { "😊", "😂 ❤️", "😉 👋 🎉", "Merhaba 😊", "👩‍💻" };
            var rows = samples.Select((text, index) =>
                new MessageRow(sender, index == 1, Message(), text, null) { Width = 740 }).ToArray();
            foreach (var row in rows) list.Controls.Add(row);
            rows[1].SetStarred(true);
            host.Show(); host.Activate(); Application.DoEvents();
            Require(rows.Take(3).Select((row, index) => Descendants(row).OfType<AnimatedEmojiView>().Single().EmojiCount == index + 1).All(value => value),
                "One, two and three supported standalone emoji use one vector surface each");
            Require(rows.All(row => row.StarredPreview == samples[Array.IndexOf(rows, row)]),
                "Local emoji presentation preserves exact Unicode and whitespace for copy, pins and stars");
            Require(rows.Skip(3).All(row => !Descendants(row).OfType<AnimatedEmojiView>().Any()),
                "Mixed text and unsupported joined sequences remain ordinary unmodified message text");
            foreach (var row in rows)
            {
                var menu = Theme.ContextMenu();
                menu.Items.Add("QA message action"); row.SetMessageMenu(menu);
                Require(Descendants(row).OfType<AnimatedEmojiView>().All(view => ReferenceEquals(view.ContextMenuStrip, menu)),
                    "An emoji surface inherits the original message context menu without intercepting its actions");
                foreach (var width in new[] { 240, 360, 740 }) { row.Width = width; row.VerifyLayout(); }
                row.Width = 740; row.VerifyLayout();
            }
            Require(rows.All(row => row.Height > 0), "Emoji, normal text, star, timestamp and receipt geometry fits narrow and wide rows");
            var first = rows[0]; var firstView = Descendants(first).OfType<AnimatedEmojiView>().Single();
            var bubble = first.Controls.OfType<RoundedPanel>().Single();
            var content = new Rectangle(first.Left + bubble.Left + firstView.Left,
                first.Top + bubble.Top + firstView.Top, firstView.Width, firstView.Height);
            Require(first.IsContentVisible(content), "Read visibility uses the displayed emoji content rather than its hidden Unicode text control");
            Require(!first.IsContentVisible(new Rectangle(content.Left, content.Bottom - 5, content.Width, 5)),
                "A five-pixel emoji sliver does not mark an incoming message read");
            var sentView = Descendants(rows[1]).OfType<AnimatedEmojiView>().Single();
            EmojiAnimationScheduler.TickForQa(motionAllowed: true, ownerActive: true);
            Require(!rows[1].CanMarkRead && EmojiAnimationScheduler.IsActiveForQa(sentView),
                "A sent emoji can animate independently of incoming read-receipt eligibility");
            var firstBounds = first.Bounds; var contentBounds = firstView.Bounds; var layouts = 0;
            first.Layout += (_, _) => layouts++;
            var beforeFrames = firstView.AnimationInvalidationsForQa;
            for (var frame = 0; frame < 100; frame++) EmojiAnimationScheduler.TickForQa(true, true);
            Require(firstView.AnimationInvalidationsForQa == beforeFrames + 100 && layouts == 0 &&
                first.Bounds == firstBounds && firstView.Bounds == contentBounds,
                "One hundred animation frames invalidate only the emoji surface without message reflow or geometry changes");
            using (var bitmap = new Bitmap(host.Width, host.Height))
            { host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "emoji-messages-modern.png")); }
            foreach (var fallback in new[] { "😊😊😊😊", "😊 metin", "👍🏽", "👨‍👩‍👧‍👦", "" })
            {
                using var row = new MessageRow(sender, false, Message(), fallback, null);
                Require(!Descendants(row).OfType<AnimatedEmojiView>().Any() && row.StarredPreview == fallback,
                    "Unsupported, mixed, four-emoji or empty content stays exact original text: " + (fallback.Length == 0 ? "empty" : fallback));
            }
            using (var deleted = new MessageRow(sender, false, Message(deleted: true), "😊", null))
                Require(!Descendants(deleted).OfType<AnimatedEmojiView>().Any() && !deleted.CanMarkRead,
                    "Deleted content never creates an animated emoji presentation or a read acknowledgement");
            using (var file = new MessageRow(sender, false, Message("file"), "😊", null))
                Require(!Descendants(file).OfType<AnimatedEmojiView>().Any(), "A file label that contains only emoji is not mistaken for an emoji text message");
            host.Close();
        }
        Require(EmojiAnimationScheduler.RegisteredTargetCount == registeredBefore,
            "Disposing emoji message rows immediately unregisters every animation target");
        VerifyScheduler(Require);
        VerifyProductionFrameCost(Require);
        return checks;
    }

    private static void VerifyProductionFrameCost(Action<bool, string> require)
    {
        var registeredBefore = EmojiAnimationScheduler.RegisteredTargetCount;
        using var host = new Form
        {
            ClientSize = new Size(420, 280), StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false, Text = "MTK Chat · Emoji frame cost QA"
        };
        var clip = new Panel { Bounds = new Rectangle(20, 20, 200, 140) };
        var visible = new Probe { Bounds = new Rectangle(20, 25, 50, 50) };
        clip.Controls.Add(visible); host.Controls.Add(clip);
        var hidden = Enumerable.Range(0, 200).Select(_ => new Probe
        { Bounds = new Rectangle(240, 25, 50, 50), Visible = false }).ToArray();
        foreach (var target in hidden)
        {
            clip.Controls.Add(target);
            // Hidden controls need not allocate HWNDs; explicit subscription models
            // history targets retained after being scrolled/hidden by a real viewport.
            EmojiAnimationScheduler.Subscribe(target);
        }
        host.Show(); host.Activate(); Application.DoEvents();
        EmojiAnimationScheduler.RefreshVisibilityForQa(true, true);
        require(EmojiAnimationScheduler.RegisteredTargetCount == registeredBefore + hidden.Length + 1 &&
            EmojiAnimationScheduler.IsActiveForQa(visible) && hidden.All(target => !EmojiAnimationScheduler.IsActiveForQa(target)),
            "Frame-cost fixture retains 200 hidden emoji targets and one eligible visible target");
        var active = EmojiAnimationScheduler.ActiveTargetCount;
        var refreshes = EmojiAnimationScheduler.FullRefreshesForQa;
        var eligibility = EmojiAnimationScheduler.EligibilityChecksForQa;
        // No policy overrides: this invokes exactly the normal production timer
        // callback. A desktop with reduced motion may remove the one active target
        // on its first frame; either way the hidden history must never be scanned.
        for (var frame = 0; frame < 100; frame++) EmojiAnimationScheduler.TickForQa();
        require(EmojiAnimationScheduler.FullRefreshesForQa == refreshes &&
            EmojiAnimationScheduler.EligibilityChecksForQa - eligibility <= active * 100,
            "One hundred production animation frames perform no full history refresh and at most one eligibility check per previously active target");
        require(hidden.All(target => target.Frames == 0), "Production frames never invalidate retained hidden emoji history targets");
        var dispatches = EmojiAnimationScheduler.RefreshDispatchesForQa;
        hidden[0].Visible = true; hidden[0].Location = new Point(90, 25); Application.DoEvents();
        require(EmojiAnimationScheduler.RefreshDispatchesForQa > dispatches,
            "Exposing a previously hidden target queues a full visibility refresh even when production frames have stopped");
        EmojiAnimationScheduler.TickForQa(true, true);
        require(EmojiAnimationScheduler.IsActiveForQa(hidden[0]) && hidden[0].Frames > 0,
            "A newly exposed target safely joins the active set after event-coalesced refresh");
        host.Close();
        require(EmojiAnimationScheduler.RegisteredTargetCount == registeredBefore,
            "Disposing a long hidden emoji history releases its entire scheduler registration set");
    }

    private static void VerifyScheduler(Action<bool, string> require)
    {
        var before = EmojiAnimationScheduler.RegisteredTargetCount;
        using (var host = new Form
        {
            ClientSize = new Size(420, 280), StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false, Text = "MTK Chat · Emoji scheduler QA"
        })
        {
            var clip = new Panel { Bounds = new Rectangle(20, 20, 200, 140) };
            var target = new Probe { Bounds = new Rectangle(20, 25, 50, 50) };
            clip.Controls.Add(target); host.Controls.Add(clip);
            host.Show(); host.Activate(); Application.DoEvents();
            EmojiAnimationScheduler.Subscribe(target); EmojiAnimationScheduler.Subscribe(target);
            require(EmojiAnimationScheduler.RegisteredTargetCount == before + 1,
                "Repeated subscription registers one target, not duplicate timer/event owners");
            var frames = target.Frames;
            EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames + 1 && EmojiAnimationScheduler.IsActiveForQa(target) && EmojiAnimationScheduler.TimerRunning,
                "Visible active emoji starts the single shared UI timer");
            frames = target.Frames; target.Location = new Point(240, 25);
            EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames && !EmojiAnimationScheduler.IsActiveForQa(target),
                "A control outside an ancestor clip receives no animation invalidation");
            target.Location = new Point(20, -60); EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames, "Native negative scroll coordinates clip an offscreen emoji without adding scroll offset twice");
            target.Location = new Point(20, -20); EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == ++frames, "A partially visible emoji resumes when scrolling exposes its actual content");
            clip.Hide(); EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames && !EmojiAnimationScheduler.IsActiveForQa(target), "Hiding an ancestor stops emoji frames");
            clip.Show(); target.Location = new Point(20, 25); EmojiAnimationScheduler.TickForQa(false, true);
            require(target.Frames == frames && !EmojiAnimationScheduler.IsActiveForQa(target), "Windows reduced-motion policy uses a static emoji frame");
            EmojiAnimationScheduler.TickForQa(true, false);
            require(target.Frames == frames && !EmojiAnimationScheduler.IsActiveForQa(target), "An inactive owning window receives no emoji frames");
            host.WindowState = FormWindowState.Minimized; EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames && !EmojiAnimationScheduler.IsActiveForQa(target), "A minimized owning window receives no emoji frames");
            host.WindowState = FormWindowState.Normal; target.Animate = false;
            EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames && !EmojiAnimationScheduler.IsActiveForQa(target), "A static or unselected picker target does not keep animation active");
            target.Animate = true; EmojiAnimationScheduler.TickForQa(true, true);
            require(target.Frames == frames + 1, "Restoring the window and its animated selection safely resumes frames");
            var survivor = new Probe { Bounds = new Rectangle(240, 25, 50, 50) };
            clip.Controls.Add(survivor); Application.DoEvents();
            EmojiAnimationScheduler.TickForQa(false, true);
            var dispatches = EmojiAnimationScheduler.RefreshDispatchesForQa;
            target.Location = new Point(22, 25);
            require(EmojiAnimationScheduler.RefreshPendingForQa,
                "A visibility change queues one coalesced refresh while the timer is stopped");
            target.Dispose();
            Application.DoEvents();
            require(!EmojiAnimationScheduler.RefreshPendingForQa && EmojiAnimationScheduler.RefreshDispatchesForQa > dispatches &&
                EmojiAnimationScheduler.RegisteredTargetCount == before + 1,
                "Disposing the first target before dispatch cannot strand a refresh posted for the surviving target");
            dispatches = EmojiAnimationScheduler.RefreshDispatchesForQa;
            survivor.Location = new Point(20, 25); Application.DoEvents();
            require(!EmojiAnimationScheduler.RefreshPendingForQa && EmojiAnimationScheduler.RefreshDispatchesForQa > dispatches,
                "A surviving previously clipped target receives later visibility refreshes after another row HWND is destroyed");
            var survivorFrames = survivor.Frames;
            EmojiAnimationScheduler.TickForQa(true, true);
            require(survivor.Frames == survivorFrames + 1 && EmojiAnimationScheduler.IsActiveForQa(survivor),
                "The surviving target resumes animation after the queued-dispatch destruction race");
            survivor.Dispose();
            require(EmojiAnimationScheduler.RegisteredTargetCount == before, "A disposed target unregisters and releases shared ancestor observations immediately");
            if (before == 0) require(!EmojiAnimationScheduler.TimerRunning && EmojiAnimationScheduler.ActiveTargetCount == 0,
                "The last target disposes the timer instead of leaving an idle polling loop");
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }

    private sealed class Probe : Control, IEmojiAnimationTarget
    {
        internal int Frames;
        internal bool Animate = true;
        Control IEmojiAnimationTarget.AnimationControl => this;
        bool IEmojiAnimationTarget.WantsAnimation => Animate;
        void IEmojiAnimationTarget.InvalidateAnimation() { Frames++; Invalidate(); }
        protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); EmojiAnimationScheduler.Subscribe(this); }
        protected override void OnHandleDestroyed(EventArgs args) { EmojiAnimationScheduler.Unsubscribe(this); base.OnHandleDestroyed(args); }
        protected override void Dispose(bool disposing) { if (disposing) EmojiAnimationScheduler.Unsubscribe(this); base.Dispose(disposing); }
    }
}
