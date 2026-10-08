using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyConversationMenus(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var session = form._session!;
        var room = fixture.InitialRoom;
        var card = fixture.Card(room);
        var menu = (ModernContextMenu)card.ContextMenuStrip!;
        var selected = form._selectedConversation;
        var messages = form._messageList.Controls.Cast<Control>().ToArray();
        form._premiumComposer.Text = "Menü açılırken korunacak taslak 😊";
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Conversation menu QA: " + check);
            checks.Add(check);
        }

        var groupRoot = new[] { "Sohbeti sabitle", "Favorilere ekle", "Yıldızlı mesajlar", "Sohbeti arşivle", "Bildirimleri sessize al",
            "Grup üyelerini görüntüle", "Grup seçenekleri", "Bu grup mesajlarını sil", "Bu sohbeti sil", "Gruptan çık" };
        var groupActions = new[] { "Grup adını değiştir", "Davet bağlantılarını yönet", "Grup fotoğrafını düzenle", "Grup yönetimi" };
        var options = (ToolStripMenuItem)ConversationMenuItem(menu, "Grup seçenekleri");
        foreach (var (name, role, agent, groupRole, member, rename, manage) in new[]
        {
            ("site-admin", "admin", false, (string?)null, true, true, true),
            ("group-admin", "user", false, "admin", true, true, true),
            ("group-mod", "user", false, "mod", true, false, true),
            ("member", "user", false, (string?)null, true, false, false),
            ("agent-admin", "admin", true, (string?)null, true, false, true),
            ("nonmember-group-admin", "user", false, "admin", false, false, true)
        })
        {
            var actor = session.User with { Role = role, IsAgent = agent };
            form._session = session with { User = actor };
            card.Tag = room with
            {
                Participants = member ? room.Participants : room.Participants.Where(user => user.Id != actor.Id).ToArray(),
                GroupRoles = groupRole is null ? null : new Dictionary<Guid, string> { [actor.Id] = groupRole }
            };
            menu.Show(card, new Point(4, 4)); Application.DoEvents();
            Require(ConversationMenuCommands(menu).Select(item => item.Text).SequenceEqual(groupRoot),
                name + ": actual group root keeps ten common, member, grouped and destructive commands");
            Require(groupActions.All(title => menu.Items.Cast<ToolStripItem>().All(item => item.Text != title)),
                name + ": group editing commands are nested rather than lengthening the root");
            if (name == "site-admin") VerifyConversationMenuHover(menu, options, Require, name);
            options.Select(); options.ShowDropDown(); Application.DoEvents();
            Require(ConversationMenuItem(options.DropDown, "Grup adını değiştir").Available == rename &&
                ConversationMenuItem(options.DropDown, "Davet bağlantılarını yönet").Available == rename &&
                ConversationMenuItem(options.DropDown, "Grup yönetimi").Available == manage &&
                ConversationMenuItem(options.DropDown, "Grup fotoğrafını düzenle").Available,
                name + ": nested actions retain the existing rename, invite, photo and management permissions");
            VerifyConversationMenuSurface(menu, Require, name + " root", maxHeight: 400);
            VerifyConversationMenuSurface(options.DropDown, Require, name + " options");
            if (name is "site-admin" or "group-mod" or "member")
                CaptureConversationMenu(menu, options, Path.Combine(directory, "conversation-menu-" + name + ".png"));
            options.HideDropDown(); menu.Close(); Application.DoEvents();
        }

        // Permissions are refreshed against the current card Tag and session on
        // every opening. The same menu above is reused for all role transitions.
        form._session = null; card.Tag = room;
        menu.Show(card, new Point(4, 4)); options.ShowDropDown(); Application.DoEvents();
        Require(!ConversationMenuItem(options.DropDown, "Grup adını değiştir").Available &&
            !ConversationMenuItem(options.DropDown, "Davet bağlantılarını yönet").Available &&
            !ConversationMenuItem(options.DropDown, "Grup yönetimi").Available &&
            ConversationMenuItem(options.DropDown, "Grup fotoğrafını düzenle").Available,
            "An absent session clears privileged submenu visibility on the existing popup");
        options.HideDropDown(); menu.Close();
        form._session = session;

        menu.Show(card, new Point(4, 4)); Application.DoEvents();
        form._session = session with { User = session.User with { Role = "user" } };
        card.Tag = room with { GroupRoles = null };
        options.ShowDropDown(); Application.DoEvents();
        Require(!ConversationMenuItem(options.DropDown, "Grup adını değiştir").Available &&
            !ConversationMenuItem(options.DropDown, "Davet bağlantılarını yönet").Available &&
            !ConversationMenuItem(options.DropDown, "Grup yönetimi").Available,
            "Opening the child menu refreshes permissions even if the actor changes while the root is already open");
        options.HideDropDown(); menu.Close(); form._session = session; card.Tag = room;

        foreach (var busy in new[] { "sending", "voice-stopping", "idle" })
        {
            form._sending = busy == "sending"; form._voiceStopping = busy == "voice-stopping";
            menu.Show(card, new Point(4, 4)); Application.DoEvents();
            var destructive = groupRoot.TakeLast(3).Select(title => ConversationMenuItem(menu, title)).ToArray();
            Require(destructive.All(item => item.Enabled == (busy == "idle") && item.ForeColor == Theme.Danger),
                busy + ": all three destructive actions retain their busy gate and danger color");
            if (busy == "sending") CaptureConversationMenu(menu, null, Path.Combine(directory, "conversation-menu-busy.png"));
            menu.Close();
        }
        form._sending = form._voiceStopping = false;
        card.Tag = room;
        menu.Show(card, new Point(4, 4)); Application.DoEvents();
        var mute = (ToolStripMenuItem)ConversationMenuItem(menu, "Bildirimleri sessize al");
        mute.Select(); mute.ShowDropDown(); Application.DoEvents();
        Require(ConversationMenuCommands(mute.DropDown).Select(item => item.Text)
            .SequenceEqual(new[] { "8 saat", "1 hafta", "Süresiz", "Sessize almayı kaldır" }) &&
            !ConversationMenuItem(mute.DropDown, "Sessize almayı kaldır").Enabled,
            "The real mute submenu retains every duration and the disabled unmute state");
        VerifyConversationMenuSurface(mute.DropDown, Require, "mute submenu");
        CaptureConversationMenu(menu, mute, Path.Combine(directory, "conversation-menu-mute.png"));
        mute.HideDropDown(); menu.Close();

        // Showing next to a card is not enough to catch a nested popup falling
        // beyond the monitor edge. Exercise the actual native placement, not a
        // synthetic bitmap arrangement, at all four work-area corners.
        var workArea = Screen.FromControl(form).WorkingArea;
        foreach (var (edge, location) in new[]
        {
            ("top-left", new Point(workArea.Left + 2, workArea.Top + 2)),
            ("top-right", new Point(workArea.Right - 2, workArea.Top + 2)),
            ("bottom-left", new Point(workArea.Left + 2, workArea.Bottom - 2)),
            ("bottom-right", new Point(workArea.Right - 2, workArea.Bottom - 2))
        })
        {
            menu.Show(location); options.Select(); options.ShowDropDown(); Application.DoEvents();
            Require(workArea.Contains(menu.Bounds) && workArea.Contains(options.DropDown.Bounds),
                edge + ": native root and group submenu both remain inside the monitor work area");
            options.HideDropDown(); menu.Close();
        }

        // Send keyboard messages only through the application's own modal menu
        // filter. This does not inject desktop-wide input into another window.
        menu.Show(card, new Point(4, 4)); Application.DoEvents();
        var favoriteAction = ConversationMenuItem(menu, "Favorilere ekle");
        favoriteAction.Select();
        var down = Message.Create(menu.Handle, 0x0100 /* WM_KEYDOWN */, new IntPtr((int)Keys.Down), IntPtr.Zero);
        ProcessConversationMenuKey(menu, ref down);
        Require(ConversationMenuItem(menu, "Yıldızlı mesajlar").Selected,
            "Native keyboard Down moves between real themed menu actions");
        options.Select(); options.ShowDropDown(); Application.DoEvents();
        var escapeChild = Message.Create(options.DropDown.Handle, 0x0100 /* WM_KEYDOWN */,
            new IntPtr((int)Keys.Escape), IntPtr.Zero);
        ProcessConversationMenuKey(options.DropDown, ref escapeChild); Application.DoEvents();
        Require(!options.DropDown.Visible && menu.Visible,
            "Native Escape closes only the nested group menu and retains its parent");
        var escapeRoot = Message.Create(menu.Handle, 0x0100 /* WM_KEYDOWN */,
            new IntPtr((int)Keys.Escape), IntPtr.Zero);
        ProcessConversationMenuKey(menu, ref escapeRoot); Application.DoEvents();
        Require(!menu.Visible && !menu.IsDisposed,
            "Native Escape closes the root without disposing its reusable popup");

        var direct = room with { Id = Guid.NewGuid(), Kind = "direct", Title = "Menü QA kişisel sohbet",
            Participants = [session.User, fixture.FirstSender], GroupRoles = null };
        using (var directCard = form.CreateConversationCard(direct))
        {
            form._conversationList.Controls.Add(directCard);
            var directMenu = (ModernContextMenu)directCard.ContextMenuStrip!;
            directMenu.Show(directCard, new Point(4, 4)); Application.DoEvents();
            Require(ConversationMenuCommands(directMenu).Select(item => item.Text).SequenceEqual(new[]
                { "Sohbeti sabitle", "Favorilere ekle", "Yıldızlı mesajlar", "Sohbeti arşivle", "Bildirimleri sessize al",
                    "Sohbet bilgileri", "Bu sohbetin mesajlarını sil", "Bu sohbeti sil" }),
                "The actual direct menu keeps eight commands and exposes no group actions or Leave");
            VerifyConversationMenuSurface(directMenu, Require, "direct root", maxHeight: 330);
            VerifyConversationMenuHover(directMenu, directMenu.Items[0], Require, "direct");
            CaptureConversationMenu(directMenu, null, Path.Combine(directory, "conversation-menu-direct.png"));
            directMenu.Close();
            Require(HistoryQaControls(directCard).All(control => ReferenceEquals(control.ContextMenuStrip, directMenu)),
                "Every card child shares the same owned direct context menu");
            directCard.Dispose();
            Require(directMenu.IsDisposed, "Disposing a card releases its root popup and nested dropdowns");
        }

        // This fixture also covers the shared renderer's native check column and
        // unthemed default item types used by other application popup callers.
        using (var shared = Theme.ContextMenu())
        {
            shared.Items.Add(new ModernMenuItem { Text = "Seçili seçenek", Icon = ModernMenuIcon.Favorite, Checked = true });
            shared.Items.Add(new ToolStripMenuItem("Devre dışı seçenek") { Enabled = false, ForeColor = Theme.Danger });
            shared.Items.Add(new ToolStripSeparator());
            var nested = new ToolStripMenuItem("Alt menü");
            nested.DropDownItems.Add(new ToolStripMenuItem("Seçili alt seçenek") { Checked = true });
            shared.Items.Add(nested);
            shared.Show(card, new Point(4, 4)); nested.Select(); nested.ShowDropDown(); Application.DoEvents();
            VerifyConversationMenuSurface(shared, Require, "shared checked/disabled root", requireIcons: false);
            VerifyConversationMenuSurface(nested.DropDown, Require, "shared checked submenu", requireIcons: false);
            foreach (var strip in new ToolStrip[] { shared, nested.DropDown })
            {
                var checkedItem = strip.Items.OfType<ToolStripMenuItem>().Single(item => item.Checked);
                using var checkedPaint = CaptureConversationMenuSurface(strip);
                if (checkedItem is ModernMenuItem vectorItem)
                {
                    var icon = vectorItem.Icon;
                    vectorItem.Icon = ModernMenuIcon.None;
                    using var tickOnly = CaptureConversationMenuSurface(strip);
                    vectorItem.Icon = icon;
                    Require(ConversationMenuDifferenceBounds(checkedPaint, tickOnly).IsEmpty,
                        "A checked vector action replaces its icon with the tick rather than painting overlapping symbols");
                }
                checkedItem.Checked = false;
                using var uncheckedPaint = CaptureConversationMenuSurface(strip);
                checkedItem.Checked = true;
                var ink = ConversationMenuDifferenceBounds(checkedPaint, uncheckedPaint);
                Require(!ink.IsEmpty && new Rectangle(checkedItem.Bounds.Left, checkedItem.Bounds.Top,
                    (int)Math.Ceiling(40 * strip.DeviceDpi / 96f), checkedItem.Height).Contains(ink),
                    "The actual shared " + (ReferenceEquals(strip, shared) ? "root" : "submenu") + " check mark fits its unified left column");
            }
            using (var disabledPaint = CaptureConversationMenuSurface(shared))
            {
                var disabledBounds = shared.Items[1].Bounds;
                Require(ConversationMenuHasColor(disabledPaint, disabledBounds, Theme.Muted) &&
                    !ConversationMenuHasColor(disabledPaint, disabledBounds, Theme.Danger),
                    "Disabled destructive text paints the theme's muted color without danger or Windows GrayText ink");
            }
            CaptureConversationMenu(shared, nested, Path.Combine(directory, "conversation-menu-checked-disabled.png"));
            nested.HideDropDown(); shared.Close();
            Require(!shared.IsDisposed, "Closing a shared menu keeps the popup reusable until owner disposal");
        }

        Require(ReferenceEquals(selected, form._selectedConversation) &&
            messages.SequenceEqual(form._messageList.Controls.Cast<Control>()) &&
            form._premiumComposer.Text == "Menü açılırken korunacak taslak 😊" && fixture.Dialogs == 0,
            "Opening, closing and inspecting menus preserves selection, message controls and draft without modal errors");
        checks.Add($"Popup geometry and renderer were exercised at the available native {menu.DeviceDpi} DPI; physical cross-monitor DPI changes were not simulated.");
        return checks;
    }

    private static ToolStripItem ConversationMenuItem(ToolStrip menu, string text) =>
        menu.Items.Cast<ToolStripItem>().Single(item => item.Text == text);

    private static void ProcessConversationMenuKey(Control target, ref Message message)
    {
        // FilterMessage alone is only the first half of the WinForms message
        // loop: unconsumed dialog keys must also reach control preprocessing.
        if (!Application.FilterMessage(ref message)) target.PreProcessControlMessage(ref message);
    }

    private static ToolStripMenuItem[] ConversationMenuCommands(ToolStrip menu) =>
        menu.Items.OfType<ToolStripMenuItem>().Where(item => item.Available).ToArray();

    private static void VerifyConversationMenuSurface(ToolStrip menu, Action<bool, string> require, string name,
        int? maxHeight = null, bool requireIcons = true)
    {
        var scale = menu.DeviceDpi / 96f;
        var rows = ConversationMenuCommands(menu);
        require(menu.Visible && menu.Renderer is ModernMenuRenderer && menu.BackColor == ModernContextMenu.SurfaceColor && menu.ForeColor == Theme.Text,
            name + ": actual popup uses the same dark theme and vector renderer");
        require(menu.Font.FontFamily.Name == "Segoe UI" && Math.Abs(menu.Font.SizeInPoints - 9.5f) < .05f &&
            rows.All(item => item.Font.Equals(menu.Font)), name + ": root and child row fonts share readable Segoe UI 9.5pt");
        require(rows.Length > 0 && rows.All(item => item.Height >= 28 * scale && item.Height <= 36 * scale &&
            item.Bounds.Top >= 0 && item.Bounds.Bottom <= menu.ClientSize.Height),
            name + ": every compact row has a readable height and fits vertically inside its popup");
        require(rows.All(item => TextRenderer.MeasureText(item.Text, item.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= menu.ClientSize.Width - 44 * scale),
            name + ": complete captions fit beside their reserved icon and arrow columns");
        if (maxHeight is { } limit)
            require(menu.Height <= limit * scale, name + $": root height {menu.Height}px fits the {limit}px logical compact budget");
        require(Screen.FromControl(menu).WorkingArea.Contains(menu.RectangleToScreen(menu.ClientRectangle)),
            name + ": actual popup stays inside the available monitor work area");
        if (!requireIcons) return;
        require(rows.All(item => item is ModernMenuItem { Icon: not ModernMenuIcon.None } && item.Image is not null &&
            item.Image.Width == 18 && item.Image.Height == 18), name + ": every conversation command reserves a vector icon slot");
        foreach (var item in rows.Cast<ModernMenuItem>())
        {
            var icon = item.Icon;
            using var actual = CaptureConversationMenuSurface(menu);
            item.Icon = ModernMenuIcon.None;
            using var empty = CaptureConversationMenuSurface(menu);
            item.Icon = icon;
            var ink = ConversationMenuDifferenceBounds(actual, empty);
            var iconColumn = new Rectangle(item.Bounds.Left, item.Bounds.Top, (int)Math.Ceiling(40 * scale), item.Height);
            require(!ink.IsEmpty && iconColumn.Contains(ink),
                name + "/" + item.Text + ": vector ink is nonempty and fits its left column without reaching the caption");
        }
    }

    private static Bitmap CaptureConversationMenuSurface(ToolStrip menu)
    {
        var bitmap = new Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
        return bitmap;
    }

    private static void VerifyConversationMenuHover(ModernContextMenu menu, ToolStripItem item,
        Action<bool, string> require, string name)
    {
        menu.Items.Cast<ToolStripItem>().First(other => other != item && other is ToolStripMenuItem).Select();
        using var idle = CaptureConversationMenuSurface(menu);
        item.Select();
        using var hover = CaptureConversationMenuSurface(menu);
        var ink = ConversationMenuDifferenceBounds(idle, hover, item.Bounds);
        var scale = menu.DeviceDpi / 96f;
        require(item.Selected && !ink.IsEmpty && ink.Width > menu.Width - item.Bounds.Left - 40 * scale,
            name + ": selecting a row paints a broad visible hover surface behind its caption");
        require(ink.Left >= item.Bounds.Left + scale && ink.Right <= menu.Width - scale &&
            ink.Top >= item.Bounds.Top + scale && ink.Bottom <= item.Bounds.Bottom - scale,
            name + ": selected icon and hover surface retain clean left, right, top and bottom gutters inside the border");
    }

    private static void CaptureConversationMenu(ModernContextMenu menu, ToolStripMenuItem? child, string path)
    {
        using var root = CaptureConversationMenuSurface(menu);
        using var nested = child is null ? null : CaptureConversationMenuSurface(child.DropDown);
        using var result = new Bitmap(root.Width + (nested?.Width ?? 0) + 34,
            Math.Max(root.Height, (child?.Bounds.Top ?? 0) + (nested?.Height ?? 0)) + 32);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Theme.Canvas); graphics.DrawImageUnscaled(root, 16, 16);
        if (nested is not null) graphics.DrawImageUnscaled(nested, root.Width + 18, 16 + child!.Bounds.Top);
        result.Save(path);
    }

    private static Rectangle ConversationMenuDifferenceBounds(Bitmap first, Bitmap second, Rectangle? region = null)
    {
        var left = first.Width; var top = first.Height; var right = -1; var bottom = -1;
        var crop = Rectangle.Intersect(region ?? new Rectangle(Point.Empty, first.Size), new Rectangle(Point.Empty, first.Size));
        for (var y = crop.Top; y < crop.Bottom; y++)
        for (var x = crop.Left; x < crop.Right; x++)
        {
            if (first.GetPixel(x, y) == second.GetPixel(x, y)) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static bool ConversationMenuHasColor(Bitmap image, Rectangle bounds, Color color)
    {
        var crop = Rectangle.Intersect(bounds, new Rectangle(Point.Empty, image.Size));
        for (var y = crop.Top; y < crop.Bottom; y++)
        for (var x = crop.Left; x < crop.Right; x++)
            if (image.GetPixel(x, y).ToArgb() == color.ToArgb()) return true;
        return false;
    }
}
