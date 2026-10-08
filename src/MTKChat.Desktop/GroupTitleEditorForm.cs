using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed class GroupTitleEditorForm : ModernForm
{
    private readonly ChatApiClient _api;
    private readonly ConversationSummary _group;
    private readonly TextEdit _name = new();
    private readonly Label _status = new();
    private readonly ModernButton _save = Theme.Button("Kaydet", ButtonKind.Primary);
    private readonly ModernButton _cancel = Theme.Button("Vazgeç", ButtonKind.Secondary);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _lifetimeDisposed;
    internal GroupTitleResult? UpdatedTitle { get; private set; }

    internal GroupTitleEditorForm(ChatApiClient api, ConversationSummary group)
    {
        _api = api; _group = group;
        Text = "MTK Chat · Grup adı"; Icon = Theme.AppIcon();
        Size = new Size(510, 350); MinimumSize = new Size(440, 330);
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        BackColor = Theme.Sidebar; ForeColor = Theme.Text; Font = Theme.Font(10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(28, 22, 28, 22), BackColor = Theme.Sidebar };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 50, 28, 54 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.Controls.Add(new Label { Text = "Grup adını değiştir", Dock = DockStyle.Fill,
            Font = Theme.Font(19, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(new Label { Text = "Grup adı", Dock = DockStyle.Fill, ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        var frame = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 12, FillColor = Theme.Surface,
            BorderColor = Theme.Divider, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 4) };
        _name.Dock = DockStyle.Fill; _name.Text = group.Title;
        _name.Properties.MaxLength = 80; _name.Properties.BorderStyle = BorderStyles.NoBorder;
        _name.Properties.Appearance.BackColor = Theme.Surface; _name.Properties.Appearance.ForeColor = Theme.Text;
        _name.Properties.Appearance.Font = Theme.Font(11);
        _name.Properties.Appearance.Options.UseBackColor = _name.Properties.Appearance.Options.UseForeColor = _name.Properties.Appearance.Options.UseFont = true;
        _name.Properties.AppearanceFocused.Assign(_name.Properties.Appearance);
        _name.AccessibleName = "Yeni grup adı";
        _name.EditValueChanged += (_, _) => { if (!_busy) { _status.Text = ""; ValidateName(); } };
        frame.Controls.Add(_name); layout.Controls.Add(frame, 0, 2);
        _status.Dock = DockStyle.Fill; _status.ForeColor = Theme.Danger;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.UseMnemonic = false;
        layout.Controls.Add(_status, 0, 3);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _cancel.Dock = _save.Dock = DockStyle.Fill;
        _cancel.Click += (_, _) => { if (!_busy) { DialogResult = DialogResult.Cancel; Close(); } };
        _save.Click += async (_, _) => await SaveAsync();
        actions.Controls.Add(_cancel, 1, 0); actions.Controls.Add(_save, 2, 0); layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout); AcceptButton = _save; CancelButton = _cancel;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
        ValidateName();
    }

    private void ValidateName() => _save.Enabled = !_busy && !string.IsNullOrWhiteSpace(_name.Text) &&
        _name.Text.Trim().Length <= 80 && !_name.Text.Any(char.IsControl) && _name.Text.Trim() != _group.Title;

    internal async Task SaveAsync()
    {
        ValidateName();
        if (!_save.Enabled || _busy) return;
        _busy = true; _save.Enabled = _cancel.Enabled = _name.Enabled = false;
        _status.ForeColor = Theme.Muted; _status.Text = "Kaydediliyor…";
        try
        {
            var result = await _api.RenameGroupAsync(_group.Id, _name.Text.Trim(), _lifetime.Token);
            if (IsDisposed) return;
            if (result.ConversationId != _group.Id || string.IsNullOrWhiteSpace(result.Title) || result.Title.Length > 80 || result.Title.Any(char.IsControl))
                throw new InvalidOperationException("Sunucudan geçersiz grup bilgisi geldi.");
            UpdatedTitle = result;
            _busy = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex)
        {
            // A rejected/failed write stays in the editor, with the draft intact.
            if (!IsDisposed) { _status.ForeColor = Theme.Danger; _status.Text = ex.Message; }
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) { _cancel.Enabled = _name.Enabled = true; ValidateName(); }
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string NameForQa { get => _name.Text; set => _name.Text = value; }
    internal bool SaveEnabledForQa => _save.Enabled;
    internal string StatusForQa => _status.Text;
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed) { _lifetimeDisposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
