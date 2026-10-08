using MTKChat.Contracts;
using MTKChat.Cryptography;
using System.Diagnostics;

namespace MTKChat.Desktop;

// All audio/session events below are synthetic. Never opens a capture device,
// contacts a production server or uses another person's account.
internal static class CallUIQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<string>();
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Call UI QA: " + message); checks.Add(message); }
        using var api = new ChatApiClient("https://call-ui-qa.invalid/chat/");
        using var device = DeviceIdentity.Create(); var clock = new Clock();
        var me = new ChatUser(Guid.NewGuid(), "QA Kullanıcısı", "qa@invalid.test", false, null);
        var peer = me with { Id = Guid.NewGuid(), DisplayName = "Arayan Kişi" };
        var call = new CallView(Guid.NewGuid(), Guid.NewGuid(), "QA Araması", peer.Id, clock.GetUtcNow(), [me, peer], []);
        var session = new FakeSession(call);
        using (var form = new CallForm(api, device, me.Id, call, true, snapshot: true, sessionFactory: () => session, clock: clock))
        {
            form.Show(); Application.DoEvents();
            Require(session.Starts == 0, "Incoming invitation does not start capture/session before acceptance");
            var incoming = Commands(form); Require(incoming.Length == 2 && incoming.Any(b => b.Text == "Kabul et") && incoming.Any(b => b.Text == "Reddet" && b.Kind == ButtonKind.Danger), "Incoming call has two clear accept/reject commands");
            VerifyGeometry(form, Require); Capture(form, directory, "call-modern-incoming.png");
            Wait(form.AcceptAsync()); Application.DoEvents();
            Require(session.Starts == 1 && Commands(form).Length == 3, "Accept starts once and switches to speaker/microphone/end controls");
            Require(Text(form, "CallDuration") == "", "Joining alone never pretends an encrypted peer is connected");
            Wait(form.AcceptAsync()); Require(session.Starts == 1, "Duplicate accept does not start a second session");
            session.CodesValue[peer.Id] = "A123 B456 C789 D012 E345 F678";
            call = call with { Peers = [new CallPeer(me, new CallJoin(Guid.NewGuid(), "QA", "QA"), false),
                new CallPeer(peer, new CallJoin(Guid.NewGuid(), "QA", "QA"), false)] };
            session.Roster(call); Application.DoEvents();
            session.Transition(CallConnectionState.Connected); Application.DoEvents();
            clock.Advance(TimeSpan.FromSeconds(35)); form.RefreshForQa();
            Require(Text(form, "CallDuration") == "00:35", "Connected duration uses monotonic clock");
            var cards = form.ParticipantCardsForQa.ToArray();
            for (var i = 0; i < 100; i++) session.Roster(call);
            Application.DoEvents(); Require(form.ParticipantCardsForQa.SequenceEqual(cards), "100 roster refreshes reuse participant cards instead of allocating/repainting new controls");
            Commands(form).Single(b => b.VectorIcon == ModernButtonIcon.Speaker).PerformClick();
            Require(session.SpeakerMuted && Commands(form).Any(b => b.VectorIcon == ModernButtonIcon.SpeakerOff), "Speaker output has immediate local mute and a matching icon");
            Wait(form.ToggleMuteAsync()); Require(session.Muted && Commands(form).Any(b => b.VectorIcon == ModernButtonIcon.MicrophoneOff), "Microphone mute is reflected in the active controls");
            session.FailUnmute = true; Wait(form.ToggleMuteAsync());
            Require(session.Muted && session.Disposals == 0 && Commands(form).Length == 3, "Failed unmute retains local silence and does not tear down an otherwise healthy call");
            session.Transition(CallConnectionState.Reconnecting); Application.DoEvents();
            Require(Text(form, "CallStatus").Contains("Yeniden bağlanıyor") && Text(form, "CallDuration") == "00:35", "Reconnecting is visible without resetting elapsed time");
            session.Transition(CallConnectionState.Connected); Application.DoEvents();
            clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2)); form.RefreshForQa();
            Require(Text(form, "CallDuration") == "1:02:35", "Hour-long calls do not wrap to a minute-only timer");
            Descendants(form).OfType<ModernButton>().Single(b => b.AccessibleName == "Arama güvenliğini doğrula").PerformClick(); Application.DoEvents();
            Require(Text(form, "CallSecurityDetail").Contains(session.CodesValue[peer.Id]), "Verification code appears inside the themed call window, not a message box");
            VerifyGeometry(form, Require); Capture(form, directory, "call-modern-connected.png");
            foreach (var factor in new[] { .9f, 1f, 1.25f })
            {
                form.Size = new Size((int)(580 * factor), (int)(740 * factor)); Application.DoEvents(); VerifyGeometry(form, Require);
            }
            session.Fail("QA bağlantısı sonlandı"); Application.DoEvents();
            Require(session.Disposals == 1 && Commands(form).Single().Text == "Kapat" && Text(form, "CallStatus") == "QA bağlantısı sonlandı", "Terminal failure retains its actual reason, disposes audio once and leaves one close action");
            var finalDuration = Text(form, "CallDuration"); clock.Advance(TimeSpan.FromMinutes(2)); form.RefreshForQa();
            Require(Text(form, "CallDuration") == finalDuration, "Final call duration stays frozen after the session ends");
            session.Transition(CallConnectionState.Connected); Application.DoEvents();
            Require(Commands(form).Length == 1, "Late session events cannot resurrect an ended call");
            form.Close(); Require(session.Disposals == 1, "Closing an ended window never double-disposes its session");
        }
        var delayed = new FakeSession(call) { PendingStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var late = new CallForm(api, device, me.Id, call, false, snapshot: true, sessionFactory: () => delayed);
        late.Show(); Application.DoEvents(); var starting = late.AcceptAsync(); late.Close(); delayed.PendingStart.SetResult(); Wait(starting); Application.DoEvents();
        Require(delayed.Disposals == 1 && late.IsDisposed, "Close during pending start disposes immediately and ignores completion"); late.Dispose();
        var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var factories = 0;
        var before = new CallForm(api, device, me.Id, call, false, snapshot: true, prepare: () => prepare.Task, sessionFactory: () => { factories++; return new FakeSession(call); });
        before.Show(); Application.DoEvents(); var pending = before.AcceptAsync(); before.Close(); prepare.SetResult(); Wait(pending);
        Require(factories == 0, "Close while preparing audio never creates a session or microphone"); before.Dispose();
        var brokenOutput = new FakeSession(call) { FailSpeaker = true };
        using (var failing = new CallForm(api, device, me.Id, call, false, snapshot: true, sessionFactory: () => brokenOutput))
        {
            failing.Show(); Application.DoEvents(); Wait(failing.AcceptAsync());
            Commands(failing).Single(b => b.VectorIcon == ModernButtonIcon.Speaker).PerformClick(); Application.DoEvents();
            Require(brokenOutput.Disposals == 1 && Text(failing, "CallStatus").Contains("Ses çıkışına erişilemedi"),
                "Disconnected output device is contained inside the call UI instead of becoming an unhandled exception");
            failing.Close();
        }
        var members = Enumerable.Range(0, 12).Select(i => me with { Id = Guid.NewGuid(), DisplayName = "Katılımcı " + i }).ToArray();
        using (var picker = new CallInviteForm(members))
        {
            picker.Show(); Application.DoEvents();
            foreach (var member in members.Take(6)) picker.SelectPerson(member.Id, true);
            Require(picker.Selected.Length == 5, "Modern group-call selector enforces five invitees without message-box errors");
            picker.SelectPerson(members[0].Id, false); picker.SelectPerson(members[6].Id, true);
            Require(picker.Selected.Length == 5 && picker.Selected.Contains(members[6].Id), "Selection is stable by user ID and can be changed at the limit");
            Require(!Descendants(picker).OfType<CheckedListBox>().Any(), "Call invite picker uses themed DevExpress selection rows instead of a native checkbox list");
            Capture(picker, directory, "call-modern-picker.png"); picker.Close();
        }
        checks.Add("Limit: fake audio/session UI tests only; real microphones, two physical PCs, internet latency and echo cancellation are not verified here.");
        return checks;
    }
    internal static ModernButton[] Commands(Form form) => Descendants(form).OfType<ModernButton>()
        .Where(b => b.Visible && b.CircularSurface && b.VectorIcon is ModernButtonIcon.Phone or ModernButtonIcon.HangUp or ModernButtonIcon.Microphone or ModernButtonIcon.MicrophoneOff or ModernButtonIcon.Speaker or ModernButtonIcon.SpeakerOff or ModernButtonIcon.Close).ToArray();
    private static void VerifyGeometry(Form form, Action<bool, string> require)
    {
        foreach (var card in Descendants(form).OfType<CallPersonCard>())
        {
            var bounds = card.RectangleToScreen(card.ClientRectangle);
            foreach (var control in Descendants(card).Where(c => c.Visible && (c is AvatarView || c is Label)))
                require(bounds.Contains(control.RectangleToScreen(control.ClientRectangle)), $"Participant card keeps {control.GetType().Name} fully inside its rounded surface");
        }
        foreach (var command in Commands(form))
        {
            var screen = command.RectangleToScreen(command.ClientRectangle);
            require(form.RectangleToScreen(form.ClientRectangle).Contains(screen), $"{form.Size}: {command.AccessibleName} stays fully within call window");
            require(command.Width >= 42 && command.Height >= 42 && command.VectorIcon != ModernButtonIcon.None, $"{form.Size}: {command.AccessibleName} has a reachable vector target");
            var caption = command.Parent!.Controls["Caption"]!;
            var text = TextRenderer.MeasureText(caption.Text, caption.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            require(caption.Width >= text.Width && caption.Height >= text.Height, $"{form.Size}: {caption.Text} caption fits below its icon");
        }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static string Text(Control form, string name) => Descendants(form).Single(c => c.Name == name).Text;
    private static void Capture(Form form, string directory, string name)
    { using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(directory, name)); }
    private static void Wait(Task task)
    { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); } if (!task.IsCompleted) throw new TimeoutException("Call UI QA task"); task.GetAwaiter().GetResult(); }
    private sealed class Clock : TimeProvider
    {
        private long _ticks = TimeSpan.FromDays(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(_ticks);
        internal void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
    private sealed class FakeSession(CallView call) : ICallSession
    {
        public event Action<CallView>? Changed; public event Action<string>? Failed; public event Action<CallConnectionState>? ConnectionStateChanged;
        public bool Muted { get; private set; } public bool SpeakerMuted { get; private set; }
        public CallConnectionState State { get; private set; } = CallConnectionState.Connecting;
        internal readonly Dictionary<Guid, string> CodesValue = [];
        public IReadOnlyDictionary<Guid, string> Codes => CodesValue;
        internal int Starts, Disposals; internal bool FailUnmute, FailSpeaker;
        internal TaskCompletionSource? PendingStart;
        public async Task StartAsync() { Starts++; Changed?.Invoke(call); if (PendingStart is not null) await PendingStart.Task; }
        public Task ToggleMuteAsync() { if (Muted && FailUnmute) throw new HttpRequestException("QA mikrofon durumu güncellenemedi"); Muted = !Muted; return Task.CompletedTask; }
        public void SetSpeakerMuted(bool muted) { if (FailSpeaker) throw new InvalidOperationException("QA output gone"); SpeakerMuted = muted; }
        internal void Transition(CallConnectionState state) { State = state; ConnectionStateChanged?.Invoke(state); }
        internal void Roster(CallView view) => Changed?.Invoke(view);
        internal void Fail(string text) { Failed?.Invoke(text); Transition(CallConnectionState.Ended); }
        public void Dispose() => Disposals++;
    }
}
