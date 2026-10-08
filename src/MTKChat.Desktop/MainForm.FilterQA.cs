using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyFilterFeatures()
    {
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        return fixture.Form.RunConversationFilterQa();
    }

    // Retain the full snapshot runner's existing entry point while extending its
    // fixture to unread/favorites and non-destructive empty-state transitions.
    internal void VerifyConversationFilters()
    {
        foreach (var check in RunConversationFilterQa()) Console.WriteLine("Conversation filter QA: " + check);
    }

    private IReadOnlyList<string> RunConversationFilterQa()
    {
        if (_selectedConversation is null) throw new InvalidOperationException("Filter fixture is missing.");
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Conversation filter QA: " + check);
            checks.Add(check);
        }
        var room = _selectedConversation;
        var ayse = room with { Id = Guid.NewGuid(), Title = "Ayşe ile sohbet", Kind = "direct", UnreadCount = 3 };
        var design = room with { Id = Guid.NewGuid(), Title = "Tasarım ekibi", Kind = "group", UnreadCount = 0 };
        _conversationList.Controls.Add(CreateConversationCard(ayse));
        _conversationList.Controls.Add(CreateConversationCard(design));
        var summaries = _conversationList.Controls.Cast<Control>().Select(control => (ConversationSummary)control.Tag!).ToArray();
        var allCount = summaries.Length;
        var groupCount = summaries.Count(conversation => conversation.Kind != "direct");
        var directCount = allCount - groupCount;
        var unreadCount = summaries.Count(conversation => conversation.UnreadCount > 0);
        var selected = _selectedConversation;
        var selectedCard = _selectedConversationCard;
        var priorDraft = _premiumComposer.Text;
        _premiumComposer.Text = "Filtre değişiminde korunacak taslak 😊";
        void Expect(int count, string check)
        {
            Application.DoEvents();
            Require(_conversationList.Controls.Cast<Control>().Count(control => control.Visible) == count, check);
            Require(_conversationFilterEmpty?.Visible == (count == 0), check + " — empty-state visibility");
        }

        void VerifyEmptyNative(string filter)
        {
            var empty = _conversationFilterEmpty!;
            var priorSize = Size;
            var output = Path.Combine(AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(output);
            IReadOnlyList<string> VerifyEmptyLayout()
            {
                try { return empty.VerifyLayout(); }
                catch (InvalidOperationException exception)
                {
                    // A clipped child is an effect, not necessarily the broken
                    // layout's cause. Keep the original assertion strict while
                    // reporting the native form and dynamic row allocation.
                    var filterHost = _filterButtons["all"].Parent!;
                    var sidebar = filterHost.Parent as TableLayoutPanel;
                    var rows = sidebar is null ? "unavailable" : string.Join(",", sidebar.GetRowHeights());
                    var styles = sidebar is null ? "unavailable" : string.Join(",", sidebar.RowStyles
                        .Cast<RowStyle>().Select(row => $"{row.SizeType}:{row.Height:0.##}"));
                    var fonts = string.Join(",", _filterButtons.Select(pair => $"{pair.Key}:{pair.Value.Font.SizeInPoints:0.##}pt"));
                    throw new InvalidOperationException($"{exception.Message}; filter={filter}; form={Size}; " +
                        $"client={ClientSize}; windowState={WindowState}; dpi={DeviceDpi}; " +
                        $"sidebar={sidebar?.Bounds}; rows=[{rows}]; styles=[{styles}]; " +
                        $"filterHost={filterHost.Bounds}; overlayHost={empty.Parent?.Bounds}; fonts=[{fonts}]", exception);
                }
            }
            try
            {
                foreach (var requested in new[] { new Size(1120, 720), new Size(1380, 860), new Size(1536, 940) })
                {
                    Size = requested; PerformLayout(); Application.DoEvents();
                    empty.Configure(empty.Title, empty.Detail, empty.AllReadArtworkForQa, showAll: true);
                    foreach (var check in VerifyEmptyLayout()) checks.Add($"{filter} {Width}×{Height}: " + check);
                    // Capture the same QA-owned native surface without its overlay
                    // to distinguish actual new text/art ink from old list pixels.
                    empty.Visible = false; Refresh(); Application.DoEvents();
                    using var baseline = CaptureInfoClient(this);
                    for (var cycle = 0; cycle < 2; cycle++)
                    {
                        // A real hide/configure/show cycle also proves that layout
                        // does not infer desired action visibility from a hidden parent.
                        empty.Configure(empty.Title, empty.Detail, empty.AllReadArtworkForQa, showAll: true);
                        empty.Visible = true; empty.BringToFront();
                        Refresh(); Application.DoEvents();
                        foreach (var check in VerifyEmptyLayout()) checks.Add($"{filter} reveal {cycle + 1}: " + check);
                        using var native = CaptureInfoClient(this);
                        foreach (Control child in empty.Controls)
                        {
                            if (!child.Visible) continue; // Compact decoration is optional, never the copy/action.
                            var bounds = new Rectangle(PointToClient(child.PointToScreen(Point.Empty)), child.ClientSize);
                            Require(ClientRectangle.Contains(bounds),
                                $"{filter} {Width}×{Height}: {child.Name} lies fully on the own-form native client");
                            var ink = 0;
                            for (var y = bounds.Top; y < bounds.Bottom; y++)
                            for (var x = bounds.Left; x < bounds.Right; x++)
                            {
                                var pixel = native.GetPixel(x, y);
                                var old = baseline.GetPixel(x, y);
                                static int Difference(Color left, Color right) => Math.Abs(left.R - right.R) +
                                    Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
                                var brightArtwork = child.Name == "FilterEmptyArtwork" && pixel.GetBrightness() > .32f;
                                var textInk = child.Name != "FilterEmptyArtwork" && Difference(pixel, child.ForeColor) < 150;
                                if ((brightArtwork || textInk) && Difference(pixel, old) > 50) ink++;
                            }
                            Require(ink >= 12,
                                $"{filter} {Width}×{Height} reveal {cycle + 1}: {child.Name} has actual foreground native pixels (ink={ink}, bounds={bounds})");
                        }
                        if (cycle == 0) native.Save(Path.Combine(output,
                            $"filter-empty-{filter}-native-{requested.Width}x{requested.Height}.png"));
                        if (cycle == 0) { empty.Visible = false; Refresh(); Application.DoEvents(); }
                    }
                }
            }
            finally { Size = priorSize; PerformLayout(); Application.DoEvents(); }
        }

        Require(_filterButtons.Count == 6 && new[] { "all", "unread", "favorites", "direct", "group", "archived" }
            .All(_filterButtons.ContainsKey), "All six requested sidebar filters, including Archive, are real buttons");
        foreach (var width in new[] { 1120, 1380, 1536, 1920 })
        {
            Size = new Size(width, 860); PerformLayout(); Application.DoEvents();
            foreach (var button in _filterButtons.Values)
            {
                Require(button.Parent is not null && button.Parent.ClientRectangle.Contains(button.Bounds),
                    $"Filter '{button.Text}' remains fully inside its wrapping host at {width}px (button {button.Bounds}, host {button.Parent?.ClientRectangle})");
                using var graphics = button.CreateGraphics();
                var measured = TextRenderer.MeasureText(graphics, button.Text, button.Font,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                Require(button.ClientSize.Width >= measured.Width + 8 && button.ClientSize.Height >= measured.Height,
                    $"Filter '{button.Text}' fits its measured native font without clipping at {width}px");
            }
            var buttons = _filterButtons.Values.ToArray();
            Require(!buttons.Any(left => buttons.Any(right => !ReferenceEquals(left, right) && left.Bounds.IntersectsWith(right.Bounds))),
                $"Six filter hit targets do not overlap at {width}px");
            Require(buttons.GroupBy(button => button.Top).Select(row => row.Count()).Distinct().Count() == 1,
                $"All filter rows are balanced at {width}px; Archive cannot become a lone overflow item");
            if (width == 1920)
            {
                Require(buttons.Select(button => button.Top).Distinct().Count() == 1,
                    "All six filters share one compact row at the screenshot's wide-window size");
                var output = Path.Combine(AppContext.BaseDirectory, "snapshots");
                Directory.CreateDirectory(output);
                using var native = CaptureInfoClient(this);
                native.Save(Path.Combine(output, "filters-six-wide-native.png"));
            }
        }
        Size = new Size(1120, 860);
        var nativeFonts = _filterButtons.ToDictionary(pair => pair.Key, pair => pair.Value.Font);
        foreach (var fontScale in new[] { 1.25f, 1.5f, 2f })
        {
            foreach (var button in _filterButtons.Values) button.Font = Theme.Font(9f * fontScale);
            _filterButtons["all"].Parent!.PerformLayout();
            PerformLayout(); Application.DoEvents();
            using var graphics = CreateGraphics();
            Require(_filterButtons.Values.All(button =>
            {
                var measured = TextRenderer.MeasureText(graphics, button.Text, button.Font,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return button.Parent!.ClientRectangle.Contains(button.Bounds) && button.Width >= measured.Width + 8 && button.Height >= measured.Height;
            }), $"Minimum-width filter layout accommodates a {fontScale:P0} enlarged-font probe without clipping (not an external-PC DPI test)");
        }
        foreach (var pair in nativeFonts) _filterButtons[pair.Key].Font = pair.Value;
        _filterButtons["all"].Parent!.PerformLayout();
        PerformLayout(); Application.DoEvents();
        var archiveLabel = _filterButtons["archived"].Text;
        var probeFonts = new List<Font>();
        var thresholdWidths = new[] { 1280, 1450 }.SelectMany(logical =>
        {
            var clientThreshold = (int)Math.Round(logical * DeviceDpi / 96f);
            var frame = Width - ClientSize.Width;
            return new[] { clientThreshold + frame - 1, clientThreshold + frame, clientThreshold + frame + 1 };
        });
        var layoutWidths = new[] { 1120, 1380, 1536, 1920 }.Concat(thresholdWidths).Distinct().ToArray();
        try
        {
            foreach (var fontScale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                var probeFont = Theme.Font(9f * fontScale); probeFonts.Add(probeFont);
                foreach (var button in _filterButtons.Values) button.Font = probeFont;
                foreach (var width in layoutWidths)
                foreach (var label in new[] { "Arşiv", "Arşiv (1)", "Arşiv (99)", "Arşiv (20000)" })
                {
                    Size = new Size(width, 860);
                    _filterButtons["archived"].Text = label;
                    PerformLayout(); Application.DoEvents();
                    using var graphics = _filterButtons["all"].Parent!.CreateGraphics();
                    var buttons = _filterButtons.Values.ToArray();
                    Require(buttons.All(button =>
                    {
                        var text = TextRenderer.MeasureText(graphics, button.Text, button.Font,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        return button.Parent!.ClientRectangle.Contains(button.Bounds) &&
                            button.Width >= text.Width + 8 && button.Height >= text.Height;
                    }), $"Dynamic filter count/font fit: {width}px, {fontScale:P0}, {label}");
                    Require(buttons.GroupBy(button => button.Top).Select(row => row.Count()).Distinct().Count() == 1 &&
                        !buttons.Any(left => buttons.Any(right => left != right && left.Bounds.IntersectsWith(right.Bounds))),
                        $"Dynamic filter grid has balanced rows and separate hit targets: {width}px, {fontScale:P0}, {label}");
                    var host = buttons[0].Parent!;
                    var listHost = _conversationFilterEmpty!.Parent!;
                    var filterBottom = PointToClient(host.PointToScreen(new Point(0, host.Height))).Y;
                    var listTop = PointToClient(listHost.PointToScreen(Point.Empty)).Y;
                    Require(filterBottom <= listTop,
                        $"Filter row reserves its full height above the conversation list: {width}px, {fontScale:P0}, {label}");
                }
            }
            // Font/text updates also happen while Settings/Profile hide the list.
            _sidebarChats!.Visible = false;
            _filterButtons["archived"].Text = "Arşiv (20000)";
            _filterButtons["archived"].Text = "Arşiv";
            foreach (var pair in nativeFonts) _filterButtons[pair.Key].Font = pair.Value;
            _sidebarChats.Visible = true;
            PerformLayout(); Application.DoEvents();
            Require(_filterButtons.Values.All(button => button.Parent!.ClientRectangle.Contains(button.Bounds)),
                "Hidden-sidebar label updates reflow fully on reveal without a clipped Archive button");
        }
        finally
        {
            foreach (var pair in nativeFonts) _filterButtons[pair.Key].Font = pair.Value;
            _filterButtons["archived"].Text = archiveLabel;
            foreach (var font in probeFonts) font.Dispose();
            Size = new Size(1120, 860); PerformLayout(); Application.DoEvents();
        }
        _filterButtons["all"].PerformClick(); Expect(allCount, "All filter contains every direct and group conversation");
        _conversationList.Visible = false;
        _filterButtons["direct"].PerformClick();
        _conversationList.Visible = true;
        Expect(directCount, "A filter changed while the chat list was collapsed still hides nonmatching cards after expansion");
        _filterButtons["group"].PerformClick(); Expect(groupCount, "Group filter excludes direct conversations");
        _filterButtons["direct"].PerformClick(); Expect(directCount, "People filter excludes group conversations");
        _premiumSearch.Text = "AYŞE"; Expect(1, "Search is case-insensitive and composes with the people filter");
        _filterButtons["group"].PerformClick(); Expect(0, "Search does not leak direct conversations into group filter");
        Require(_conversationFilterEmpty?.Title == "Sohbet bulunamadı", "An unmatched search uses a search-result empty state");
        _premiumSearch.Text = "Tasarım ekibi"; Expect(1, "Group search returns only its matching group");
        _premiumSearch.Text = "";
        _premiumSearch.Text = "   "; Expect(groupCount, "Whitespace-only search leaves the selected filter's full result set visible");
        _premiumSearch.Text = "";
        _filterButtons["unread"].PerformClick(); Expect(unreadCount, "Unread filter uses actual conversation UnreadCount metadata");
        Require(_premiumSearch.Properties.NullValuePrompt == "Okunmamış sohbetlerde ara...",
            "Unread search prompt matches the selected filter");
        _premiumSearch.Text = "  AYŞE  "; Expect(1, "Unread search trims surrounding whitespace while preserving real unread results");
        _premiumSearch.Text = "bulunamayan kişi"; Expect(0, "Unread search can produce an unmatched result while unread conversations still exist");
        Require(_conversationFilterEmpty?.Title == "Sohbet bulunamadı" && !_conversationFilterEmpty.AllReadArtworkForQa,
            "An unmatched unread search never claims all messages were read with the all-read artwork");
        _premiumSearch.Text = ""; Expect(unreadCount, "Clearing unmatched unread search restores the actual unread conversations");
        Require(ReferenceEquals(selected, _selectedConversation) && ReferenceEquals(selectedCard, _selectedConversationCard) &&
            _premiumComposer.Text == "Filtre değişiminde korunacak taslak 😊",
            "Hiding the selected read chat preserves its conversation, card, history target and draft");

        foreach (var conversation in summaries) SetFavoriteConversation(conversation.Id, false);
        _filterButtons["favorites"].PerformClick(); Expect(0, "Favorites filter starts empty when no chat is favorited");
        Require(_conversationFilterEmpty?.Title == "Henüz favori sohbet yok" &&
            _conversationFilterEmpty.Detail.Contains("sağ tıklayıp", StringComparison.Ordinal),
            "Favorites empty state explains the real right-click action");
        VerifyEmptyNative("favorites");
        SetFavoriteConversation(ayse.Id, true);
        SetFavoriteConversation(design.Id, true);
        FilterConversations(_premiumSearch.Text);
        Expect(2, "Favorites can contain both a direct chat and a group");
        _premiumSearch.Text = "AYŞE"; Expect(1, "Favorite search composes with the saved favorite IDs");
        _premiumSearch.Text = "";
        SetFavoriteConversation(ayse.Id, false);
        FilterConversations(_premiumSearch.Text);
        Expect(1, "Removing a favorite immediately updates the favorite result set");

        // Go through the production summary reconciliation path, not merely a
        // hidden-label fixture: unread counts can change after a background poll.
        ReconcileConversationCards(summaries.Select(conversation => conversation with { UnreadCount = 0 }).ToArray());
        _filterButtons["unread"].PerformClick(); Expect(0, "No-read-count metadata produces a genuine unread empty state");
        Require(_conversationFilterEmpty?.Title == "Okunmamış sohbet yok" &&
            _conversationFilterEmpty.Detail == "Hepsini gördünüz." && _conversationFilterEmpty.AllActionVisible &&
            _conversationFilterEmpty.AllReadArtworkForQa,
            "Unread empty state uses the requested exact copy and visible return action");
        VerifyEmptyNative("unread");
        Require(_conversationList.Controls.Cast<Control>().All(control => control.Tag is ConversationSummary),
            "The empty state is an overlay rather than a synthetic conversation/card");
        var unreadRoom = summaries.First(conversation => conversation.Id != selected.Id) with { UnreadCount = 1 };
        ReconcileConversationCards(summaries.Select(conversation => conversation.Id == unreadRoom.Id ? unreadRoom :
            conversation with { UnreadCount = 0 }).ToArray());
        Expect(1, "A changed unread count during reconciliation restores the unread list");
        ReconcileConversationCards(summaries.Select(conversation => conversation with { UnreadCount = 0 }).ToArray());
        Expect(0, "A read-count change during reconciliation restores the empty state");
        _premiumSearch.Text = "olmayan sohbet";
        _conversationFilterEmpty!.ShowAllForQa();
        Expect(allCount, "Empty-state return action clears search and returns to all chats");
        Require(_conversationFilter == "all" && _premiumSearch.Text.Length == 0 &&
            _premiumComposer.Text == "Filtre değişiminde korunacak taslak 😊",
            "Returning from an empty state leaves the draft intact");
        SetFavoriteConversation(design.Id, false);
        SetArchivedConversation(ayse.Id, true);
        _filterButtons["all"].PerformClick(); Expect(allCount - 1, "Archived direct chats leave the main list without deleting a card");
        _premiumSearch.Text = "Ana listede kalan arama";
        _filterButtons["archived"].PerformClick(); Expect(1, "Archive filter displays the personally archived direct chat");
        Require(_premiumSearch.Text.Length == 0, "Entering Archive clears an unrelated main-list query without changing the draft");
        Require(_premiumSearch.Properties.NullValuePrompt == "Arşivlenmiş sohbetlerde ara...", "Archive search uses its own visible prompt");
        _premiumSearch.Text = "Tasarım"; Expect(0, "Archive search cannot reveal an unarchived group");
        _filterButtons["archived"].PerformClick(); Expect(0, "Reselecting Archive preserves an intentional archive search");
        _premiumSearch.Text = "";
        SetArchivedConversation(ayse.Id, false);
        Expect(0, "Unarchiving removes a chat from archive immediately");
        Require(_conversationFilterEmpty?.Title == "Arşivlenmiş sohbet yok", "Archive empty state describes archive rather than deletion");
        VerifyEmptyNative("archived");
        _filterButtons["all"].PerformClick(); Expect(allCount, "Unarchiving restores the same direct/group cards to All");
        _premiumComposer.Text = priorDraft;
        return checks;
    }

    internal void PopulateFilterSnapshot(string filter, bool empty)
    {
        PopulateSnapshot();
        if (empty && filter == "unread")
        {
            var summaries = _conversationList.Controls.Cast<Control>().Select(control =>
                ((ConversationSummary)control.Tag!) with { UnreadCount = 0 }).ToArray();
            ReconcileConversationCards(summaries);
        }
        if (!empty && filter == "favorites")
            foreach (var card in _conversationList.Controls.Cast<Control>().Take(3))
                SetFavoriteConversation(((ConversationSummary)card.Tag!).Id, true);
        SetConversationFilter(filter);
    }
}
