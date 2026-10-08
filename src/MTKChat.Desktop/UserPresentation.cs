using MTKChat.Contracts;
using System.Globalization;

namespace MTKChat.Desktop;

internal static class UserPresentation
{
    internal static string Role(ChatUser user, string? groupRole = null) => user.IsAgent ? "Bot" : user.Role == "admin" ? "Admin" :
        groupRole == "admin" ? "Grup yöneticisi" : groupRole == "mod" ? "Mod" : "User";
    internal static string Heading(ChatUser user, string? groupRole = null) => $"{user.DisplayName} · {Role(user, groupRole)}";
    internal static Color RoleColor(ChatUser user, string? groupRole = null) => user.IsAgent ? Theme.Bot :
        user.Role == "admin" || groupRole == "admin" ? Theme.Warning : groupRole == "mod" ? Color.FromArgb(171, 132, 255) : Theme.Text;
    internal static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    internal static string PresenceText(PresenceView person) => PresenceText(person, DateTimeOffset.Now);

    // Keep the production clock at the call boundary so relative dates can be
    // checked deterministically without changing any user's presence state.
    internal static string PresenceText(PresenceView person, DateTimeOffset now)
    {
        if (person.User.IsAgent) return "Yapay zekâ";
        if (person.IsViewingConversation) return "●  Bu sohbette";
        if (person.IsOnline) return "●  Çevrimiçi";
        if (person.LastSeenAt is not { } seen) return "Çevrimdışı";
        var local = seen.ToLocalTime();
        var today = now.ToLocalTime().Date;
        return local.Date == today ? $"Son görülme bugün {local:HH:mm}" : local.Date == today.AddDays(-1) ?
            $"Son görülme dün {local:HH:mm}" :
            // A full Gregorian date avoids ambiguous years and remains stable
            // on computers configured with a different calendar or culture.
            $"Son görülme {local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }
}
