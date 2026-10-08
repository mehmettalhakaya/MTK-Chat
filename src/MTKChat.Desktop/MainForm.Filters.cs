using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private ConversationFilterEmptyState? _conversationFilterEmpty;
    private bool _arrangingConversationFilters;
    private bool _conversationFilterLayoutQueued;
    internal bool HasVisibleFilterEmpty => _conversationFilterEmpty is { Visible: true };

    private void SetConversationFilter(string key)
    {
        if (!_filterButtons.ContainsKey(key)) throw new ArgumentOutOfRangeException(nameof(key));
        var enteringArchive = key == "archived" && _conversationFilter != "archived";
        _conversationFilter = key;
        foreach (var entry in _filterButtons)
            entry.Value.Kind = entry.Key == key ? ButtonKind.Secondary : ButtonKind.Ghost;
        _premiumSearch.Properties.NullValuePrompt = key switch
        {
            "unread" => "Okunmamış sohbetlerde ara...",
            "favorites" => "Favori sohbetlerde ara...",
            "archived" => "Arşivlenmiş sohbetlerde ara...",
            _ => "Sohbetlerde ara..."
        };
        // Archive is a separate collection. A leftover main-list search must not
        // make its newly opened view appear empty before the user searches it.
        // Re-selecting Archive during an archive search preserves that query.
        if (enteringArchive && _premiumSearch.Text.Length != 0) _premiumSearch.Text = "";
        FilterConversations(_premiumSearch.Text);
    }

    private bool MatchesConversationFilter(ConversationSummary conversation)
    {
        var archived = IsArchivedConversation(conversation.Id);
        // Archive is a personal view, not a hidden/deleted server conversation.
        // Incoming messages may update unread metadata but never unarchive a room.
        if (_conversationFilter == "archived") return archived;
        if (archived) return false;
        return _conversationFilter switch
        {
            "unread" => conversation.UnreadCount > 0,
            "favorites" => IsFavoriteConversation(conversation.Id),
            "direct" => conversation.Kind == "direct",
            "group" => conversation.Kind != "direct",
            _ => true
        };
    }

    private void FilterConversations(string query)
    {
        // Leading/trailing spaces are not a search term. In particular an empty
        // whitespace draft must not hide every one-word conversation title.
        query = query.Trim();
        var filterCount = 0;
        var visibleCount = 0;
        var cards = _conversationList.Controls.Cast<Control>().Where(control => control.Tag is ConversationSummary).ToArray();
        UpdateArchiveFilterSummary(cards.Select(card => (ConversationSummary)card.Tag!));
        _conversationList.SuspendLayout();
        try
        {
            foreach (var card in cards)
            {
                var conversation = (ConversationSummary)card.Tag!;
                var matchesFilter = MatchesConversationFilter(conversation);
                if (matchesFilter) filterCount++;
                var matches = matchesFilter && conversation.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                if (matches) visibleCount++;
                // Visible's getter also reflects hidden ancestors. The sidebar
                // may be collapsed into Profile/Settings while polling updates
                // unread metadata; still set the child's own visibility flag so
                // nonmatching cards do not reappear when its parent is expanded.
                card.Visible = matches;
            }
        }
        finally { _conversationList.ResumeLayout(); }

        // Filtering only hides sidebar cards. It must never select another chat,
        // clear the composer, destroy message rows or acknowledge unread messages.
        if (_conversationFilterEmpty is null) return;
        var searching = !string.IsNullOrWhiteSpace(query);
        var (title, detail) = searching && filterCount > 0 ?
            ("Sohbet bulunamadı", "Farklı bir adla aramayı deneyin.") : _conversationFilter switch
            {
                "unread" => ("Okunmamış sohbet yok", "Hepsini gördünüz."),
                "favorites" => ("Henüz favori sohbet yok", "Bir sohbeti sağ tıklayıp favorilere ekleyebilirsiniz."),
                "direct" => ("Henüz kişisel sohbet yok", "Yeni Sohbet ile bir konuşma başlatın."),
                "group" => ("Henüz grup sohbeti yok", "Yeni Sohbet menüsünden bir grup oluşturun."),
                "archived" => ("Arşivlenmiş sohbet yok", "Bir sohbeti sağ tıklayıp arşivleyebilirsiniz."),
                _ => searching ? ("Sohbet bulunamadı", "Farklı bir adla aramayı deneyin.") :
                    ("Henüz sohbet yok", "Yeni Sohbet ile bir konuşma başlatın.")
            };
        _conversationFilterEmpty.Configure(title, detail, _conversationFilter == "unread" && filterCount == 0,
            _conversationFilter != "all" || searching);
        _conversationFilterEmpty.Visible = visibleCount == 0;
        if (visibleCount == 0) _conversationFilterEmpty.BringToFront();
    }

    private void ArrangeConversationFilters(Control host, RowStyle height)
    {
        if (_arrangingConversationFilters || host.IsDisposed || host.Disposing || host.ClientSize.Width < 1) return;
        _arrangingConversationFilters = true;
        try
        {
            // Match ModernButton's actual GDI drawing flags, including glyph
            // overhang. Compact spacing fits all six on one row when possible;
            // otherwise use a balanced grid, never an orphaned Archive line.
            var scale = host.DeviceDpi / 96f;
            int Pixels(int logical) => Math.Max(1, (int)Math.Round(logical * scale));
            var gap = Pixels(4);
            var rowHeight = Pixels(34);
            using var graphics = host.CreateGraphics();
            var measured = _filterButtons.Values.Select(button => (Button: button,
                Text: TextRenderer.MeasureText(graphics, button.Text, button.Font,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix))).ToArray();
            if (measured.Length == 0) return;
            rowHeight = Math.Max(rowHeight, measured.Max(item => item.Text.Height) + Pixels(12));
            var minimumWidths = measured.Select(item => Math.Max(Pixels(40), item.Text.Width + Pixels(8))).ToArray();
            var available = host.ClientSize.Width;
            int[] columnWidths = minimumWidths;
            var columns = measured.Length;
            if (minimumWidths.Sum() + gap * (columns - 1) > available)
            {
                // Six filters become 3+3, then 2+2+2, then one per row for
                // accessibility fonts. The font is never shrunk to hide a bug.
                foreach (var candidate in new[] { 3, 2, 1 })
                {
                    columns = Math.Min(candidate, measured.Length);
                    columnWidths = Enumerable.Range(0, columns).Select(column =>
                        minimumWidths.Where((_, index) => index % columns == column).Max()).ToArray();
                    if (columnWidths.Sum() + gap * (columns - 1) <= available) break;
                }
            }
            // Distribute spare pixels evenly; full-width aligned rows do not
            // look like a wrapped paragraph and the last hit target stays inside.
            var spare = Math.Max(0, available - columnWidths.Sum() - gap * (columns - 1));
            for (var column = 0; column < columns; column++)
                columnWidths[column] += spare / columns + (column < spare % columns ? 1 : 0);
            for (var index = 0; index < measured.Length; index++)
            {
                var column = index % columns;
                var x = columnWidths.Take(column).Sum() + column * gap;
                var y = index / columns * (rowHeight + gap);
                measured[index].Button.SetBounds(x, y, columnWidths[column], rowHeight);
            }
            var rows = (measured.Length + columns - 1) / columns;
            var requiredHeight = rows * rowHeight + (rows - 1) * gap + Pixels(8);
            if (Math.Abs(height.Height - requiredHeight) > .5f)
            {
                height.Height = requiredHeight;
                QueueConversationFilterLayout(host);
            }
        }
        finally { _arrangingConversationFilters = false; }
    }

    private void QueueConversationFilterLayout(Control host)
    {
        if (_conversationFilterLayoutQueued || host.IsDisposed || host.Disposing || !host.IsHandleCreated) return;
        _conversationFilterLayoutQueued = true;
        try
        {
            host.BeginInvoke((Action)(() =>
            {
                _conversationFilterLayoutQueued = false;
                if (host.IsDisposed || host.Disposing || host.Parent is null) return;
                // Width changes arrive while TableLayoutPanel is already arranging
                // this child. Its row allocation still reflects the old one-row
                // height at that point (42px), even after RowStyle became 81px.
                // Apply the new row height once outside that reentrant layout pass.
                host.Parent.PerformLayout();
                host.PerformLayout();
            }));
        }
        catch (InvalidOperationException) { _conversationFilterLayoutQueued = false; }
    }
}
