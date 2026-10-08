using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm : ModernForm
{
    // Production uses the site's HTTPS proxy; local development can override this explicitly.
    private readonly ChatApiClient _api;
    private readonly bool _snapshotMode;
    private readonly AvatarCache _avatars;
    private readonly DeviceIdentity _identity = DeviceIdentityStore.LoadOrCreate();
    private readonly Dictionary<Guid, DeviceKeyBundle> _deviceCache = new();
    private readonly FlowLayoutPanel _conversationList = new();
    private readonly ModernMessageList _messageList = new();
    private readonly Label _conversationTitle = new();
    private readonly Label _conversationMeta = new();
    private readonly Label _securityStatus = new();
    private readonly AvatarView _accountAvatar = new();
    private readonly AvatarView _headerAvatar = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 3000 };

    private LoginResponse? _session;
    private ConversationSummary? _selectedConversation;
    private RoundedPanel? _selectedConversationCard;
    private bool _refreshing;
    private bool _sending;
    private readonly SemaphoreSlim _conversationListGate = new(1, 1);
    private bool _loadingConversations;
    private int _conversationVersion;
    private string? _renderFingerprint;
    private ExpiryChoice _selectedExpiry = new("Kalıcı", null);

    public MainForm(bool snapshotMode = false, ChatApiClient? api = null)
    {
        _snapshotMode = snapshotMode;
        _api = api ?? new ChatApiClient(Environment.GetEnvironmentVariable("MTK_CHAT_API_URL") ?? "https://mtkaya.me/chat/");
        _avatars = new AvatarCache(_api);
        Text = "MTK Chat";
        Icon = Theme.AppIcon();
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1536, 940);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Font(9f);
        if (!snapshotMode) Opacity = 0;

        BuildLayout();
        ConfigureChatNotifications();
        FormClosed += (_, _) => ClearPendingImage();
        FormClosed += (_, _) => ClearPendingVoice();
        FormClosed += (_, _) => ClearPendingFile();
        FormClosing += (_, _) => _callForm?.Close();
        if (!snapshotMode) Shown += OnShownAsync;
        _readTimer.Tick += async (_, _) => await MarkVisibleReadAsync();
        _refreshTimer.Tick += async (_, _) => await RefreshBackgroundAsync();
    }

    private void BuildLayout()
    {
        BuildPremiumLayout();
    }

    private async void OnShownAsync(object? sender, EventArgs eventArgs)
    {
        using var login = new LoginForm();
        while (_session is null)
        {
            if (login.ShowDialog(this) != DialogResult.OK)
            {
                Close();
                return;
            }
            try
            {
                login.SetBusy(true);
                var session = await _api.LoginAsync(login.Email, login.Password);
                await _api.RegisterDeviceAsync(new RegisterDeviceRequest(
                    Environment.MachineName,
                    _identity.ExportEncryptionPublicKey(),
                    _identity.ExportSigningPublicKey()));
                _session = session;
                RefreshRailAccount(_session.User);
            }
            catch (Exception exception)
            {
                login.ShowError(exception.Message);
            }
        }

        try
        {
            Opacity = 1;
            await LoadChatPreferencesAsync();
            // Polling must survive an incomplete first load; do not wait for history
            // and presence before enabling its recovery path.
            _refreshTimer.Start();
            _readTimer.Start();
            await LoadConversationsAsync();
        }
        catch (Exception exception)
        {
            ShowError($"Sohbetler yüklenemedi: {exception.Message}");
        }
    }

    private async Task LoadConversationsAsync(Guid? selectId = null, bool silent = false)
    {
        if (_session is null || IsDisposed || silent && _loadingConversations) return;
        await _conversationListGate.WaitAsync();
        _loadingConversations = true;
        try
        {
            var conversations = await _api.GetConversationsAsync();
            if (IsDisposed) return;
            // Polling must not dispose a popup's owner while WinForms is processing
            // its native menu loop. The next poll applies the latest complete list.
            if (ConversationPopupOpen()) return;
            var selectedId = selectId ?? _selectedConversation?.Id;
            ObserveConversationNotifications(conversations);
            ReconcileConversationPreviews(conversations);
            ReconcileConversationCards(conversations);
            RefreshPersonalConversationIndicators();
            var selected = conversations.FirstOrDefault(c => c.Id == selectedId) ?? conversations.FirstOrDefault(c => !IsArchivedConversation(c.Id));
            if (selected is not null)
            {
                var card = _conversationList.Controls.OfType<RoundedPanel>().Single(c => ((ConversationSummary)c.Tag!).Id == selected.Id);
                if (_selectedConversation?.Id != selected.Id || selectId is not null) await SelectConversationAsync(selected, card, silent);
                else
                {
                    var selectedChanged = !ConversationVisualEquals(_selectedConversation, selected) ||
                        !ReferenceEquals(_selectedConversationCard, card);
                    _selectedConversation = selected;
                    RefreshEncryptionRecipientStatus();
                    _selectedConversationCard = card;
                    if (selectedChanged)
                    {
                        SetConversationCardSelected(card, true);
                        _conversationTitle.Text = selected.Title;
                        // Presence owns the direct-chat subtitle; list polling must not erase last seen.
                        if (selected.Kind != "direct")
                            _conversationMeta.Text = string.Join(" · ", selected.Participants.Select(u => u.DisplayName));
                        _ = ApplyConversationAvatarAsync(_headerAvatar, selected);
                    }
                }
            }
            else ResetConversationSelection();
            if (!_snapshotMode) ScheduleConversationPreviews();
        }
        catch (Exception ex) { RecordNetworkFailure(ex); if (!silent && !IsDisposed) ShowError(ex.Message); }
        finally { _loadingConversations = false; _conversationListGate.Release(); }
    }

    private RoundedPanel CreateConversationCard(ConversationSummary conversation)
    {
        int Scale(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96f));
        var card = new RoundedPanel
        {
            Tag = conversation,
            Height = Scale(72),
            Width = Math.Max(Scale(180), _conversationList.ClientSize.Width - _conversationList.Padding.Horizontal - Scale(2)),
            Margin = new Padding(0, 0, 0, Scale(2)),
            Padding = Padding.Empty,
            FillColor = Theme.Sidebar,
            BorderColor = Color.Transparent,
            CornerRadius = 13,
            Cursor = Cursors.Hand
        };
        var avatar = new AvatarView
        {
            Initials = Initials(conversation.Title),
            AvatarColor = Theme.SurfaceHover,
            Size = new Size(Scale(48), Scale(48)),
            Location = new Point(Scale(8), Scale(12))
        };
        _ = ApplyConversationAvatarAsync(avatar, conversation);
        var time = new Label
        {
            Name = "ConversationLastMessageTime",
            Text = ConversationPreviewTime(conversation),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Muted,
            Font = Theme.Font(9f),
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        var name = new Label
        {
            Name = "ConversationTitle",
            Text = conversation.Title,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 11.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            AutoEllipsis = true
        };
        var preview = new Label
        {
            // Only authenticated, locally decrypted content enters this label.
            // The API's generic encrypted label is never used as message text.
            Name = "ConversationLastMessagePreview",
            Text = ConversationPreviewText(conversation),
            ForeColor = Theme.Muted,
            Font = Theme.Font(9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            AutoEllipsis = true
        };
        var unread = new RoundedPanel
        {
            Name = "ConversationUnreadBadge",
            Visible = conversation.UnreadCount > 0,
            FillColor = Theme.Accent,
            GradientEndColor = Theme.GradientEnd,
            CornerRadius = 24,
            BorderWidth = 0,
            AccessibleName = $"{conversation.UnreadCount} okunmamış mesaj"
        };
        var count = new Label
        {
            Text = conversation.UnreadCount > 99 ? "99+" : conversation.UnreadCount.ToString(),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Font(8f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            UseMnemonic = false
        };
        unread.Controls.Add(count);
        card.Controls.Add(preview);
        card.Controls.Add(name);
        card.Controls.Add(time);
        card.Controls.Add(avatar);
        card.Controls.Add(unread);
        void LayoutCard()
        {
            avatar.SetBounds(Scale(8), Math.Max(0, (card.Height - Scale(48)) / 2), Scale(48), Scale(48));
            var right = Math.Max(Scale(70), card.Width - Scale(10));
            var badgeSize = Scale(conversation.UnreadCount > 99 ? 26 : 22);
            var titleHeight = Math.Max(Math.Max(Scale(24), name.Font.Height + Scale(2)),
                conversation.UnreadCount > 0 ? badgeSize : 0);
            var previewHeight = Math.Max(Scale(22), Math.Max(preview.Font.Height, time.Font.Height) + Scale(2));
            var top = Math.Max(Scale(4), (card.Height - titleHeight - previewHeight) / 2);
            var clockWidth = Math.Max(Scale(42), TextRenderer.MeasureText("00:00", time.Font,
                Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Scale(4));
            // The clock belongs to the preview row, not to the title/date row.
            // Reserve separate slots so a long title or preview cannot cover it.
            time.SetBounds(right - clockWidth, top + titleHeight, clockWidth, previewHeight);
            unread.SetBounds(right - badgeSize, top + (titleHeight - badgeSize) / 2, badgeSize, badgeSize);
            // Direct sibling labels avoid a transparent rectangular HWND above
            // the right slots (and reverse-order DrawToBitmap occlusion).
            var textWidth = Math.Max(1, right - Scale(68));
            name.SetBounds(Scale(68), top, Math.Max(1, textWidth - (conversation.UnreadCount > 0 ? badgeSize + Scale(8) : 0)), titleHeight);
            preview.SetBounds(Scale(68), top + titleHeight, Math.Max(1, textWidth - (time.Text.Length > 0 ? clockWidth + Scale(8) : 0)), previewHeight);
        }
        card.Layout += (_, _) => LayoutCard();
        name.FontChanged += (_, _) => card.PerformLayout();
        preview.FontChanged += (_, _) => card.PerformLayout();
        time.FontChanged += (_, _) => card.PerformLayout();
        LayoutCard();
        card.Paint += (_, e) =>
        {
            if (ReferenceEquals(card, _selectedConversationCard)) return;
            using var separator = new Pen(Color.FromArgb(28, 34, 51));
            e.Graphics.DrawLine(separator, Scale(68), card.Height - 1, Math.Max(Scale(68), card.Width - Scale(8)), card.Height - 1);
        };
        ConfigureConversationPersonalIndicators(card, conversation);
        AttachClick(card, async () => await SelectConversationAsync((ConversationSummary)card.Tag!, card));
        AttachHover(card, card);
        AddConversationActions(card);
        return card;
    }

    private static string ConversationTime(DateTimeOffset? timestamp)
    {
        // Sidebar clocks always describe the last message's local sending time.
        // Full dates for last-seen presence are intentionally handled elsewhere.
        return timestamp?.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static void SetConversationCardSelected(RoundedPanel card, bool selected)
    {
        card.FillColor = selected ? Color.FromArgb(31, 38, 66) : Theme.Sidebar;
        card.GradientEndColor = selected ? Color.FromArgb(37, 34, 75) : Color.Empty;
        card.BorderColor = selected ? Color.FromArgb(79, 83, 143) : Color.Transparent;
        card.AccentGlow = false;
        card.Invalidate(true);
    }

    private static void AttachClick(Control control, Func<Task> action)
    {
        control.Click += async (_, _) => await action();
        foreach (Control child in control.Controls) AttachClick(child, action);
    }

    private void AttachHover(Control control, RoundedPanel card)
    {
        control.MouseEnter += (_, _) =>
        {
            if (!ReferenceEquals(card, _selectedConversationCard)) card.FillColor = Color.FromArgb(21, 27, 44);
        };
        control.MouseLeave += (_, _) =>
        {
            var cursor = card.PointToClient(Cursor.Position);
            if (!card.ClientRectangle.Contains(cursor) && !ReferenceEquals(card, _selectedConversationCard))
                SetConversationCardSelected(card, false);
        };
        foreach (Control child in control.Controls) AttachHover(child, card);
    }

    private void ResizeConversationCards()
    {
        foreach (Control control in _conversationList.Controls)
            control.Width = Math.Max((int)Math.Round(180 * DeviceDpi / 96f),
                _conversationList.ClientSize.Width - _conversationList.Padding.Horizontal - (int)Math.Round(2 * DeviceDpi / 96f));
    }

    private async Task SelectConversationAsync(ConversationSummary conversation, RoundedPanel card, bool silent = false)
    {
        if (_selectedConversation?.Id != conversation.Id)
        {
            CloseComposerEmojis();
            CloseMessageInfo();
            ResetPinnedMessages();
            _conversationVersion++;
            CancelHistoryLoad();
            _historyLoadedConversation = null;
            ClearPendingImage();
            ClearPendingFile();
            ClearPendingVoice();
            _renderFingerprint = null;
            _presenceFingerprint = null;
            ClearRenderedMessageCache();
            // If loading the new room fails, never leave the prior room's messages
            // under the newly selected title. Its next successful poll replaces this.
            foreach (Control row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
            _messageList.Controls.Clear();
            _messageList.Controls.Add(CreateEmptyState("Mesajlar yükleniyor..."));
        }
        if (_selectedConversationCard is { IsDisposed: false })
        {
            SetConversationCardSelected(_selectedConversationCard, false);
        }
        _selectedConversationCard = card;
        SetConversationCardSelected(card, true);
        _selectedConversation = conversation;
        RefreshEncryptionRecipientStatus();
        SetConversationControlsEnabled(true);
        _headerAvatar.Visible = _securityStatus.Visible = true;
        _conversationTitle.Text = conversation.Title;
        _conversationMeta.Text = string.Join(" · ", conversation.Participants.Select(user => user.DisplayName));
        _conversationMeta.ForeColor = Theme.Muted;
        _headerAvatar.Initials = Initials(conversation.Title);
        _headerAvatar.AvatarColor = Theme.Accent;
        _headerAvatar.AccessibleName = conversation.Kind == "direct" ? "Kullanıcı fotoğrafı" : "Grup fotoğrafını düzenle";
        _headerAvatar.Cursor = conversation.Kind == "direct" ? Cursors.Default : Cursors.Hand;
        _ = ApplyConversationAvatarAsync(_headerAvatar, conversation);
        var version = _conversationVersion;
        await RefreshMessagesAsync(silent);
        if (!_snapshotMode && version == _conversationVersion && !IsDisposed) _ = RefreshPinnedMessagesAsync(force: true);
        if (version == _conversationVersion && !IsDisposed) await RefreshPresenceAsync();
    }

    private async Task RefreshMessagesAsync(bool silent)
    {
        // Same-room ticks share one load. A different selection cancels the old
        // load, so this guard cannot strand a new room behind a slow old request.
        if (_refreshing) return;
        if (_selectedConversation is null || _session is null) return;
        _refreshing = true;
        var conversationId = _selectedConversation.Id;
        var version = _conversationVersion;
        using var load = new CancellationTokenSource(_historyLoadTimeout);
        _historyLoadCts = load;
        try
        {
            var messages = await _api.GetMessagesAsync(conversationId, load.Token);
            if (version != _conversationVersion || IsDisposed) return;
            load.Token.ThrowIfCancellationRequested();
            if (messages.Any(message => message.ConversationId != conversationId))
                throw new InvalidDataException("Sunucu farklı bir sohbete ait geçmiş döndürdü.");
            var profiles = string.Join('|', _selectedConversation.Participants.Select(user =>
                $"{user.Id}:{user.DisplayName}:{user.Role}:{user.PhotoVersion}:{GroupRole(user.Id)}"));
            var fingerprint = profiles + ":" + string.Join('|', messages.Select(message =>
                $"{message.Id:N}:{message.DeletedForEveryone}:{message.Payloads.Count}:{message.ExpiresAt?.ToUnixTimeMilliseconds()}"));
            var messageKeys = messages.ToDictionary(message => message.Id, MessageKeyFor);
            if (MessagePopupOpen() || string.Equals(_renderFingerprint, fingerprint, StringComparison.Ordinal) && MessageRowsMatch(messageKeys))
            {
                // Status-only updates must not recreate rows, reset scroll or stop voice playback.
                var byId = messages.ToDictionary(m => m.Id);
                foreach (var row in _messageList.Controls.OfType<MessageRow>())
                    if (byId.TryGetValue(row.MessageId, out var message)) row.UpdateDelivery(message.Delivery);
                RefreshOpenMessageInfo(messages);
                UpdateConversationPreviewFromHistory(conversationId, messages);
                UpdatePinnedMessageBanner();
                return;
            }

            var rendered = new List<Control>();
            var created = new List<Control>();
            var nextRows = new Dictionary<Guid, RenderedMessageRow>();
            var nextDates = new Dictionary<(DateTime Date, int Order), MessageDateDivider>();
            var unavailableSenders = new Dictionary<Guid, Exception>();
            var refreshedKeys = new HashSet<Guid>();
            var uiSlice = System.Diagnostics.Stopwatch.StartNew();
            var sliceRows = 0;
            var processedRows = 0;
            try
            {
                if (messages.Count == 0)
                {
                    var empty = CreateEmptyState();
                    rendered.Add(empty);
                    created.Add(empty);
                }
                else
                {
                    DateTime? previousDate = null;
                    var dateOrder = 0;
                    foreach (var message in messages)
                    {
                        load.Token.ThrowIfCancellationRequested();
                        var localDate = message.CreatedAt.ToLocalTime().Date;
                        if (localDate != previousDate)
                        {
                            var dateKey = (localDate, dateOrder++);
                            if (!_renderedDateDividers.TryGetValue(dateKey, out var divider) ||
                                divider.IsDisposed || divider.Parent != _messageList)
                            {
                                divider = new MessageDateDivider(localDate);
                                created.Add(divider);
                            }
                            rendered.Add(divider);
                            nextDates[dateKey] = divider;
                            previousDate = localDate;
                        }
                        var key = messageKeys[message.Id];
                        if (_renderedMessageRows.TryGetValue(message.Id, out var cached) &&
                            !cached.Row.IsDisposed && cached.Row.Parent == _messageList && cached.Key == key)
                        {
                            cached.Row.UpdateDelivery(message.Delivery);
                            rendered.Add(cached.Row);
                            nextRows[message.Id] = cached;
                        }
                        else
                        {
                            Control row;
                            if (!message.DeletedForEveryone && unavailableSenders.ContainsKey(message.SenderId))
                                row = CreatePendingKeyRow(message);
                            else
                            {
                                try
                                {
                                    row = await CreateMessageBubbleAsync(message, load.Token, refreshedKeys);
                                    nextRows[message.Id] = new RenderedMessageRow((MessageRow)row, key);
                                }
                                catch (Exception exception) when (exception is ChatTransportException ||
                                    exception is ChatApiException { StatusCode: not System.Net.HttpStatusCode.Unauthorized })
                                {
                                    // One missing sender must not hide everyone else's history.
                                    // Remember failures for this batch to avoid N identical key GETs.
                                    unavailableSenders[message.SenderId] = exception;
                                    row = CreatePendingKeyRow(message);
                                }
                            }
                            _messageRowsCreated++;
                            rendered.Add(row);
                            created.Add(row);
                        }
                        _historyProgressObserverForQa?.Invoke(++processedRows);
                        if (++sliceRows >= 8 || uiSlice.ElapsedMilliseconds >= 12)
                        {
                            // All controls remain on the UI thread, but large first loads
                            // yield between small batches so input/paint/timers can run.
                            // No message content is truncated or silently skipped.
                            _historyUiYieldCount++;
                            await Task.Yield();
                            load.Token.ThrowIfCancellationRequested();
                            sliceRows = 0;
                            uiSlice.Restart();
                        }
                        if (version != _conversationVersion) break;
                    }
                }
                // Keep the final deadline check in the owned batch's cleanup scope:
                // cancellation here must dispose newly decoded rows/media as well.
                load.Token.ThrowIfCancellationRequested();
            }
            catch
            {
                foreach (var control in created) control.Dispose();
                throw;
            }
            if (version != _conversationVersion || IsDisposed)
            {
                foreach (var control in created) control.Dispose();
                return;
            }
            // A key GET or the cooperative UI slice above can let the user open
            // an existing row's popup after our initial visibility check. Check
            // again at the atomic commit boundary, before disposing any owner.
            if (MessagePopupOpen())
            {
                foreach (var control in created) control.Dispose();
                RefreshOpenMessageInfo(messages);
                return;
            }
            var scrollY = -_messageList.AutoScrollPosition.Y;
            var keepAtBottom = !silent || _messageList.Controls.Count == 0 ||
                _messageList.MaximumOffset - scrollY <= 60;
            _messageList.SuspendLayout();
            try
            {
                var previous = _messageList.Controls.Cast<Control>().ToArray();
                var retained = rendered.ToHashSet();
                foreach (var control in previous.Where(control => !retained.Contains(control)))
                {
                    _messageList.Controls.Remove(control);
                    control.Dispose();
                }
                for (var index = 0; index < rendered.Count; index++)
                {
                    var control = rendered[index];
                    if (control.Parent != _messageList) _messageList.Controls.Add(control);
                    if (_messageList.Controls.GetChildIndex(control) != index)
                        _messageList.Controls.SetChildIndex(control, index);
                }
                ResizeBubbles();
            }
            finally
            {
                _messageList.ResumeLayout();
            }
            _historyLoadedConversation = conversationId;
            _renderedMessageRows.Clear();
            foreach (var pair in nextRows) _renderedMessageRows.Add(pair.Key, pair.Value);
            _renderedDateDividers.Clear();
            foreach (var pair in nextDates) _renderedDateDividers.Add(pair.Key, pair.Value);
            UpdateConversationPreviewFromHistory(conversationId, messages);
            // A pending key row is retryable, not a successfully cached decrypt.
            _renderFingerprint = unavailableSenders.Count == 0 ? fingerprint : null;
            if (unavailableSenders.Values.FirstOrDefault() is { } keyFailure) RecordNetworkFailure(keyFailure);
            // ScrollControlIntoView aligns oversized messages at their top. Latest-message
            // mode needs the true bottom, including margins, even for a multi-page reply.
            if (keepAtBottom && messages.Count > 0 && _messageList.Controls.Count > 0)
                _messageList.ScrollToOffset(_messageList.MaximumOffset);
            else _messageList.AutoScrollPosition = new Point(0, scrollY);
            RefreshOpenMessageInfo(messages);
            UpdatePinnedMessageBanner();
        }
        catch (Exception exception)
        {
            // Canceled/late work for a prior selection must never replace the new
            // room's rows, error state or reconnect deadline.
            if (version != _conversationVersion || IsDisposed) return;
            if (exception is OperationCanceledException && load.IsCancellationRequested)
                exception = new ChatTransportException("message_history", true, exception);
            RecordNetworkFailure(exception);
            ShowHistoryLoadFailure(conversationId, exception);
        }
        finally
        {
            if (ReferenceEquals(_historyLoadCts, load))
            {
                _historyLoadCts = null;
                _refreshing = false;
            }
        }
    }

    private Control CreateEmptyState(string text = "Henüz mesaj yok", Func<Task>? retry = null)
    {
        var container = new ReferenceSurface
        {
            Tag = "empty",
            Width = Math.Max(200, _messageList.ClientSize.Width - 64),
            Height = Math.Max(300, _messageList.ClientSize.Height - 58),
            Margin = Padding.Empty
        };
        var content = new ReferenceSurface { Size = new Size(260, retry is null ? 108 : 166) };
        var avatar = new AvatarView
        {
            Initials = "M",
            AvatarColor = Theme.SurfaceRaised,
            Size = new Size(62, 62),
            Location = new Point(99, 0)
        };
        var label = new Label
        {
            Text = text,
            Bounds = new Rectangle(0, 76, 260, 26),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.Font(9f, FontStyle.Bold)
        };
        content.Controls.Add(label);
        content.Controls.Add(avatar);
        if (retry is not null)
        {
            var button = Theme.Button("Yeniden dene", ButtonKind.Secondary);
            button.Bounds = new Rectangle(50, 118, 160, 38);
            button.Click += async (_, _) =>
            {
                button.Enabled = false;
                try { await retry(); }
                finally { if (!button.IsDisposed) button.Enabled = true; }
            };
            content.Controls.Add(button);
        }
        container.Controls.Add(content);
        void CenterContent() => content.Location = new Point((container.ClientSize.Width - content.Width) / 2, (container.ClientSize.Height - content.Height) / 2);
        container.Resize += (_, _) => CenterContent();
        CenterContent();
        return container;
    }

    private async Task<Control> CreateMessageBubbleAsync(StoredMessage message, CancellationToken cancellationToken = default,
        HashSet<Guid>? refreshedKeys = null)
    {
        string displayText;
        var decrypted = false;
        byte[]? imageBytes = null;
        byte[]? voiceBytes = null;
        EncryptedFileDescriptor? file = null;
        if (message.DeletedForEveryone)
        {
            displayText = "Mesaj silindi";
        }
        else
        {
            try
            {
                var payload = message.Payloads.FirstOrDefault(item => item.RecipientId == _session!.User.Id)
                    ?? throw new InvalidOperationException("Bu cihaz için şifreli zarf bulunamadı.");
                var plaintext = await DecryptWithKeyRefreshAsync(message, payload, cancellationToken, refreshedKeys);
                decrypted = true;
                if (message.Kind == "file")
                {
                    try
                    {
                        file = System.Text.Json.JsonSerializer.Deserialize<EncryptedFileDescriptor>(plaintext)
                            ?? throw new InvalidDataException("Dosya bilgisi boş.");
                        if (message.Attachment?.StorageToken != file.StorageToken || file.Size is < 1 or > FileCryptography.MaxFileBytes)
                            throw new InvalidDataException("Dosya bilgisi uyuşmuyor.");
                        displayText = $"▤  {FileCryptography.SafeName(file.FileName)}\n{file.Size / 1024d:0.#} KB";
                    }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                }
                else if (message.Kind.StartsWith("image/", StringComparison.Ordinal))
                {
                    imageBytes = plaintext;
                    displayText = "";
                }
                else if (message.Kind == "audio/wav")
                {
                    voiceBytes = plaintext;
                    displayText = "";
                }
                else
                {
                    displayText = Encoding.UTF8.GetString(plaintext);
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (ChatTransportException) { throw; }
            catch (ChatApiException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch
            {
                decrypted = false; file = null;
                displayText = "Mesaj açılamadı";
            }
        }

        var row = (MessageRow)BuildMessageBubble(message, displayText, imageBytes, voiceBytes);
        row.CanMarkRead &= decrypted;
        row.CanStar &= decrypted;
        row.CanPin &= decrypted;
        if (!decrypted) row.SetStarred(false);
        if (file is not null) row.SetFileAction(() => SaveReceivedFileAsync(message, file));
        return row;
    }

    private Control BuildMessageBubble(StoredMessage message, string displayText, byte[]? imageBytes, byte[]? voiceBytes = null)
    {
        var mine = message.SenderId == _session!.User.Id;
        var sender = mine ? _session.User : _selectedConversation?.Participants.FirstOrDefault(u => u.Id == message.SenderId)
            ?? new ChatUser(message.SenderId, "Kullanıcı", "", false, null);
        var row = new MessageRow(sender, mine, message, displayText, imageBytes, voiceBytes);
        row.SetGroupRole(GroupRole(sender.Id));
        _ = _avatars.ApplyAsync(row.Avatar, sender);
        var menu = Theme.ContextMenu();
        row.SetStarred(!message.DeletedForEveryone && ChatPreferences()?.IsStarred(message.ConversationId, message.Id) == true);
        if (!message.DeletedForEveryone)
        {
            var star = menu.Items.Add("Yıldızla", null, (_, _) => ToggleMessageStar(row));
            menu.Opening += (_, _) => { star.Text = row.IsStarred ? "Yıldızı kaldır" : "Yıldızla"; star.Enabled = row.CanStar; };
            AddMessagePinAction(menu, row);
        }
        if (message.Kind == "text" && !message.DeletedForEveryone && !string.IsNullOrEmpty(displayText))
            menu.Items.Add("Metni kopyala", null, (_, _) => Clipboard.SetText(displayText));
        menu.Items.Add("Benden sil", null, async (_, _) => await DeleteAsync(message.Id, false)).ForeColor = Theme.Danger;
        if (mine && !message.DeletedForEveryone)
        {
            menu.Items.Add("Mesaj bilgisi", null, (_, _) => ShowMessageInfo(message, row));
            var everyone = menu.Items.Add("Herkesten sil", null, async (_, _) => await DeleteAsync(message.Id, true));
            everyone.ForeColor = Theme.Danger;
            // Re-evaluate when opening: a menu created before the deadline must not
            // continue offering the action after 15 minutes. The server decides finally.
            menu.Opening += (_, _) => everyone.Visible = CanDeleteForEveryone(message, _session?.User.Id, DateTimeOffset.UtcNow);
        }
        row.SetMessageMenu(menu);
        return row;
    }

    internal void PopulateSnapshot(bool admin = true)
    {
        var me = new ChatUser(Guid.NewGuid(), "MTK Demo", "demo@mtkaya.me", false, null, admin ? "admin" : "user");
        var gemini = new ChatUser(Guid.NewGuid(), "Gemini Agent", "", true, null, "agent");
        var groq = new ChatUser(Guid.NewGuid(), "Groq Agent", "", true, null, "agent");
        var offline = new ChatUser(Guid.NewGuid(), "Ayşe Demir", "ayse@mtkaya.me", false, null, "user");
        _session = new LoginResponse("", me);
        RefreshRailAccount(me);
        var now = DateTimeOffset.Now;
        var conversation = new ConversationSummary(Guid.NewGuid(), "MTK Lounge", new[] { me, gemini, groq, offline }, "", now, 0);
        var card = CreateConversationCard(conversation);
        _conversationList.Controls.Add(card);
        // Illustrative local fixtures only: no real identity/photo or live API lookup.
        var zeynep = new ChatUser(Guid.NewGuid(), "Zeynep Kaya", "zeynep@example.invalid", false, null);
        var ahmet = new ChatUser(Guid.NewGuid(), "Ahmet Yılmaz", "ahmet@example.invalid", false, null);
        var elif = new ChatUser(Guid.NewGuid(), "Elif Demir", "elif@example.invalid", false, null);
        var kerem = new ChatUser(Guid.NewGuid(), "Kerem Arslan", "kerem@example.invalid", false, null);
        foreach (var sample in new[]
        {
            new ConversationSummary(Guid.NewGuid(), zeynep.DisplayName, new[] { me, zeynep }, "", now.AddMinutes(-8), 2, Kind: "direct"),
            new ConversationSummary(Guid.NewGuid(), ahmet.DisplayName, new[] { me, ahmet }, "", now.AddMinutes(-36), 1, Kind: "direct"),
            new ConversationSummary(Guid.NewGuid(), "MTK Ekibi", new[] { me, offline, kerem }, "", now.AddHours(-2), 12),
            new ConversationSummary(Guid.NewGuid(), elif.DisplayName, new[] { me, elif }, "", now.AddDays(-1), 0, Kind: "direct"),
            new ConversationSummary(Guid.NewGuid(), kerem.DisplayName, new[] { me, kerem }, "", now.AddDays(-3), 0, Kind: "direct")
        }) _conversationList.Controls.Add(CreateConversationCard(sample));
        _selectedConversation = conversation;
        _selectedConversationCard = card;
        RefreshEncryptionRecipientStatus();
        SetConversationCardSelected(card, true);
        _conversationTitle.Text = conversation.Title;
        _conversationMeta.Text = "MTK Demo · Gemini Agent · Groq Agent";
        _headerAvatar.Initials = "ML";
        _headerAvatar.AvatarColor = Theme.Accent;

        StoredMessage Sample(Guid sender) => new(Guid.NewGuid(), Guid.NewGuid(), conversation.Id, sender, "text", now, null, false,
            Array.Empty<EncryptedPayload>(), null);
        _messageList.Controls.Add(new MessageDateDivider(now.ToLocalTime().Date));
        _messageList.Controls.Add(BuildMessageBubble(Sample(gemini.Id), "Bir fikirle başlayalım: bu hafta birlikte ne üretmek istersiniz?", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(me.Id), "İnsanların ve yapay zekânın aynı masada buluştuğu bir alan. Daha az kalabalık, daha çok iyi fikir.", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(groq.Id), "Güzel başlangıç. İlk fikri küçük bir prototipe dönüştürelim; sonra birlikte geliştirebiliriz.", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(offline.Id), "Ben de buradayım. Taslakları burada paylaşabiliriz ✨", null));
        RenderPresence(new[]
        {
            new PresenceView(me, true, true, true, now),
            new PresenceView(gemini, false, true, false, null),
            new PresenceView(groq, false, true, false, null),
            new PresenceView(offline, true, true, true, now)
        });
        ResizeConversationCards();
        ResizeBubbles();
        // Match live RefreshMessagesAsync's bottom-pinned view, including tall/DPI-scaled rows.
        _messageList.ScrollToOffset(_messageList.MaximumOffset);
    }

    internal void PopulateReferenceSnapshot(bool direct = true, bool admin = true)
    {
        PopulateSnapshot(admin);
        if (!direct || _session is null) return;
        var card = _conversationList.Controls.OfType<RoundedPanel>()
            .First(c => ((ConversationSummary)c.Tag!).Kind == "direct");
        var conversation = (ConversationSummary)card.Tag!;
        if (_selectedConversationCard is not null) SetConversationCardSelected(_selectedConversationCard, false);
        _selectedConversation = conversation;
        _selectedConversationCard = card;
        RefreshEncryptionRecipientStatus();
        SetConversationCardSelected(card, true);
        _conversationTitle.Text = conversation.Title;
        _conversationMeta.Text = "Çevrimiçi";
        _conversationMeta.ForeColor = Theme.Success;
        var peer = conversation.Participants.Single(user => user.Id != _session.User.Id);
        _headerAvatar.Initials = Initials(peer.DisplayName);
        _headerAvatar.AvatarColor = Theme.SurfaceHover;
        _headerAvatar.Cursor = Cursors.Default;
        _headerAvatar.AccessibleName = "Kullanıcı fotoğrafı";
        foreach (Control old in _messageList.Controls.Cast<Control>().ToArray()) old.Dispose();
        var now = DateTimeOffset.Now;
        StoredMessage Sample(Guid sender, int minutesAgo) => new(Guid.NewGuid(), Guid.NewGuid(), conversation.Id,
            sender, "text", now.AddMinutes(-minutesAgo), null, false, Array.Empty<EncryptedPayload>(), null,
            DeleteForEveryoneUntil: now.AddMinutes(15 - minutesAgo));
        _messageList.Controls.Add(new MessageDateDivider(now.ToLocalTime().Date));
        _messageList.Controls.Add(BuildMessageBubble(Sample(peer.Id, 12), "Merhaba! Nasılsın? Uzun zamandır konuşamadık 😊", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(_session.User.Id, 11), "Merhaba! İyiyim, teşekkür ederim. Sen nasılsın?", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(peer.Id, 10), "Ben de iyiyim. Yeni projede işler nasıl gidiyor?\nHer şey yolunda mı?", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(_session.User.Id, 8), "Oldukça iyi gidiyor! Arayüz taslaklarını bitirdik. Şimdi geliştirme aşamasındayız. 🚀", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(peer.Id, 5), "Harika görünüyor! 👏\nRenkler ve tasarım gerçekten çok iyi. Emeğinize sağlık.", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(_session.User.Id, 2), "Teşekkür ederim! Yakında seninle detayları da paylaşırım.", null));
        _messageList.Controls.Add(BuildMessageBubble(Sample(peer.Id, 1), "Tamamdır, görüşürüz! 👋", null));
        RenderPresence(new[]
        {
            new PresenceView(_session.User, true, true, true, now),
            new PresenceView(peer, true, true, true, now)
        });
        _conversationMeta.Text = "Çevrimiçi";
        ResizeConversationCards();
        ResizeBubbles();
        _messageList.ScrollToOffset(_messageList.MaximumOffset);
    }

    private static string Initials(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "?";
        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    private async Task<byte[]> DecryptWithKeyRefreshAsync(StoredMessage message, EncryptedPayload payload,
        CancellationToken cancellationToken = default, HashSet<Guid>? refreshedKeys = null)
    {
        var senderDevice = await GetDeviceAsync(message.SenderId, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Gönderen cihaz anahtarı bulunamadı.");
        try
        {
            return Decrypt(message, payload, senderDevice.SigningPublicKey);
        }
        catch (CryptographicException)
        {
            // The same refreshed key cannot repair N old invalid envelopes; only
            // one forced refresh per sender is useful during a history batch.
            if (refreshedKeys is not null && !refreshedKeys.Add(message.SenderId)) throw;
            senderDevice = await GetDeviceAsync(message.SenderId, forceRefresh: true, cancellationToken)
                ?? throw new InvalidOperationException("Gönderen cihaz anahtarı yenilenemedi.");
            return Decrypt(message, payload, senderDevice.SigningPublicKey);
        }
    }

    private byte[] Decrypt(StoredMessage message, EncryptedPayload payload, string signingPublicKey) =>
        MessageCryptography.Decrypt(payload, message.ClientMessageId, message.ConversationId, message.SenderId,
            message.CreatedAt, _identity.EncryptionKey, signingPublicKey);

    private async Task<DeviceKeyBundle?> GetDeviceAsync(Guid userId, bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceRefresh && _deviceCache.TryGetValue(userId, out var cached)) return cached;
        var device = await _api.TryGetDeviceAsync(userId, cancellationToken);
        if (device is not null) _deviceCache[userId] = device;
        return device;
    }

    private async Task SendTextAsync()
    {
        var text = _premiumComposer.Text.Trim();
        var image = _pendingImage;
        var voice = _pendingVoice;
        var file = _pendingFile;
        if ((text.Length == 0 && image is null && voice is null && file is null) || _sending || _voiceRecorder is not null || _voiceStopping) return;
        _sending = true;
        _premiumComposer.Enabled = false;
        try
        {
            var conversationId = _selectedConversation?.Id;
            if (file is not null)
            {
                if (!await SendFileAsync(file)) return;
                if (ReferenceEquals(_pendingFile, file)) ClearPendingFile();
            }
            if (image is not null)
            {
                // SendBytesAsync clears its input; keep the staged image intact on failure.
                if (!await SendBytesAsync(image.Bytes.ToArray(), image.MimeType)) return;
                if (ReferenceEquals(_pendingImage, image)) ClearPendingImage();
            }
            if (voice is not null)
            {
                if (!await SendBytesAsync(voice.Bytes.ToArray(), "audio/wav")) return;
                if (ReferenceEquals(_pendingVoice, voice)) ClearPendingVoice();
            }
            if (_selectedConversation?.Id != conversationId || text.Length == 0) return;
            if (await SendBytesAsync(Encoding.UTF8.GetBytes(text), "text") && _premiumComposer.Text.Trim() == text)
                _premiumComposer.Text = "";
        }
        finally
        {
            _sending = false;
            // A pending send can finish after the window closes. Releasing its
            // logical busy state is safe, but a disposed DevExpress editor must
            // not be re-enabled/focused from the late continuation.
            if (!IsDisposed && !Disposing && !_premiumComposer.IsDisposed)
            {
                _premiumComposer.Enabled = true;
                _premiumComposer.Focus();
            }
        }
    }

    private async Task PickImageAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Görsel seç",
            Filter = "Görseller|*.png;*.jpg;*.jpeg;*.webp;*.gif",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var info = new FileInfo(picker.FileName);
        if (info.Length > 5 * 1024 * 1024)
        {
            ShowError("Görsel en fazla 5 MB olabilir.");
            return;
        }
        var mimeType = Path.GetExtension(picker.FileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        try
        {
            StageImage(await File.ReadAllBytesAsync(picker.FileName), mimeType, info.Name);
        }
        catch (Exception exception)
        {
            ShowError($"Görsel açılamadı: {exception.Message}");
        }
    }

    private async Task<bool> SendBytesAsync(byte[] bytes, string kind)
    {
        try
        {
            if (_selectedConversation is null || _session is null) return false;
            var conversationId = _selectedConversation.Id;
            // Encryption and diagnostics belong to the initiating send, not a
            // different room/account selected while network requests complete.
            var accountId = _session.User.Id;
            var selectionVersion = _conversationVersion;
            var expiry = _selectedExpiry;
            var clientMessageId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var members = await _api.GetMembersAsync(conversationId);
            var payloads = new List<EncryptedPayload>();
            var unavailable = new List<string>();
            var unavailableUsers = new List<ChatUser>();
            foreach (var member in members)
            {
                var device = await GetDeviceAsync(member.Id, forceRefresh: member.IsAgent);
                if (IsDisposed || _session?.User.Id != accountId) return false;
                if (device is null)
                {
                    unavailable.Add(member.DisplayName);
                    unavailableUsers.Add(member);
                    continue;
                }
                payloads.Add(MessageCryptography.Encrypt(bytes, clientMessageId, conversationId,
                    accountId, member.Id, createdAt, device.EncryptionPublicKey, _identity.SigningKey));
            }
            RecordRecipientKeyStatus(accountId, conversationId, selectionVersion, unavailableUsers, sendAccepted: false);
            if (unavailable.Count > 0 && conversationId != Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
                throw new InvalidOperationException($"Mesaj gönderilmedi. {string.Join(", ", unavailable)} önce uygulamaya bir kez giriş yapmalı; şifreleme için cihaz anahtarı gerekiyor.");
            if (payloads.Count == 0) throw new InvalidOperationException("Katılımcı anahtarı bulunamadı.");
            DateTimeOffset? expires = expiry.Duration is { } duration
                ? createdAt.Add(duration)
                : null;
            if (IsDisposed || _session?.User.Id != accountId) return false;
            await _api.SendMessageAsync(new SendMessageRequest(clientMessageId, conversationId, kind,
                createdAt, expires, payloads, null));
            RecordRecipientKeyStatus(accountId, conversationId, selectionVersion, unavailableUsers, sendAccepted: true);
            // POST acceptance is already confirmed. A subsequent read outage must
            // not look like a failed send or encourage a duplicate resend.
            if (_selectedConversation?.Id == conversationId) await RefreshMessagesAsync(silent: true);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task DeleteAsync(Guid messageId, bool forEveryone)
    {
        var roomId = _messageList.Controls.OfType<MessageRow>().FirstOrDefault(r => r.MessageId == messageId)?.ConversationId;
        try
        {
            await _api.DeleteMessageAsync(messageId, forEveryone);
            if (IsDisposed) return;
            if (roomId is not null) InvalidateConversationVisibility(roomId);
            await RefreshMessagesAsync(silent: false);
            await LoadConversationsAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void ShowExpiryMenu()
    {
        // The popup is owned by the expiry button. Disposing in Closed reenters
        // WinForms' native dropdown teardown and can crash an otherwise idle app.
        var menu = GetExpiryMenu();
        menu.Show(_premiumExpiryButton, new Point(0, -menu.PreferredSize.Height));
    }

    private void ResizeBubbles()
    {
        if (_resizingBubbles || IsDisposed) return;
        var width = Math.Max(200, _messageList.ViewportWidth - _messageList.Padding.Horizontal - 2);
        var emptyHeight = Math.Max(300, _messageList.ClientSize.Height - 58);
        if (_bubbleLayoutRevision == _messageLayoutRevision && _bubbleLayoutWidth == width && _bubbleLayoutHeight == emptyHeight) return;
        _bubbleLayoutRevision = _messageLayoutRevision;
        _bubbleLayoutWidth = width;
        _bubbleLayoutHeight = emptyHeight;
        _bubbleResizePasses++;
        _resizingBubbles = true;
        _messageList.SuspendLayout();
        try
        {
            foreach (Control container in _messageList.Controls)
            {
                if (container.Width != width) container.Width = width;
                if (Equals(container.Tag, "empty") && container.Height != emptyHeight)
                    container.Height = emptyHeight;
                if (container.Controls.Count > 0 && container.Controls[0] is RoundedPanel bubble && bubble.Anchor.HasFlag(AnchorStyles.Right))
                    bubble.Left = container.Width - bubble.Width;
            }
        }
        finally { _messageList.ResumeLayout(); _resizingBubbles = false; }
    }

    private async Task OpenGroupPhotoAsync(ConversationSummary? target = null)
    {
        var conversation = target ?? _selectedConversation;
        if (conversation is null || _session is null || conversation.Kind == "direct") return;
        using var dialog = new GroupPhotoForm(_api, conversation, _avatars);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.UpdatedPhoto is not { } result) return;
        if (_selectedConversation?.Id == result.ConversationId)
        {
            _selectedConversation = _selectedConversation with { PhotoVersion = result.PhotoVersion };
            _ = _avatars.ApplyGroupAsync(_headerAvatar, _selectedConversation);
        }
        foreach (Control control in _conversationList.Controls)
        {
            if (control.Tag is not ConversationSummary group || group.Id != result.ConversationId) continue;
            var updated = group with { PhotoVersion = result.PhotoVersion };
            control.Tag = updated;
            if (control.Controls.OfType<AvatarView>().FirstOrDefault() is { } avatar)
                _ = _avatars.ApplyGroupAsync(avatar, updated);
        }
        await RefreshConversationPhotosAsync();
    }

    private async Task RefreshConversationPhotosAsync()
    {
        if (_session is null || IsDisposed) return;
        try
        {
            var latest = await _api.GetConversationsAsync();
            if (IsDisposed) return;
            foreach (Control control in _conversationList.Controls)
            {
                if (control.Tag is not ConversationSummary prior) continue;
                var current = latest.FirstOrDefault(item => item.Id == prior.Id);
                if (current is null || current.PhotoVersion == prior.PhotoVersion) continue;
                control.Tag = current;
                if (control.Controls.OfType<AvatarView>().FirstOrDefault() is { } avatar)
                    _ = _avatars.ApplyGroupAsync(avatar, current);
                if (_selectedConversation?.Id == current.Id)
                {
                    _selectedConversation = current;
                    _ = _avatars.ApplyGroupAsync(_headerAvatar, current);
                }
            }
        }
        catch (Exception exception) { RecordNetworkFailure(exception); }
    }

    private Action<string>? _errorObserverForQa;
    private void ShowError(string message)
    {
        if (IsDisposed) return;
        if (_errorObserverForQa is { } observer) { observer(message); return; }
        MessageBox.Show(this, message, "MTK Chat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeComposerEmojis();
            DisposeChatNotifications();
            _sidebarStatuses?.Dispose();
            _sidebarBlocked?.Dispose();
            _sidebarProfileEditor?.Dispose();
            _previewLifetime.Cancel();
            _conversationPreviews.Clear();
            _previewRequests.Clear();
            CancelHistoryLoad();
            DisposePinnedMessages();
            ClearRenderedMessageCache();
            _voiceTimer.Dispose();
            ClearPendingVoice();
            _refreshTimer.Dispose();
            _readTimer.Dispose();
            _api.Dispose();
            _identity.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record ExpiryChoice(string Label, TimeSpan? Duration)
    {
        public override string ToString() => Label;
    }
}
