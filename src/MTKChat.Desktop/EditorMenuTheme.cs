using System.Runtime.CompilerServices;
using DevExpress.Utils;
using DevExpress.Utils.Menu;
using DevExpress.XtraEditors;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Grid;

namespace MTKChat.Desktop;

internal static class EditorMenuTheme
{
    private static readonly ConditionalWeakTable<Control, object> Hooked = new();

    internal static void Attach(Control control)
    {
        if (Hooked.TryGetValue(control, out _)) return;
        Hooked.Add(control, new object());
        // Reparented controls must not gain duplicate handlers; weak keys also avoid
        // keeping closed forms alive in a global theme registry.
        control.ControlAdded += (_, e) => { if (e.Control is { } child) Attach(child); };
        if (control is TextEdit editor)
            editor.Properties.BeforeShowMenu += (_, e) => Apply(e.Menu);
        if (control is GridControl { MainView: GridView view })
            view.PopupMenuShowing += (_, e) => { if (e.Menu is { } menu) Apply(menu); };
        foreach (Control child in control.Controls) Attach(child);
    }

    internal static void Apply(DXPopupMenu menu)
    {
        Style(menu.Appearance, Theme.Surface, Theme.Text);
        foreach (DXMenuItem item in menu.Items) ApplyItem(item);
    }

    private static void ApplyItem(DXMenuItem item)
    {
        Style(item.Appearance, Theme.Surface, Theme.Text);
        Style(item.AppearanceHovered, Theme.SurfaceHover, Theme.Text);
        Style(item.AppearanceDisabled, Theme.Surface, Theme.Muted);
        if (item is DXSubMenuItem sub)
            foreach (DXMenuItem child in sub.Items) ApplyItem(child);
    }

    private static void Style(AppearanceObject appearance, Color background, Color foreground)
    {
        appearance.BackColor = background;
        appearance.ForeColor = foreground;
        appearance.Options.UseBackColor = appearance.Options.UseForeColor = true;
    }
}
