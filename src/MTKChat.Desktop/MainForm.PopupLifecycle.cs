namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private ModernContextMenu? _expiryMenu;

    private ModernContextMenu GetExpiryMenu()
    {
        if (_expiryMenu is { IsDisposed: false } existing) return existing;
        var choices = new[]
        {
            new ExpiryChoice("Kalıcı", null),
            new ExpiryChoice("30 saniye", TimeSpan.FromSeconds(30)),
            new ExpiryChoice("5 dakika", TimeSpan.FromMinutes(5)),
            new ExpiryChoice("1 saat", TimeSpan.FromHours(1)),
            new ExpiryChoice("1 gün", TimeSpan.FromDays(1))
        };
        var menu = Theme.ContextMenu();
        foreach (var choice in choices)
        {
            var item = new ToolStripMenuItem(choice.Label) { Tag = choice };
            item.Click += (_, _) =>
            {
                _selectedExpiry = choice;
                _premiumExpiryButton.Text = choice.Label + "  ▾";
            };
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in menu.Items)
                item.Checked = ((ExpiryChoice)item.Tag!).Duration == _selectedExpiry.Duration;
        };
        // ToolStrip's modal filter continues native teardown after firing Closed.
        // Disposing inside Closed caused SetVisibleCore to recreate an already
        // disposed handle and terminate the application's UI thread. Own and reuse
        // one popup for the button's entire lifetime, as the other composer menus do.
        _premiumExpiryButton.Disposed += (_, _) => menu.Dispose();
        _expiryMenu = menu;
        return menu;
    }
}
