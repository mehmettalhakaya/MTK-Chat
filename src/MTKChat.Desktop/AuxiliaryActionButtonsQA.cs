using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

// Exercise only synthetic, snapshot-mode forms: no call session, microphone,
// production account, real HTTP request or destructive PerformClick is needed.
internal static class AuxiliaryActionButtonsQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException("Auxiliary action QA: " + text);
            checks.Add(text);
        }
        using var api = new ChatApiClient("https://auxiliary-action-qa.invalid/chat/");
        var admin = new ChatUser(Guid.NewGuid(), "QA Yöneticisi", "qa@invalid.test", false, null, "admin");
        var guest = admin with { Id = Guid.NewGuid(), DisplayName = "QA Üyesi", Role = "user" };
        var room = new ConversationSummary(Guid.NewGuid(), "QA Grubu", [admin, guest], "", DateTimeOffset.UtcNow, 0);
        using (var management = new UserManagementForm(api, admin, room, snapshotMode: true))
        {
            management.Show(); management.PopulateSnapshot(); Application.DoEvents();
            var commands = Descendants(management).OfType<ModernButton>().ToArray();
            Require(commands.Length == 11 && commands.Where(button => button.Text is "Banla" or "Chat · yasakla")
                .All(button => button.Kind == ButtonKind.Secondary && button.ForeColor == Theme.Danger),
                "Both global/chat destructive user commands have a visible secondary shape and red ink");
            foreach (var compact in new[] { false, true })
            {
                if (compact) management.Size = management.MinimumSize;
                management.PerformLayout(); Application.DoEvents();
                foreach (var button in commands)
                {
                    // The action rail intentionally scrolls horizontally; inspect
                    // each reachable command after exposing its whole rectangle.
                    var strip = (FlowLayoutPanel)button.Parent!;
                    strip.ScrollControlIntoView(button); Application.DoEvents();
                    ManagementActionQA.Verify([button], Require, (compact ? "Compact" : "Normal") + " user management · " + button.Text);
                }
                Capture(management, compact ? "admin-actions-compact.png" : "admin-actions.png", directory);
            }
            management.Hide();
        }
        using var device = DeviceIdentity.Create();
        var call = new CallView(Guid.NewGuid(), room.Id, room.Title, guest.Id, DateTimeOffset.UtcNow, [admin, guest], []);
        using (var incoming = new CallForm(api, device, admin.Id, call, incoming: true, snapshot: true))
        {
            incoming.Show(); Application.DoEvents();
            // Incoming calls now expose only accept/reject. The new CallUIQA
            // separately checks vector targets, visible captions and active controls.
            var commands = CallUIQA.Commands(incoming);
            Require(commands.Length == 2 && commands.Any(button => button.Text == "Reddet" && button.Kind == ButtonKind.Danger)
                && commands.Any(button => button.Text == "Kabul et" && button.Kind == ButtonKind.Primary),
                "Incoming call has visible distinct accept/reject vector targets with captions");
            Capture(incoming, "incoming-call-actions.png", directory);
            incoming.Hide();
        }
        return checks;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Capture(Form form, string name, string directory)
    {
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
    }
}
