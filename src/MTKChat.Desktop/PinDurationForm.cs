namespace MTKChat.Desktop;

// A small themed policy chooser, not another editable copy of the message.
internal sealed class PinDurationForm : ModernForm
{
    internal int DurationHours { get; private set; } = 168;

    internal PinDurationForm()
    {
        Text = "MTK Chat · Mesajı sabitle";
        Icon = Theme.AppIcon();
        ClientSize = new Size(400, 310);
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Font(10);
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(new Label { Text = "Mesajı sabitle", Bounds = new Rectangle(26, 24, 348, 38),
            ForeColor = Theme.Text, Font = Theme.Font(19, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Sohbetin üstünde ne kadar kalsın?", Bounds = new Rectangle(27, 70, 346, 24),
            ForeColor = Theme.Muted, Font = Theme.Font(9.5f) });
        var choices = new List<(ModernButton Button, int Hours)>();
        foreach (var (text, hours) in new[] { ("24 saat", 24), ("7 gün", 168), ("30 gün", 720) })
        {
            var button = Theme.Button(text, hours == DurationHours ? ButtonKind.Primary : ButtonKind.Secondary);
            button.AccessibleName = text + " sabitleme süresi";
            button.SetBounds(26 + choices.Count * 119, 118, 110, 46);
            button.Click += (_, _) =>
            {
                DurationHours = hours;
                foreach (var choice in choices) choice.Button.Kind = choice.Hours == hours ? ButtonKind.Primary : ButtonKind.Secondary;
            };
            choices.Add((button, hours)); Controls.Add(button);
        }
        Controls.Add(new Label { Text = "En fazla 3 mesaj sabitlenebilir. Sabitlemeyi kaldırmak mesajı silmez.",
            Bounds = new Rectangle(27, 184, 346, 40), ForeColor = Theme.Muted, Font = Theme.Font(9) });
        var cancel = Theme.Button("Vazgeç", ButtonKind.Secondary);
        cancel.SetBounds(26, 246, 110, 42); cancel.DialogResult = DialogResult.Cancel;
        var save = Theme.Button("Sabitle", ButtonKind.Primary);
        save.SetBounds(146, 246, 228, 42); save.DialogResult = DialogResult.OK;
        Controls.AddRange([cancel, save]); AcceptButton = save; CancelButton = cancel;
        WrapFixedContent();
    }
}
