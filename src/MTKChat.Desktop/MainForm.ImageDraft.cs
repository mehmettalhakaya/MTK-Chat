using System.Security.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private sealed record PendingImage(byte[] Bytes, string MimeType, string Name);

    private PendingImage? _pendingImage;
    private readonly PictureBox _imageDraftPicture = new();
    private readonly Label _imageDraftName = new();

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _composerEmojiPicker?.Visible == true)
        { _composerEmojiPicker.Close(ToolStripDropDownCloseReason.Keyboard); return true; }
        if (keyData == (Keys.Control | Keys.K)) CloseComposerEmojis();
        if (keyData == Keys.Escape && _sidebarPage != SidebarPage.Chats)
        { CloseSidebarDrawer(); return true; }
        if (keyData == (Keys.Control | Keys.K) && _sidebarPage != SidebarPage.Chats)
        {
            CloseSidebarDrawer();
            if (_sidebarPage != SidebarPage.Chats) return true; // A save is in flight.
        }
        if (keyData == Keys.Escape && _messageInfoSelection is not null)
        {
            CloseMessageInfo(focusComposer: true);
            return true;
        }
        if (keyData == (Keys.Control | Keys.K)) { _premiumSearch.Focus(); return true; }
        // DevExpress may consume Ctrl+V as an editor command before KeyDown fires.
        if (keyData == (Keys.Control | Keys.V) && _premiumComposer.ContainsFocus && TryStageClipboardImage())
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Control BuildImageDraftPreview()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            Margin = new Padding(2, 0, 2, 8),
            Padding = new Padding(9),
            FillColor = Theme.SurfaceRaised,
            BorderColor = Theme.Divider,
            CornerRadius = 12
        };
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        _imageDraftPicture.Dock = DockStyle.Fill;
        _imageDraftPicture.SizeMode = PictureBoxSizeMode.Zoom;
        _imageDraftPicture.BackColor = Theme.Canvas;
        _imageDraftName.Dock = DockStyle.Fill;
        _imageDraftName.ForeColor = Theme.Text;
        _imageDraftName.Font = Theme.Font(9f);
        _imageDraftName.TextAlign = ContentAlignment.MiddleLeft;
        _imageDraftName.AutoEllipsis = true;
        _imageDraftName.Padding = new Padding(12, 0, 0, 0);
        var remove = Theme.Button("×", ButtonKind.Ghost);
        remove.Dock = DockStyle.Fill;
        remove.AccessibleName = "Görsel taslağını kaldır";
        new ToolTip().SetToolTip(remove, "Görseli kaldır");
        remove.Click += (_, _) => ClearPendingImage();
        row.Controls.Add(_imageDraftPicture, 0, 0);
        row.Controls.Add(_imageDraftName, 1, 0);
        row.Controls.Add(remove, 2, 0);
        card.Controls.Add(row);
        return card;
    }

    private bool TryStageClipboardImage()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                if (image is null) return false;
                using var stream = new MemoryStream();
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                StageImage(stream.ToArray(), "image/png", "Panodan görsel.png");
                return true;
            }

            if (!Clipboard.ContainsFileDropList()) return false;
            var path = Clipboard.GetFileDropList().Cast<string>()
                .FirstOrDefault(file => File.Exists(file) && ImageMimeType(file) is not null);
            if (path is null) return false;
            var info = new FileInfo(path);
            if (info.Length > 5 * 1024 * 1024)
            {
                ShowError("Görsel en fazla 5 MB olabilir.");
                return true;
            }
            StageImage(File.ReadAllBytes(path), ImageMimeType(path)!, info.Name);
            return true;
        }
        catch (Exception exception)
        {
            ShowError($"Panodaki görsel açılamadı: {exception.Message}");
            return true;
        }
    }

    private static string? ImageMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null
    };

    private void StageImage(byte[] bytes, string mimeType, string name)
    {
        ClearPendingFile();
        if (bytes.Length > 5 * 1024 * 1024)
        {
            CryptographicOperations.ZeroMemory(bytes);
            ShowError("Görsel en fazla 5 MB olabilir.");
            return;
        }

        Bitmap? preview = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var decoded = Image.FromStream(stream);
            if ((long)decoded.Width * decoded.Height > 40_000_000)
                throw new InvalidDataException("Görsel çözünürlüğü çok yüksek.");
            preview = new Bitmap(96, 72);
            using var graphics = Graphics.FromImage(preview);
            graphics.Clear(Theme.Canvas);
            var scale = Math.Min(96d / decoded.Width, 72d / decoded.Height);
            var width = Math.Max(1, (int)(decoded.Width * scale));
            var height = Math.Max(1, (int)(decoded.Height * scale));
            graphics.DrawImage(decoded, (96 - width) / 2, (72 - height) / 2, width, height);
        }
        catch when (mimeType == "image/webp")
        {
            // Some Windows installations cannot decode WebP; the encrypted upload still works.
        }
        catch
        {
            preview?.Dispose();
            CryptographicOperations.ZeroMemory(bytes);
            ShowError("Seçilen dosya açılamadı veya görsel çözünürlüğü çok yüksek.");
            return;
        }

        ClearPendingImage();
        ClearPendingVoice();
        _pendingImage = new PendingImage(bytes, mimeType, name);
        if (_imageDraftCard is not null) _imageDraftCard.Visible = true;
        _imageDraftPicture.Image = preview;
        _imageDraftName.Text = $"{name}  ·  {bytes.Length / 1024d:N0} KB";
        if (_imageDraftRowStyle is not null) _imageDraftRowStyle.Height = 100;
        if (_composerHeightStyle is not null) _composerHeightStyle.Height = ComposerBaseHeight + 100;
        _premiumComposer.Focus();
    }

    private void ClearPendingImage()
    {
        if (_pendingImage is not null) CryptographicOperations.ZeroMemory(_pendingImage.Bytes);
        _pendingImage = null;
        var preview = _imageDraftPicture.Image;
        _imageDraftPicture.Image = null;
        preview?.Dispose();
        _imageDraftName.Text = "";
        if (_imageDraftCard is not null) _imageDraftCard.Visible = false;
        if (_imageDraftRowStyle is not null) _imageDraftRowStyle.Height = 0;
        if (_composerHeightStyle is not null) _composerHeightStyle.Height = ComposerBaseHeight;
    }

    internal void PopulateImageDraftSnapshot()
    {
        using var sample = new Bitmap(180, 110);
        using (var graphics = Graphics.FromImage(sample))
        {
            using var circle = new SolidBrush(Theme.Success);
            graphics.Clear(Theme.Accent);
            graphics.FillEllipse(circle, 55, 20, 70, 70);
        }
        using var stream = new MemoryStream();
        sample.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        StageImage(stream.ToArray(), "image/png", "Panodan görsel.png");
    }
}
