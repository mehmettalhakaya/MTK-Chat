using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class ConversationPickerQA
{
    internal static IReadOnlyList<string> Verify(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Conversation picker QA: " + check);
            checks.Add(check);
        }
        static ChatUser User(int value, string? name = null, string role = "user", bool bot = false) =>
            new(Guid.Parse($"70000000-0000-0000-0000-{value:000000000000}"), name ?? $"Kullanıcı {value:0000}", "", bot, null, role);
        var users = new[]
        {
            User(1, "Zeynep Kaya"), User(2, "Ahmet Yılmaz"), User(3, "MTK", "admin"),
            User(4, "Gemini Agent", bot: true), User(5, "Çok uzun kullanıcı adı üst üste binmeden sınırda kesilmeli")
        };
        using (var group = new ConversationPickerForm(users.Concat([users[0]]), true) { ShowInTaskbar = false })
        {
            group.Show(); Pump(group);
            Require(group.PeopleForQa.FilteredCountForQa == 5, "Duplicate site IDs create only one selectable person");
            Require(!group.ConfirmEnabledForQa && !group.ValidateForQa(), "An empty group cannot be confirmed");
            Require(Descendants(group).OfType<CheckedListBox>().Count() == 0 &&
                Descendants(group).OfType<CheckEdit>().All(edit => edit.Properties.CheckBoxOptions.Style == CheckBoxStyle.SvgCheckBox1),
                "Group selection uses themed DevExpress SVG CheckEdit controls, not native checked lists");
            var person = group.PeopleForQa.RowsForQa.First(row => row.User?.Id == users[1].Id);
            person.ClickForQa(); Pump(group);
            Require(group.Selected.SequenceEqual([users[1].Id]) && person.SelectorForQa.Checked,
                "One row click chooses exactly one person and updates the real CheckEdit once");
            person.SelectorForQa.Checked = false;
            Require(group.Selected.Length == 0, "One checkbox change deselects exactly once without a row double-toggle");
            person.SelectorForQa.Focus();
            SendMessage(person.SelectorForQa.Handle, 0x100, (nint)Keys.Space, 0);
            SendMessage(person.SelectorForQa.Handle, 0x101, (nint)Keys.Space, 0); Pump(group);
            Require(group.Selected.SequenceEqual([users[1].Id]) && person.SelectorForQa.Checked,
                "Space on the app-owned real DevExpress editor toggles exactly once");
            group.TitleForQa("  MTK Ekibi  ");
            Require(group.GroupTitle == "MTK Ekibi" && group.ConfirmEnabledForQa, "A trimmed non-empty title and one member enable creation");
            group.TitleForQa("Tek\nSatır");
            Require(!group.ConfirmEnabledForQa && !group.ValidateForQa(), "Control characters cannot bypass the single-line group-title rule");
            group.TitleForQa("MTK Ekibi");
            group.SearchForQa("Zeynep"); Pump(group);
            Require(group.PeopleForQa.FilteredCountForQa == 1 && group.Selected.Contains(users[1].Id),
                "Searching does not remove a selected member hidden by the filter");
            group.SearchForQa("bulunmayan"); Pump(group);
            Require(group.PeopleForQa.FilteredCountForQa == 0 && group.PeopleForQa.VisibleRowCountForQa == 0 && group.Selected.Length == 1,
                "No results displays the empty state without leaking controls or losing selection");
            group.SearchForQa(""); Pump(group);
            Require(group.PeopleForQa.RowsForQa.First(row => row.User?.Id == users[1].Id).SelectorForQa.Checked,
                "Clearing search restores the checked state by user ID");
            group.ClearForQa();
            Require(group.Selected.Length == 0 && !group.ConfirmEnabledForQa, "Clear resets visible and hidden choices");
            group.SelectForQa(users[0].Id, true); group.SelectForQa(users[2].Id, true);
            foreach (var size in new[] { new Size(430, 520), new Size(560, 740), new Size(720, 860) })
            {
                group.Size = size; Pump(group);
                Require(group.PeopleForQa.ClientSize.Height > 0 && group.PeopleForQa.ClientSize.Width > 0,
                    $"Member viewport retains a usable area at {size}");
                foreach (var row in group.PeopleForQa.RowsForQa)
                {
                    var selector = row.SelectorForQa;
                    Require(row.ClientRectangle.Contains(selector.Bounds) &&
                        row.Controls.OfType<Label>().All(label => label.Right <= selector.Left),
                        $"Name, role and SVG selection do not overlap at {size}, user {row.User!.DisplayName}");
                }
            }
            group.Size = new Size(560, 740); Pump(group); Capture(group, Path.Combine(outputDirectory, "conversation-picker-group.png"));
            group.Hide();
        }
        using (var direct = new ConversationPickerForm(users, false) { ShowInTaskbar = false })
        {
            direct.Show(); Pump(direct);
            direct.SelectForQa(users[0].Id, true); direct.SelectForQa(users[1].Id, true);
            Require(direct.Selected.SequenceEqual([users[1].Id]) && direct.ConfirmEnabledForQa,
                "Direct conversation selection replaces the prior person instead of creating a group");
            direct.SearchForQa("Zeynep"); Pump(direct);
            Require(direct.Selected.SequenceEqual([users[1].Id]), "Direct selection also survives a temporary search filter");
            direct.SearchForQa(""); Pump(direct);
            Require(Descendants(direct).OfType<CheckEdit>().All(edit => edit.Properties.CheckBoxOptions.Style == CheckBoxStyle.SvgRadio2),
                "Direct selection has distinct SVG radio affordances");
            Capture(direct, Path.Combine(outputDirectory, "conversation-picker-direct.png")); direct.Hide();
        }
        using (var large = new ConversationPickerForm(Enumerable.Range(1, 3000).Select(value => User(value)), true) { ShowInTaskbar = false })
        {
            large.Show(); Pump(large); large.TitleForQa("Kalabalık grup");
            Require(large.PeopleForQa.VisibleRowCountForQa is > 0 and < 20 && large.PeopleForQa.FilteredCountForQa == 3000,
                "A 3,000-account directory renders a bounded visible control pool");
            foreach (var user in Enumerable.Range(1, 50).Select(value => User(value))) large.SelectForQa(user.Id, true);
            Require(large.Selected.Length == 49 && large.ValidateForQa(), "A fiftieth member is rejected without losing the valid first 49");
            large.TitleForQa(new string('a', 81));
            Require(Descendants(large).OfType<TextEdit>().Any(edit => edit.AccessibleName == "Grup adı" && edit.Properties.MaxLength == 80) &&
                (large.GroupTitle.Length <= 80 || !large.ValidateForQa()),
                "Typing/pasting is bounded to 80 characters and an oversized programmatic title cannot be confirmed");
            large.PeopleForQa.ScrollToEndForQa(); Pump(large);
            Require(large.PeopleForQa.RowsForQa.Any(row => row.User?.Id == User(3000).Id) && large.Selected.Length == 49,
                "Recycled rows reach the final account without dropping hidden selections");
            Require(large.PeopleForQa.NavigateForQa(User(3000).Id, Keys.Home), "Keyboard Home navigation reaches the directory start");
            Pump(large);
            Require(large.PeopleForQa.RowsForQa.First().User?.Id == User(1).Id && large.PeopleForQa.RowsForQa.First().SelectorForQa.Focused,
                "Keyboard navigation scrolls and focuses the correct real DevExpress editor");
            var child = large.PeopleForQa.RowsForQa.First(); large.Close();
            Require(child.IsDisposed, "Closing the picker releases its recycled rows and avatar owners");
        }
        return checks;
    }
    private static void Pump(Form form)
    { for (var iteration = 0; iteration < 3; iteration++) { form.PerformLayout(); Application.DoEvents(); form.Update(); } }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void Capture(Control control, string path)
    { using var bitmap = new Bitmap(control.Width, control.Height); control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(path, ImageFormat.Png); }
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint parameter, nint data);
}
