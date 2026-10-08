using MTKChat.Contracts;
using MTKChat.Cryptography;
using System.Net;

namespace MTKChat.Desktop;

// The window only presents the encrypted session. Injection keeps UI QA away
// from physical microphones; the session still owns audio and encryption.
internal sealed class CallForm : ModernForm
{
    private readonly ChatApiClient _api;
    private readonly Guid _user;
    private CallView _call;
    private readonly ChatUser[] _invitees;
    private ICallSession? _session;
    private readonly Func<ICallSession> _sessionFactory;
    private readonly Func<Task>? _prepare;
    private readonly bool _snapshot, _incoming, _injectedSession;
    private readonly TimeProvider _clock;
    private readonly AvatarCache _avatars;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _name = new(), _status = new(), _duration = new(), _securityDetail = new();
    private readonly AvatarView _avatar = new();
    private readonly FlowLayoutPanel _people = new();
    private readonly TableLayoutPanel _actions = new(), _body = new();
    private readonly ModernButton _accept = Theme.Button("Kabul et", ButtonKind.Primary);
    private readonly ModernButton _mute = Theme.Button("Sustur", ButtonKind.Secondary);
    private readonly ModernButton _speaker = Theme.Button("Sesi kapat", ButtonKind.Secondary);
    private readonly ModernButton _end = Theme.Button("Reddet", ButtonKind.Danger);
    private readonly ModernButton _security = Theme.Button("Arama güvenliği", ButtonKind.Ghost);
    private readonly Dictionary<Guid, CallPersonCard> _cards = [];
    private readonly Dictionary<ModernButton, Control> _actionTiles = [];
    private long? _connectedAt, _invitationFailureAt;
    private Guid? _verifiedUser;
    private bool _accepting, _checkingInvitation, _ended, _leaveSent, _cleaned, _detailsOpen;
    private CallConnectionState _connectionState = CallConnectionState.Connecting;

    internal CallForm(ChatApiClient api, DeviceIdentity device, Guid user, CallView call, bool incoming,
        bool snapshot = false, Func<Task>? prepare = null, AvatarCache? avatars = null,
        Func<ICallSession>? sessionFactory = null, TimeProvider? clock = null)
    {
        SuspendLayout();
        _api = api; _user = user; _call = call; _invitees = call.Invitees.ToArray(); _incoming = incoming; _snapshot = snapshot;
        _prepare = prepare; _injectedSession = sessionFactory is not null;
        _sessionFactory = sessionFactory ?? (() => new CallSession(api, call.Id, user, device));
        _clock = clock ?? TimeProvider.System;
        _avatars = avatars ?? new AvatarCache(api);
        Text = "MTK Chat · Sesli arama"; Icon = Theme.AppIcon();
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(580, 740); MinimumSize = new Size(500, 640);
        MaximizeBox = false; BackColor = Theme.Canvas; StartPosition = FormStartPosition.CenterParent;
        _body.Dock = DockStyle.Fill; _body.BackColor = Theme.Canvas; _body.Padding = new Padding(24, 18, 24, 18);
        _body.ColumnCount = 1; _body.RowCount = 5;
        foreach (var style in new[] { new RowStyle(SizeType.Absolute, 30), new RowStyle(SizeType.Absolute, 202),
            new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 54), new RowStyle(SizeType.Absolute, 112) }) _body.RowStyles.Add(style);
        _body.Controls.Add(Label(IsGroup ? "GRUP SESLİ ARAMASI" : "SESLİ ARAMA", 9, Theme.Muted), 0, 0);
        var hero = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 112)); hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); hero.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var avatarHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Margin = Padding.Empty };
        _avatar.Size = new Size(96, 96); _avatar.AvatarColor = Theme.Accent; avatarHost.Controls.Add(_avatar);
        avatarHost.Resize += (_, _) => _avatar.Location = new Point((avatarHost.Width - _avatar.Width) / 2, 4 * DeviceDpi / 96);
        foreach (var label in new[] { _name, _status, _duration })
        { label.Dock = DockStyle.Fill; label.BackColor = Color.Transparent; label.TextAlign = ContentAlignment.MiddleCenter; label.AutoEllipsis = true; label.Margin = Padding.Empty; }
        _name.Font = Theme.Font(21, FontStyle.Bold); _name.ForeColor = Theme.Text;
        _status.Font = Theme.Font(11); _status.ForeColor = Theme.Muted; _status.Name = "CallStatus";
        _duration.Font = Theme.Font(10); _duration.ForeColor = Theme.Success; _duration.Name = "CallDuration";
        hero.Controls.Add(avatarHost, 0, 0); hero.Controls.Add(_name, 0, 1); hero.Controls.Add(_status, 0, 2); hero.Controls.Add(_duration, 0, 3);
        _body.Controls.Add(hero, 0, 1);
        _people.Dock = DockStyle.Fill; _people.FlowDirection = FlowDirection.TopDown; _people.WrapContents = false;
        _people.AutoScroll = true; _people.BackColor = Theme.Canvas; _people.Margin = new Padding(0, 8, 0, 8);
        _people.Resize += (_, _) => ResizePeople(); _body.Controls.Add(_people, 0, 2);
        var securityPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        securityPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); securityPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _security.Dock = DockStyle.Fill; _security.Text = "Uçtan uca şifreli · Güvenliği doğrula";
        _security.ForeColor = Theme.Muted; _security.AccessibleName = "Arama güvenliğini doğrula";
        _security.Click += (_, _) => { _detailsOpen = !_detailsOpen; RenderSecurity(); };
        _securityDetail.Dock = DockStyle.Fill; _securityDetail.Font = Theme.Font(9); _securityDetail.ForeColor = Theme.Muted;
        _securityDetail.TextAlign = ContentAlignment.MiddleCenter; _securityDetail.Name = "CallSecurityDetail";
        securityPanel.Controls.Add(_security, 0, 0); securityPanel.Controls.Add(_securityDetail, 0, 1); _body.Controls.Add(securityPanel, 0, 3);
        _actions.Dock = DockStyle.Fill; _actions.RowCount = 1; _actions.Margin = Padding.Empty; _actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddAction(_accept, ModernButtonIcon.Phone, "Kabul et"); AddAction(_mute, ModernButtonIcon.Microphone, "Sustur");
        AddAction(_speaker, ModernButtonIcon.Speaker, "Sesi kapat"); AddAction(_end, ModernButtonIcon.HangUp, incoming ? "Reddet" : "Bitir");
        _accept.Click += async (_, _) => await AcceptAsync(); _mute.Click += async (_, _) => await ToggleMuteAsync();
        _speaker.Click += (_, _) =>
        {
            if (_session is null) return;
            try { _session.SetSpeakerMuted(!_session.SpeakerMuted); RenderActions(); }
            catch (Exception) { EndWithError("Ses çıkışına erişilemedi. Ses aygıtını kontrol edip yeniden arayın."); }
        };
        _end.Click += (_, _) => Close(); _body.Controls.Add(_actions, 0, 4); Controls.Add(_body);
        RenderIdentity(); RenderPeople(); RenderState(); RenderActions();
        _timer.Tick += async (_, _) => { RenderDuration(); await CheckInvitationAsync(); };
        if (!snapshot) Shown += async (_, _) =>
        { ClampToWorkingArea(); if (incoming) { System.Media.SystemSounds.Asterisk.Play(); _timer.Start(); } else await AcceptAsync(); };
        ResumeLayout(true);
    }
    private bool IsGroup => _invitees.Length > 2;
    private static Label Label(string text, float size, Color color) => new()
    { Text = text, Font = Theme.Font(size), ForeColor = color, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty };
    private void ClampToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
    private void AddAction(ModernButton button, ModernButtonIcon icon, string caption)
    {
        var tile = new TableLayoutPanel { RowCount = 2, ColumnCount = 1, Dock = DockStyle.Fill, Margin = new Padding(4, 6, 4, 0) };
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); tile.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        button.Text = caption; button.AccessibleName = caption; button.VectorIcon = icon; button.CircularSurface = true;
        button.Size = new Size(64, 64); button.Anchor = AnchorStyles.None; button.Margin = Padding.Empty;
        var label = Label(caption, 9, Theme.Text); label.Name = "Caption";
        tile.Controls.Add(button, 0, 0); tile.Controls.Add(label, 0, 1); _actionTiles[button] = tile;
    }
    private void Caption(ModernButton button, string text)
    { button.Text = button.AccessibleName = text; _actionTiles[button].Controls["Caption"]!.Text = text; }
    private void RenderActions()
    {
        var ringing = _incoming && _session is null && !_ended;
        var buttons = _ended ? new[] { _end } : ringing ? new[] { _end, _accept } : new[] { _speaker, _mute, _end };
        _actions.SuspendLayout();
        // Reuse the tiles and only reparent them on incoming/active/ended changes.
        // Polling no longer recreates every control or steals keyboard focus.
        if (!_actions.Controls.Cast<Control>().SequenceEqual(buttons.Select(b => _actionTiles[b])))
        {
            _actions.Controls.Clear(); _actions.ColumnStyles.Clear(); _actions.ColumnCount = buttons.Length;
            for (var i = 0; i < buttons.Length; i++)
            { _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length)); _actions.Controls.Add(_actionTiles[buttons[i]], i, 0); }
        }
        _accept.Enabled = !_accepting && !_ended;
        _mute.Enabled = _speaker.Enabled = _session is not null && !_ended && !_accepting;
        _mute.VectorIcon = _session?.Muted == true ? ModernButtonIcon.MicrophoneOff : ModernButtonIcon.Microphone;
        _mute.ForeColor = _session?.Muted == true ? Theme.Warning : Theme.Text; Caption(_mute, _session?.Muted == true ? "Mikrofonu aç" : "Sustur");
        _speaker.VectorIcon = _session?.SpeakerMuted == true ? ModernButtonIcon.SpeakerOff : ModernButtonIcon.Speaker;
        Caption(_speaker, _session?.SpeakerMuted == true ? "Sesi aç" : "Sesi kapat");
        _speaker.AccessibleName = _session?.SpeakerMuted == true ? "Gelen arama sesini aç" : "Gelen arama sesini kapat";
        Caption(_end, _ended ? "Kapat" : ringing ? "Reddet" : "Bitir"); _end.VectorIcon = _ended ? ModernButtonIcon.Close : ModernButtonIcon.HangUp;
        _actions.ResumeLayout(true);
    }
    private void RenderIdentity()
    {
        var counterpart = _invitees.FirstOrDefault(p => p.Id != _user);
        _name.Text = IsGroup ? _call.Title : counterpart?.DisplayName ?? _call.Title;
        if (!IsGroup && counterpart is not null) _ = _avatars.ApplyAsync(_avatar, counterpart);
        else _avatar.Initials = UserPresentation.Initials(_call.Title);
    }
    private void RenderState()
    {
        if (_ended) return;
        _status.ForeColor = _connectionState == CallConnectionState.Reconnecting ? Theme.Warning : Theme.Muted;
        _status.Text = _session is null && _incoming ? "Gelen sesli arama…" : _connectionState switch
        {
            CallConnectionState.Connected => IsGroup ? $"{_call.Peers.Count} kişi görüşmede" : "Görüşme devam ediyor",
            CallConnectionState.Reconnecting => "Bağlantı kesildi · Yeniden bağlanıyor…",
            _ => _accepting ? "Güvenli bağlantı kuruluyor…" : "Çalıyor…"
        };
        RenderDuration(); RenderSecurity();
    }
    private void RenderDuration()
    {
        if (_ended) return; // Retain the final duration, not an ever-growing ended call.
        if (_connectedAt is not { } start) { _duration.Text = ""; return; }
        var elapsed = _clock.GetElapsedTime(start);
        _duration.Text = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }
    private void Ui(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated || _cleaned) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing && !_cleaned) action(); }); } catch (InvalidOperationException) { }
    }
    internal async Task AcceptAsync()
    {
        if (_accepting || _session is not null || (_snapshot && !_injectedSession) || _ended || _cleaned) return;
        _accepting = true; RenderActions(); RenderState();
        try
        {
            if (_prepare is not null) await _prepare(); if (_cleaned || _ended) return;
            if (!_snapshot) VoicePlayback.StopCurrent();
            var session = _sessionFactory(); _session = session;
            session.Changed += view => Ui(() => { if (_session != session || _ended) return; _call = view; RenderPeople(); RenderState(); });
            session.ConnectionStateChanged += state => Ui(() =>
            {
                if (_session != session || _ended) return; _connectionState = state;
                if (state == CallConnectionState.Connected) _connectedAt ??= _clock.GetTimestamp();
                if (state == CallConnectionState.Ended) { EndWithError("Arama sona erdi.", false); return; }
                RenderState(); RenderActions();
            });
            session.Failed += error => Ui(() => { if (_session == session) EndWithError(error); });
            RenderActions(); await session.StartAsync(); if (_cleaned || _ended || _session != session) return;
            if (session.State == CallConnectionState.Ended) { EndWithError("Arama sona erdi.", false); return; }
            _connectionState = session.State; if (session.State == CallConnectionState.Connected) _connectedAt ??= _clock.GetTimestamp();
            RenderPeople(); RenderState(); if (!_snapshot) _timer.Start();
        }
        catch (Exception ex) { if (!_cleaned && !_ended) EndWithError(ex.Message); }
        finally { _accepting = false; if (!_cleaned) { RenderActions(); RenderState(); } }
    }
    internal async Task ToggleMuteAsync()
    {
        var session = _session; if (session is null || _ended || !_mute.Enabled) return; _mute.Enabled = false;
        try { await session.ToggleMuteAsync(); }
        catch (Exception ex)
        {
            // A failed unmute must not destroy a healthy call. The engine keeps
            // capture muted until the server accepts the requested change.
            if (!_cleaned && !_ended) { _status.Text = ex.Message; _status.ForeColor = Theme.Warning; }
        }
        finally { if (!_cleaned && _session == session) { RenderActions(); RenderPeople(); } }
    }
    private async Task CheckInvitationAsync()
    {
        if (!_incoming || _snapshot || _session is not null || _accepting || _checkingInvitation || _ended || _cleaned) return;
        if (_clock.GetUtcNow() - _call.CreatedAt >= TimeSpan.FromSeconds(60))
        { EndWithError("Arama yanıtlanmadı.", false); return; }
        _checkingInvitation = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var view = await _api.GetCallAsync(_call.Id, timeout.Token); if (_cleaned || _session is not null || _accepting) return;
            _invitationFailureAt = null; _call = view; RenderPeople(); RenderState();
        }
        catch (Exception ex)
        {
            if (_cleaned || _lifetime.IsCancellationRequested || _session is not null || _accepting) return;
            if (ex is ChatApiException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized })
                EndWithError("Arama sona erdi veya iptal edildi.", false);
            else
            {
                _invitationFailureAt ??= _clock.GetTimestamp();
                if (_clock.GetElapsedTime(_invitationFailureAt.Value) > TimeSpan.FromSeconds(20)) EndWithError("Aramaya ulaşılamadı. Lütfen yeniden deneyin.");
                else { _status.Text = "Arama durumu yeniden kontrol ediliyor…"; _status.ForeColor = Theme.Warning; }
            }
        }
        finally { _checkingInvitation = false; }
    }
    private void EndWithError(string message, bool warning = true)
    {
        if (_ended || _cleaned) return; _ended = true; _timer.Stop(); _session?.Dispose(); _session = null;
        _status.Text = message; _status.ForeColor = warning ? Theme.Warning : Theme.Muted;
        RenderDuration(); RenderPeople(); RenderActions(); RenderSecurity(); _ = LeaveQuietlyAsync();
    }
    private async Task LeaveQuietlyAsync()
    {
        if (_leaveSent || _snapshot) return; _leaveSent = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _api.LeaveCallAsync(_call.Id, timeout.Token); } catch { /* Server also expires disconnected participants. */ }
    }
    private void RenderPeople()
    {
        _people.SuspendLayout();
        foreach (var user in _invitees)
        {
            if (!_cards.TryGetValue(user.Id, out var card))
            {
                card = new CallPersonCard(user, _avatars, () => { _verifiedUser = user.Id; _detailsOpen = true; RenderSecurity(); });
                _cards[user.Id] = card; _people.Controls.Add(card);
            }
            var peer = _call.Peers.FirstOrDefault(p => p.User.Id == user.Id);
            var invitation = _call.Invitations?.FirstOrDefault(i => i.UserId == user.Id)?.State;
            var authenticated = user.Id == _user || _session?.Codes.ContainsKey(user.Id) == true;
            var muted = user.Id == _user ? _session?.Muted ?? peer?.Muted == true : peer?.Muted == true;
            var status = _ended ? "Arama sona erdi" : peer is not null ? !authenticated ? "Güvenlik doğrulanıyor…" : muted ? "Mikrofon kapalı" : "Görüşmede" :
                invitation == "declined" ? "Katılmadı" : user.Id == _user && _incoming ? "Yanıt bekleniyor" : "Çalıyor…";
            card.UpdateStatus((user.Id == _user ? "Siz · " : "") + status, peer is not null && authenticated && !_ended, !_ended && _session?.Codes.ContainsKey(user.Id) == true);
        }
        ResizePeople(); _people.ResumeLayout(true); RenderSecurity();
    }
    private void ResizePeople()
    {
        var width = Math.Max(1, _people.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2 * DeviceDpi / 96);
        foreach (var card in _cards.Values) card.Width = width;
    }
    private void RenderSecurity()
    {
        var codes = _session?.Codes; _security.ForeColor = codes?.Count > 0 && !_ended ? Theme.Success : Theme.Muted;
        _securityDetail.Visible = _detailsOpen; _body.RowStyles[3].Height = (_detailsOpen ? 100 : 54) * DeviceDpi / 96f;
        var selected = _verifiedUser ?? codes?.Keys.FirstOrDefault();
        _securityDetail.Text = !_ended && selected is { } id && codes?.TryGetValue(id, out var code) == true
            ? $"{_invitees.FirstOrDefault(u => u.Id == id)?.DisplayName}\n{code}\nBu kodu karşı tarafla karşılaştırın."
            : "Güvenlik kodu, karşı taraf güvenli aramaya katıldığında görünür.";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_cleaned)
        {
            _cleaned = true; _lifetime.Cancel(); _timer.Stop(); _timer.Dispose(); _session?.Dispose(); _session = null;
            // Hidden action tiles are unparented; dispose them explicitly too.
            foreach (var tile in _actionTiles.Values) tile.Dispose();
            _lifetime.Dispose(); _ = LeaveQuietlyAsync();
        }
        base.Dispose(disposing);
    }
    internal void RefreshForQa() { RenderState(); RenderPeople(); RenderActions(); }
    internal IReadOnlyList<Control> ParticipantCardsForQa => _cards.Values.Cast<Control>().ToArray();
}

internal sealed class CallPersonCard : RoundedPanel
{
    private readonly Label _status = new();
    private readonly ModernButton _verify = Theme.Button("Kod", ButtonKind.Ghost);
    internal CallPersonCard(ChatUser user, AvatarCache avatars, Action verify)
    {
        Height = 56; Margin = new Padding(0, 0, 0, 6); CornerRadius = 14; FillColor = Theme.Surface; BorderColor = Theme.Divider;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6), RowCount = 1, ColumnCount = 3 };
        // An AutoSize row used the nested table's default preferred height,
        // positioning the avatar/status below this compact card on 125% DPI.
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        var avatar = new AvatarView { Width = 36, Height = 36, Anchor = AnchorStyles.Left, Margin = Padding.Empty }; _ = avatars.ApplyAsync(avatar, user);
        var text = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); text.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var name = new Label { Text = user.DisplayName, AutoEllipsis = true, ForeColor = Theme.Text, Font = Theme.Font(9.5f, FontStyle.Bold), Dock = DockStyle.Fill, Margin = Padding.Empty };
        _status.Dock = DockStyle.Fill; _status.Font = Theme.Font(8.5f); _status.Margin = Padding.Empty; _status.AutoEllipsis = true;
        text.Controls.Add(name, 0, 0); text.Controls.Add(_status, 0, 1);
        _verify.Dock = DockStyle.Fill; _verify.AccessibleName = user.DisplayName + " arama güvenlik kodu"; _verify.Click += (_, _) => verify();
        layout.Controls.Add(avatar, 0, 0); layout.Controls.Add(text, 1, 0); layout.Controls.Add(_verify, 2, 0); Controls.Add(layout);
    }
    internal void UpdateStatus(string status, bool connected, bool canVerify)
    { _status.Text = status; _status.ForeColor = connected ? Theme.Success : Theme.Muted; _verify.Visible = canVerify; }
}
