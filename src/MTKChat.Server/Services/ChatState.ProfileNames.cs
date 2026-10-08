using System.Globalization;
using System.Text;
using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public enum ProfileNameChangeStatus { Success, InvalidName, Forbidden }

public sealed partial class ChatState
{
    private sealed record ProfileNameData(Guid UserId, string FirstName, string LastName);
    // Real names belong to the chat profile, not the site's username/role table.
    private readonly Dictionary<Guid, ProfileNameData> _profileNames = new();

    public ProfileNameChangeStatus SetProfileName(Guid ownerId, string? firstName, string? lastName, out ChatUser? updated)
    {
        updated = null;
        lock (_gate)
        {
            if (!_users.TryGetValue(ownerId, out var owner) || owner.IsAgent || _bannedUsers.Contains(ownerId))
                return ProfileNameChangeStatus.Forbidden;
            if (!TryNormalizeProfileName(firstName, out var first) || !TryNormalizeProfileName(lastName, out var last))
                return ProfileNameChangeStatus.InvalidName;
            var value = new ProfileNameData(ownerId, first, last);
            if (!_profileNames.TryGetValue(ownerId, out var previous) || previous != value)
            {
                _profileNames[ownerId] = value;
                // A failed snapshot write must not display a successful profile edit
                // or leave a value that a later unrelated write would persist.
                try { PersistUnsafe(); }
                catch
                {
                    if (previous is null) _profileNames.Remove(ownerId);
                    else _profileNames[ownerId] = previous;
                    throw;
                }
            }
            updated = _users[ownerId] = owner with { FirstName = first, LastName = last };
            return ProfileNameChangeStatus.Success;
        }
    }

    private static bool TryNormalizeProfileName(string? raw, out string value)
    {
        value = "";
        // Bound work before Unicode normalization; count final characters as
        // Unicode scalars so non-BMP letters are not penalized as two characters.
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 512) return false;
        string normalized;
        try { normalized = raw.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        var text = new StringBuilder(normalized.Length);
        var letters = 0; var count = 0; var space = false; var lastWasSeparator = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter)
            {
                letters++; space = false; lastWasSeparator = false;
            }
            else if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                if (count == 0 || lastWasSeparator) return false;
                space = false;
            }
            else if (category == UnicodeCategory.SpaceSeparator)
            {
                if (space) continue;
                space = true; lastWasSeparator = true;
                if (++count > 80) return false;
                text.Append(' ');
                continue;
            }
            else if (rune.Value is '-' or '\'' or 0x2019)
            {
                if (count == 0) return false;
                space = false; lastWasSeparator = true;
            }
            else return false; // No controls, invisible direction changes, markup, digits or emoji.
            if (++count > 80) return false;
            text.Append(rune.ToString());
        }
        if (letters == 0 || lastWasSeparator) return false;
        value = text.ToString();
        return true;
    }
}
