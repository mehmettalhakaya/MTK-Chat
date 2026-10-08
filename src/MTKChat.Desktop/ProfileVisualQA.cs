using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Synthetic editor-only visual probes. They never open a photo picker, stage a
// real image, contact a server, or use a production user's profile metadata.
internal static class ProfileVisualQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool valid, string text)
        {
            if (!valid) throw new InvalidOperationException("Profile visual QA: " + text);
            checks.Add(text);
        }
        using var handler = new NoProfileNetworkHandler();
        using var api = new ChatApiClient("https://profile-visual.invalid/chat/", handler);
        var user = new ChatUser(Guid.NewGuid(), "deniz", "deniz@example.invalid", false, null, "user",
            FirstName: "Deniz", LastName: "Öz Kaya");
        foreach (var width in new[] { 420, 300 })
        {
            using var window = new Form { ClientSize = new Size(width, 900), BackColor = Theme.Sidebar,
                Text = "Synthetic profile layout", AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96, 96) };
            using var editor = new ProfileEditor(api, user, new AvatarCache(api), snapshotMode: true) { Dock = DockStyle.Fill };
            window.Controls.Add(editor); window.Show(); Layout();
            Require(!Descendants(editor).Any(control => control is Label &&
                (control.Text.Contains("JPEG", StringComparison.OrdinalIgnoreCase) ||
                 control.Text.Contains("120 kare", StringComparison.OrdinalIgnoreCase))),
                $"The {width}px profile contains no JPEG/GIF format-or-size information label");
            Require(editor.ProfileCardsForQa.All(card => card.BorderColor == Theme.Divider &&
                    card.FillColor == ProfileEditor.ProfileCardFill && card.FillColor != Theme.Surface) &&
                !editor.ProfileCardsForQa[0].Bounds.IntersectsWith(editor.ProfileCardsForQa[1].Bounds),
                $"Identity/photo and full-name sections use separate non-overlapping themed cards at {width}px");
            Require(!editor.HorizontalScroll.Visible && !editor.PhotoButtonsForQa[2].Visible,
                $"The clean {width}px profile needs no horizontal scrollbar or idle disabled Save footer");
            ManagementActionQA.Verify(editor.PhotoButtonsForQa.Take(2).Append(editor.NameButtonsForQa[0]),
                Require, $"Profile read actions at {width}px / {editor.DeviceDpi} DPI");
            VerifyPersonalLabels();
            Capture("profile-polished-" + width + ".png");

            var longUser = user with { FirstName = "Mehmet Talha Uzun İsim", LastName = "Öz Kaya Uzun Soyisim" };
            editor.RefreshUser(longUser); Layout();
            VerifyPersonalLabels();
            VerifyNameFits("a multi-word full name");
            Require(editor.FullNameLabelForQa.Width > editor.NameButtonsForQa[0].Width &&
                !editor.FullNameLabelForQa.RectangleToScreen(editor.FullNameLabelForQa.ClientRectangle)
                    .IntersectsWith(editor.NameButtonsForQa[0].RectangleToScreen(editor.NameButtonsForQa[0].ClientRectangle)),
                $"Full name uses its own full-width row below Düzenle, rather than competing with the button at {width}px");
            Capture("profile-polished-long-name-" + width + ".png");
            editor.RefreshUser(longUser with { FirstName = new string('A', 80), LastName = new string('B', 80) }); Layout();
            VerifyPersonalLabels();
            VerifyNameFits("two maximal 80-character fields");

            editor.BeginNameEdit(); editor.SetNameDraftForQa("Deniz", "Kaya"); Layout();
            // The drawer legitimately scrolls when the window is very short.
            // Ensure each real action can be exposed in full, not only that its
            // self-reported Bounds fit an already-clipped immediate parent.
            editor.ScrollControlIntoView(editor.NameButtonsForQa[1]); Layout();
            ManagementActionQA.Verify(editor.NameButtonsForQa.Skip(1), Require,
                $"Profile name-edit actions at {width}px / {editor.DeviceDpi} DPI");
            Require(editor.NameDraftForQa == ("Deniz", "Kaya") && editor.NameSaveEnabledForQa,
                $"Card reflow preserves the editable valid name draft at {width}px");
            Capture("profile-polished-edit-" + width + ".png");
            editor.CancelNameEdit(); editor.StageRemovalForQa(); Layout();
            editor.ScrollControlIntoView(editor.PhotoButtonsForQa[2]); Layout();
            ManagementActionQA.Verify([editor.PhotoButtonsForQa[2]], Require,
                $"Profile photo-save action at {width}px / {editor.DeviceDpi} DPI");
            Require(editor.SaveEnabledForQa && editor.PhotoButtonsForQa[2].Visible && handler.Requests == 0,
                $"A photo draft exposes its independent Save action without submitting a request at {width}px");
            window.Hide();

            void Layout() { editor.PerformLayout(); window.PerformLayout(); Application.DoEvents(); }
            void VerifyPersonalLabels()
            {
                var labels = editor.PersonalLabelsForQa;
                Require(labels[0].Text == "Kişisel bilgiler" && labels[1].Text == "Ad ve Soyad:",
                    $"The {width}px profile distinguishes its personal-information heading from the full-name field caption");
                foreach (var label in labels)
                {
                    var measured = TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal), int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    var screen = label.RectangleToScreen(label.ClientRectangle);
                    var editScreen = editor.NameButtonsForQa[0].RectangleToScreen(editor.NameButtonsForQa[0].ClientRectangle);
                    var nameScreen = editor.FullNameLabelForQa.RectangleToScreen(editor.FullNameLabelForQa.ClientRectangle);
                    Require(label.Visible && !label.AutoEllipsis &&
                        label.Parent!.ClientRectangle.Contains(label.Bounds) &&
                        label.ClientSize.Height - label.Padding.Vertical >= measured.Height &&
                        !screen.IntersectsWith(editScreen) && !screen.IntersectsWith(nameScreen),
                        $"The {width}px {label.Text} label fits its measured text without clipping or overlapping Düzenle / the name: " +
                        $"label={label.Bounds}, text={measured}, dpi={editor.DeviceDpi}");
                }
                Require(!labels[0].RectangleToScreen(labels[0].ClientRectangle)
                        .IntersectsWith(labels[1].RectangleToScreen(labels[1].ClientRectangle)),
                    $"The personal-information heading and Ad ve Soyad: caption occupy separate rows at {width}px");
            }
            void VerifyNameFits(string description)
            {
                var label = editor.FullNameLabelForQa;
                var measured = TextRenderer.MeasureText(label.Text, label.Font,
                    new Size(Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                Require(!label.AutoEllipsis && label.ClientSize.Height - label.Padding.Vertical >= measured &&
                    label.Parent!.ClientRectangle.Contains(label.Bounds),
                    $"The {width}px full-name field word-wraps {description} without ellipsis or overlap: " +
                    $"label={label.Bounds}, textHeight={measured}, dpi={editor.DeviceDpi}");
            }
            void Capture(string file)
            {
                using var image = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(image, new Rectangle(Point.Empty, window.Size));
                image.Save(Path.Combine(directory, file));
            }
        }
        Require(handler.Requests == 0, "All profile visual probes stay local and never write account, photo, privacy or name data");
        return checks;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class NoProfileNetworkHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Profile visual probes must never open a network request.");
        }
    }
}
