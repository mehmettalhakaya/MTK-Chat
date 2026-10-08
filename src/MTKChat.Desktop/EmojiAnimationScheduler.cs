using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MTKChat.Desktop;

// One UI timer serves message emoji and the currently selected picker cell. A target
// owns its control; this scheduler never owns a bitmap, a message or a network task.
internal interface IEmojiAnimationTarget
{
    Control AnimationControl { get; }
    bool WantsAnimation { get; }
    void InvalidateAnimation();
    Form? AnimationOwner => AnimationControl.FindForm();
}

internal static class EmojiAnimationScheduler
{
    [ThreadStatic] private static SchedulerState? _state;
    [ThreadStatic] private static bool? _motionEnabled;
    private static readonly long StartedAt = Stopwatch.GetTimestamp();

    internal static double Seconds => Stopwatch.GetElapsedTime(StartedAt).TotalSeconds;
    internal static bool MotionEnabled => _motionEnabled ??= ReadMotionSetting();
    internal static int RegisteredTargetCount => _state?.Entries.Count ?? 0;
    internal static int ActiveTargetCount => _state?.Active.Count ?? 0;
    internal static bool TimerRunning => _state?.Timer.Enabled ?? false;
    internal static bool IsActiveForQa(IEmojiAnimationTarget target) => _state?.Active.Contains(target) == true;
    internal static bool RefreshPendingForQa => _state?.RefreshPending ?? false;
    internal static int RefreshDispatchesForQa => _state?.RefreshDispatches ?? 0;
    internal static int FullRefreshesForQa => _state?.FullRefreshes ?? 0;
    internal static int EligibilityChecksForQa => _state?.EligibilityChecks ?? 0;

    internal static void Subscribe(IEmojiAnimationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.AnimationControl.IsDisposed || target.AnimationControl.Disposing) return;
        var state = _state ??= new SchedulerState();
        state.Subscribe(target);
    }

    internal static void Unsubscribe(IEmojiAnimationTarget target)
    {
        if (_state is not { } state || !state.RemoveTarget(target)) return;
        if (state.Entries.Count == 0) { state.Dispose(); _state = null; }
        else state.UpdateTimer();
    }

    internal static void RefreshVisibility() => _state?.Refresh();

    // Deterministic app-owned QA can exercise the same clip/visibility algorithm
    // even on a test desktop with Windows animations disabled or no input focus.
    internal static void RefreshVisibilityForQa(bool? motionAllowed = null, bool? ownerActive = null)
        => _state?.Refresh(motionAllowed, ownerActive);
    internal static void TickForQa(bool? motionAllowed = null, bool? ownerActive = null)
        => _state?.Tick(motionAllowed, ownerActive);

    internal static bool CanAnimate(IEmojiAnimationTarget target)
        => Eligible(target, MotionEnabled, null);

    private static bool ReadMotionSetting()
    {
        // SPI_GETCLIENTAREAANIMATION is defined by the installed Windows SDK's
        // WinUser.h. WinForms has no IsClientAreaAnimationEnabled property.
        return SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled;
    }
    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    private static bool Eligible(IEmojiAnimationTarget target, bool motionAllowed, bool? ownerActive)
    {
        var control = target.AnimationControl;
        if (!motionAllowed || control.IsDisposed || control.Disposing || !control.IsHandleCreated ||
            !control.Visible || control.Width <= 0 || control.Height <= 0 || !target.WantsAnimation) return false;
        var top = control.TopLevelControl;
        var owner = target.AnimationOwner;
        if (top is ToolStripDropDown popup)
        {
            owner ??= (popup as ContextMenuStrip)?.SourceControl?.FindForm() ?? popup.OwnerItem?.Owner?.FindForm();
            if (!popup.Visible || (owner?.WindowState == FormWindowState.Minimized)) return false;
            if (!(ownerActive ?? (popup.ContainsFocus || owner?.ContainsFocus == true ||
                (owner is not null && ReferenceEquals(Form.ActiveForm, owner))))) return false;
        }
        else
        {
            owner ??= control.FindForm();
            if (owner is null || !owner.Visible || owner.WindowState == FormWindowState.Minimized ||
                !(ownerActive ?? (owner.ContainsFocus || ReferenceEquals(Form.ActiveForm, owner)))) return false;
        }
        // RectangleToScreen is unnecessary here: accumulating child-local offsets
        // preserves native negative scroll positions and avoids double-adding Offset.
        var visible = control.ClientRectangle;
        for (var child = control; child.Parent is { } parent; child = parent)
        {
            if (!parent.Visible || parent.IsDisposed || parent.Disposing) return false;
            visible.Offset(child.Left, child.Top);
            visible.Intersect(parent.ClientRectangle);
            if (visible.Width <= 0 || visible.Height <= 0) return false;
        }
        return true;
    }

    private sealed class SchedulerState : IDisposable
    {
        internal readonly Dictionary<IEmojiAnimationTarget, HashSet<Control>> Entries = [];
        internal readonly HashSet<IEmojiAnimationTarget> Active = [];
        internal readonly System.Windows.Forms.Timer Timer = new() { Interval = 40 };
        private readonly Dictionary<Control, Observation> _observations = [];
        private readonly SynchronizationContext _context = SynchronizationContext.Current is WindowsFormsSynchronizationContext context
            ? context : new WindowsFormsSynchronizationContext();
        private bool _disposed;
        private bool _refreshQueued;
        private int _preferencesQueued;
        internal bool RebindNeeded;
        internal bool RefreshPending => _refreshQueued;
        internal int RefreshDispatches { get; private set; }
        internal int FullRefreshes { get; private set; }
        internal int EligibilityChecks { get; private set; }

        internal SchedulerState()
        {
            _motionEnabled = null; // Re-read if Windows settings changed while no emoji targets existed.
            Timer.Tick += (_, _) => Tick();
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
        }

        private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args)
        {
            // SystemEvents is not guaranteed to run on this UI thread. Only one
            // posted settings callback is needed and it never enumerates UI state.
            if (Interlocked.Exchange(ref _preferencesQueued, 1) != 0) return;
            try { _context.Post(_ =>
            {
                Interlocked.Exchange(ref _preferencesQueued, 0);
                if (!_disposed) { _motionEnabled = null; QueueRefresh(); }
            }, null); }
            catch (InvalidOperationException)
            { Interlocked.Exchange(ref _preferencesQueued, 0); /* The owning dispatcher may already be closing. */ }
        }

        internal void QueueRefresh(bool rebind = false)
        {
            if (_disposed) return;
            RebindNeeded |= rebind;
            if (_refreshQueued) return;
            _refreshQueued = true;
            // Dispatch through the UI thread's stable marshalling context, not a
            // message-row HWND that can disappear before this callback is pumped.
            try { _context.Post(_ =>
            {
                _refreshQueued = false;
                if (!_disposed) { RefreshDispatches++; Refresh(); }
            }, null); }
            catch (InvalidOperationException) { _refreshQueued = false; Refresh(); }
        }

        internal void Subscribe(IEmojiAnimationTarget target)
        {
            if (!Entries.ContainsKey(target)) Entries.Add(target, []);
            // Incremental ref-counted observers keep a long emoji history O(N ×
            // ancestor depth), rather than rebuilding all prior rows N times.
            BindTarget(target);
            if (IsEligible(target, MotionEnabled, null)) Active.Add(target);
            else Active.Remove(target);
            UpdateTimer();
        }

        internal bool RemoveTarget(IEmojiAnimationTarget target)
        {
            if (!Entries.Remove(target, out var chain)) return false;
            foreach (var control in chain) ReleaseObservation(control);
            Active.Remove(target);
            return true;
        }

        internal void UpdateTimer() => Timer.Enabled = Active.Count != 0;

        internal void Refresh(bool? motionAllowed = null, bool? ownerActive = null)
        {
            if (_disposed) return;
            FullRefreshes++;
            foreach (var target in Entries.Keys.Where(target => target.AnimationControl.IsDisposed ||
                target.AnimationControl.Disposing).ToArray())
                RemoveTarget(target);
            if (Entries.Count == 0)
            {
                Dispose();
                if (ReferenceEquals(_state, this)) _state = null;
                return;
            }
            if (RebindNeeded)
            {
                RebindNeeded = false;
                foreach (var target in Entries.Keys.ToArray()) BindTarget(target);
            }
            var permitted = motionAllowed ?? MotionEnabled;
            foreach (var target in Entries.Keys)
            {
                var eligible = IsEligible(target, permitted, ownerActive);
                if (eligible) Active.Add(target);
                else Active.Remove(target);
            }
            // There is no idle timer polling when every animated item is hidden,
            // clipped, minimized, inactive or reduced-motion. Observers resume it.
            UpdateTimer();
        }

        internal void Tick(bool? motionAllowed = null, bool? ownerActive = null)
        {
            if (_disposed) return;
            // Visibility/scroll/activation/preferences observers maintain Active
            // in one coalesced full refresh. A normal 40ms frame must not rescan
            // every offscreen history row. QA policy overrides deliberately refresh
            // the whole set so the deterministic fixture can control eligibility.
            if (motionAllowed.HasValue || ownerActive.HasValue) Refresh(motionAllowed, ownerActive);
            if (_disposed) return;
            var permitted = motionAllowed ?? MotionEnabled;
            foreach (var target in Active.ToArray())
            {
                // Recheck just active targets before invalidation. This also closes
                // the brief gap before a queued hide/minimize notification is pumped.
                if (IsEligible(target, permitted, ownerActive)) target.InvalidateAnimation();
                else
                {
                    Active.Remove(target);
                    if (target.AnimationControl.IsDisposed || target.AnimationControl.Disposing) RemoveTarget(target);
                }
            }
            if (Entries.Count == 0)
            {
                Dispose();
                if (ReferenceEquals(_state, this)) _state = null;
            }
            else UpdateTimer();
        }

        private bool IsEligible(IEmojiAnimationTarget target, bool motionAllowed, bool? ownerActive)
        { EligibilityChecks++; return Eligible(target, motionAllowed, ownerActive); }

        private void BindTarget(IEmojiAnimationTarget target)
        {
            var required = new HashSet<Control>();
            for (var control = target.AnimationControl; control is not null; control = control.Parent) required.Add(control);
            if (target.AnimationOwner is { } owner) required.Add(owner);
            var previous = Entries[target];
            foreach (var control in previous.Where(control => !required.Contains(control))) ReleaseObservation(control);
            foreach (var control in required.Where(control => !previous.Contains(control)))
            {
                if (_observations.TryGetValue(control, out var observation)) observation.References++;
                else _observations.Add(control, new Observation(this, control));
            }
            Entries[target] = required;
        }

        private void ReleaseObservation(Control control)
        {
            if (!_observations.TryGetValue(control, out var observation) || --observation.References != 0) return;
            observation.Dispose(); _observations.Remove(control);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            Timer.Stop(); Timer.Dispose();
            foreach (var observer in _observations.Values) observer.Dispose();
            _observations.Clear(); Entries.Clear(); Active.Clear();
        }

        private sealed class Observation : IDisposable
        {
            private readonly SchedulerState _state;
            private readonly Control _control;
            internal int References = 1;
            internal Observation(SchedulerState state, Control control)
            {
                _state = state; _control = control;
                control.VisibleChanged += Changed; control.LocationChanged += Changed; control.SizeChanged += Changed;
                control.ParentChanged += ParentChanged; control.HandleCreated += ParentChanged; control.HandleDestroyed += ParentChanged;
                control.Layout += Layout; control.Disposed += ParentChanged;
                if (control is ScrollableControl scroll) scroll.Scroll += Scroll;
                if (control is ModernMessageList list) list.MetricsChanged += Changed;
                if (control is Form form) { form.Activated += Changed; form.Deactivate += Changed; }
            }
            private void Changed(object? sender, EventArgs args) => _state.QueueRefresh();
            private void ParentChanged(object? sender, EventArgs args) => _state.QueueRefresh(rebind: true);
            private void Layout(object? sender, LayoutEventArgs args) => _state.QueueRefresh();
            private void Scroll(object? sender, ScrollEventArgs args) => _state.QueueRefresh();
            public void Dispose()
            {
                _control.VisibleChanged -= Changed; _control.LocationChanged -= Changed; _control.SizeChanged -= Changed;
                _control.ParentChanged -= ParentChanged; _control.HandleCreated -= ParentChanged; _control.HandleDestroyed -= ParentChanged;
                _control.Layout -= Layout; _control.Disposed -= ParentChanged;
                if (_control is ScrollableControl scroll) scroll.Scroll -= Scroll;
                if (_control is ModernMessageList list) list.MetricsChanged -= Changed;
                if (_control is Form form) { form.Activated -= Changed; form.Deactivate -= Changed; }
            }
        }
    }
}
