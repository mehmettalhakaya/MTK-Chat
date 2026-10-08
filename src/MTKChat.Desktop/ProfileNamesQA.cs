using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class ProfileNamesQA
{
    internal static IReadOnlyList<string> Verify(string directory) =>
        [.. MainForm.VerifyProfileNames(directory), .. ProfileVisualQA.Verify(directory)];
}

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyProfileNames(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool valid, string text)
        {
            if (!valid) throw new InvalidOperationException("Profile names QA: " + text);
            checks.Add(text);
        }
        var user = new ChatUser(Guid.NewGuid(), "mtk-example", "profile-example@invalid.test", false, null, "user");
        using var handler = new ProfileNameQaHandler(user);
        using var api = new ChatApiClient("https://profile-name-qa.invalid/chat/", handler);
        using var editor = new ProfileEditor(api, user, new AvatarCache(api), snapshotMode: true);
        using var window = new Form { Text = "Profile names synthetic QA", ClientSize = new Size(300, 690), BackColor = Theme.Sidebar };
        editor.Dock = DockStyle.Fill; window.Controls.Add(editor); window.Show(); Application.DoEvents();
        Require(editor.PersonalLabelsForQa[0].Text == "Kişisel bilgiler" &&
            editor.PersonalLabelsForQa[1].Text == "Ad ve Soyad:" &&
            editor.PersonalLabelsForQa.All(label => label.Visible),
            "The profile read card uses Kişisel bilgiler and an explicit Ad ve Soyad: field label, even without a saved name");
        Require(editor.FullNameForQa == "" && !editor.NameEditingForQa,
            "Missing first/last names leave an honestly blank full-name field, not the username");
        Require(editor.NameButtonsForQa[0].Text == "Düzenle" && editor.NameButtonsForQa[0].Visible &&
            editor.NameButtonsForQa[0].Kind == ButtonKind.Secondary,
            "A visible surfaced Düzenle button opens name entry when the account has no full name");
        Require(!editor.NameSaveEnabledForQa && handler.NameWrites == 0,
            "Opening the profile never creates names or silently writes to the account");
        Capture(window, "profile-names-empty.png");
        editor.NameButtonsForQa[0].PerformClick(); Application.DoEvents();
        Require(editor.NameEditingForQa && editor.NameDraftForQa == ("", "") && !editor.NameSaveEnabledForQa,
            "The actual Düzenle action opens two blank DevExpress name fields");
        var editLabels = NameEditorLabels(editor).Where(label => label.Visible).Select(label => label.Text).ToArray();
        Require(editLabels.Contains("Kişisel bilgiler") && editLabels.Contains("Ad") && editLabels.Contains("Soyad") &&
            !editLabels.Contains("İsim soyisim"),
            "The editable personal-information card retains the section title and labels the separate Ad / Soyad inputs consistently");
        foreach (var invalid in new[] { ("Deniz", ""), ("123", "Kaya"), ("🙂", "Kaya"), ("-", "Kaya"), (new string('A', 81), "Kaya"), ("De\u200bniz", "Kaya") })
        {
            editor.SetNameDraftForQa(invalid.Item1, invalid.Item2);
            Require(!editor.NameSaveEnabledForQa, $"Invalid first/last-name draft cannot enable save: {invalid.Item1.Length}/{invalid.Item2.Length} characters");
        }
        editor.SetNameDraftForQa("  Deniz  ", "  Öz   Kaya  ");
        Require(editor.NameSaveEnabledForQa && editor.NameButtonsForQa.Skip(1).All(button => button.Visible &&
            button.Parent!.ClientRectangle.Contains(button.Bounds)),
            "Valid Unicode names expose fully contained surfaced Save/Cancel buttons at a 300px drawer width");
        Capture(window, "profile-names-edit.png");
        editor.NameButtonsForQa[2].PerformClick();
        Require(!editor.NameEditingForQa && editor.FullNameForQa == "" && handler.NameWrites == 0,
            "Cancel discards only the name draft and sends no request");

        var named = user with { FirstName = "Mehmet Talha", LastName = "Kaya" };
        handler.User = named; editor.RefreshUser(named);
        Require(editor.FullNameForQa == "Mehmet Talha Kaya" && editor.HeadingForQa == "mtk-example · User",
            "Existing full name is shown separately while the username and role remain unchanged");
        Capture(window, "profile-names-present.png");
        editor.BeginNameEdit();
        Require(editor.NameDraftForQa == ("Mehmet Talha", "Kaya") && !editor.NameSaveEnabledForQa,
            "Editing an existing full name loads its exact values without enabling a no-op save");
        editor.RefreshUser(named with { FirstName = "Başka", LastName = "Oturum" });
        Require(editor.NameDraftForQa == ("Mehmet Talha", "Kaya") && editor.NameSaveEnabledForQa && handler.NameWrites == 0,
            "A refresh changing the confirmed name enables a previously unchanged draft without silently writing it");
        editor.RefreshUser(named);
        Require(editor.NameDraftForQa == ("Mehmet Talha", "Kaya") && !editor.NameSaveEnabledForQa && handler.NameWrites == 0,
            "A refresh matching the preserved draft disables a newly redundant name save without rewriting its fields");
        editor.SetNameDraftForQa("  Deniz  ", "  Öz   Kaya  ");
        var refreshed = named with { FirstName = "Başka", LastName = "Oturum", Role = "admin" };
        handler.User = refreshed; editor.RefreshUser(refreshed);
        Require(editor.NameDraftForQa == ("  Deniz  ", "  Öz   Kaya  ") && editor.NameSaveEnabledForQa &&
            editor.HeadingForQa == "mtk-example · Admin",
            "A background account refresh updates metadata but preserves an open name draft");
        var publishes = 0; ChatUser? published = null;
        editor.PhotoSaved += saved => { publishes++; published = saved; };
        handler.FailName = true; HistoryQaPump(editor.SaveNameAsync());
        Require(handler.NameWrites == 1 && handler.LastNameRequest == new ProfileNameChangeRequest("Deniz", "Öz Kaya"),
            "The authenticated own-profile PUT sends canonical first/last names, not a target account or username rename");
        Require(editor.NameEditingForQa && editor.NameDraftForQa == ("  Deniz  ", "  Öz   Kaya  ") &&
            editor.NameSaveEnabledForQa && !editor.IsBusy && publishes == 0 && editor.NameStatusForQa.Contains("Kaydedilemedi"),
            "A rejected name write stays inline with the exact draft, unlocked retry, and no false profile update");

        handler.FailName = false;
        handler.NameResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = editor.SaveNameAsync(); HistoryQaUntil(() => handler.NameWrites == 2);
        HistoryQaPump(editor.SaveNameAsync()); editor.CancelNameEdit();
        Require(editor.IsBusy && !editor.NameSaveEnabledForQa && editor.NameEditingForQa &&
            editor.NameButtonsForQa.All(button => !button.Enabled) && handler.NameWrites == 2,
            "A pending name save disables all name actions and cannot be canceled, duplicated, or replaced by a second write");
        editor.RefreshUser(refreshed with { Email = "new-metadata@invalid.test" });
        Require(editor.NameDraftForQa == ("  Deniz  ", "  Öz   Kaya  "),
            "Even a refresh while the PUT is pending cannot erase typed names");
        handler.NameResponse.SetResult(true); HistoryQaPump(saving); handler.NameResponse = null;
        Require(!editor.IsBusy && !editor.NameEditingForQa && editor.FullNameForQa == "Deniz Öz Kaya" &&
            published is { FirstName: "Deniz", LastName: "Öz Kaya" } && publishes == 1,
            "One confirmed name response refreshes the visible full name and propagates the updated session exactly once");
        Require(handler.User.DisplayName == user.DisplayName && handler.PhotoDeletes == 0 && handler.PrivacyWrites == 0,
            "Changing a full name never changes username, photo, privacy, or role through another endpoint");

        editor.BeginNameEdit(); editor.SetNameDraftForQa("Taslak", "Soyadı");
        editor.StageRemovalForQa(); HistoryQaPump(editor.SaveAsync());
        Require(handler.PhotoDeletes == 1 && editor.NameDraftForQa == ("Taslak", "Soyadı") && editor.NameEditingForQa &&
            editor.NameSaveEnabledForQa && handler.NameWrites == 2,
            "Saving a staged photo removal independently preserves the unsaved full-name draft");
        editor.SetMode(privacy: true); editor.ChangePrivacyForQa(false, false); HistoryQaPump(editor.SaveAsync());
        Require(handler.PrivacyWrites == 1 && handler.LastPrivacy == new PrivacySettings(false, false) &&
            editor.NameDraftForQa == ("Taslak", "Soyadı") && handler.NameWrites == 2,
            "Saving privacy writes only privacy and retains a separately staged name draft");
        editor.SetMode(privacy: false); editor.CancelNameEdit();
        Require(editor.FullNameForQa == "Deniz Öz Kaya" && editor.NameDraftForQa == ("Deniz", "Öz Kaya"),
            "Cancel restores the last confirmed full name after independent photo/privacy writes");
        var originalFullName = editor.FullNameForQa;
        editor.RefreshUser(handler.User with { Id = Guid.NewGuid(), FirstName = "Başka", LastName = "Hesap" });
        Require(editor.FullNameForQa == originalFullName,
            "A refresh belonging to another account is ignored rather than replacing the current profile");

        using (var malformedHandler = new ProfileNameQaHandler(user) { WrongId = true })
        using (var malformedApi = new ChatApiClient("https://profile-malformed-qa.invalid/chat/", malformedHandler))
        using (var malformed = new ProfileEditor(malformedApi, user, new AvatarCache(malformedApi), snapshotMode: true))
        {
            malformed.BeginNameEdit(); malformed.SetNameDraftForQa("Deniz", "Kaya"); HistoryQaPump(malformed.SaveNameAsync());
            Require(malformed.NameEditingForQa && malformed.UpdatedUser is null && malformed.FullNameForQa == "" &&
                malformed.NameStatusForQa.Contains("geçersiz"),
                "A successful HTTP response for another account cannot replace the current profile");
        }
        using (var cancelHandler = new ProfileNameQaHandler(user) { NameResponse = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        using (var cancelApi = new ChatApiClient("https://profile-cancel-qa.invalid/chat/", cancelHandler))
        {
            var closing = new ProfileEditor(cancelApi, user, new AvatarCache(cancelApi), snapshotMode: true);
            closing.BeginNameEdit(); closing.SetNameDraftForQa("Deniz", "Kaya");
            var pending = closing.SaveNameAsync(); HistoryQaUntil(() => cancelHandler.NameWrites == 1);
            closing.Dispose(); HistoryQaPump(pending);
            Require(cancelHandler.Canceled && pending.IsCompletedSuccessfully,
                "Disposing a pending name editor cancels HTTP and completes without an async exception or disposed control access");
        }
        window.Hide(); return checks;

        void Capture(Form form, string file)
        {
            form.PerformLayout(); Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(directory, file));
        }

        static IEnumerable<Label> NameEditorLabels(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is Label label) yield return label;
                foreach (var nested in NameEditorLabels(child)) yield return nested;
            }
        }
    }

    private sealed class ProfileNameQaHandler(ChatUser user) : HttpMessageHandler
    {
        internal ChatUser User = user;
        internal bool FailName, WrongId, Canceled;
        internal int NameWrites, PhotoDeletes, PrivacyWrites;
        internal ProfileNameChangeRequest? LastNameRequest;
        internal PrivacySettings? LastPrivacy;
        internal TaskCompletionSource<bool>? NameResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/chat/api/profile/name" && request.Method == HttpMethod.Put)
            {
                NameWrites++;
                LastNameRequest = await request.Content!.ReadFromJsonAsync<ProfileNameChangeRequest>(cancellationToken);
                if (NameResponse is { } pending)
                {
                    try { await pending.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { Canceled = true; throw; }
                }
                if (FailName) return new(HttpStatusCode.BadRequest)
                { Content = JsonContent.Create(new ApiError("profile_name_invalid", "İsim soyisim doğrulanamadı.")) };
                User = User with { FirstName = LastNameRequest!.FirstName, LastName = LastNameRequest.LastName };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(WrongId ? User with { Id = Guid.NewGuid() } : User) };
            }
            if (path == "/chat/api/profile/photo" && request.Method == HttpMethod.Delete)
            {
                PhotoDeletes++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(User) };
            }
            if (path == "/chat/api/profile/privacy" && request.Method == HttpMethod.Put)
            {
                PrivacyWrites++; LastPrivacy = await request.Content!.ReadFromJsonAsync<PrivacySettings>(cancellationToken);
                return new(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected profile name synthetic QA route.");
        }
    }
}
