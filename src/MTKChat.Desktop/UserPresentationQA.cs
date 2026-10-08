using System.Globalization;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class UserPresentationQA
{
    internal static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Presence presentation regression: " + check);
            checks.Add(check);
        }

        static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
        {
            var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        var now = LocalTime(2026, 10, 4, 13, 30);
        var person = new PresenceView(new ChatUser(Guid.NewGuid(), "QA User", "qa@example.invalid", false, null),
            false, true, false, LocalTime(2026, 10, 1, 9, 7));
        Require(UserPresentation.PresenceText(person, now) == "Son görülme 01.10.2026 09:07",
            "An older last-seen date includes its full day, month, year and time");
        Require(UserPresentation.PresenceText(person with { LastSeenAt = LocalTime(2025, 1, 1, 0, 5) }, now) ==
            "Son görülme 01.01.2025 00:05", "A prior-year date retains its actual year and zero-padded fields");
        Require(UserPresentation.PresenceText(person with { LastSeenAt = LocalTime(2026, 10, 4, 9, 7) }, now) ==
            "Son görülme bugün 09:07", "Today's relative label is unchanged");
        Require(UserPresentation.PresenceText(person with { LastSeenAt = LocalTime(2026, 10, 3, 9, 7) }, now) ==
            "Son görülme dün 09:07", "Yesterday's relative label is unchanged");
        Require(UserPresentation.PresenceText(person with { LastSeenAt = LocalTime(2025, 12, 31, 23, 55) },
            LocalTime(2026, 1, 1, 0, 15)) == "Son görülme dün 23:55",
            "Yesterday remains correct across a year boundary");
        Require(UserPresentation.PresenceText(person with { LastSeenAt = person.LastSeenAt!.Value.ToUniversalTime() }, now) ==
            "Son görülme 01.10.2026 09:07", "UTC presence is displayed in the device's local timezone");
        Require(UserPresentation.PresenceText(person with { LastSeenAt = null }, now) == "Çevrimdışı",
            "Hidden or unavailable last-seen data never invents a date");
        Require(UserPresentation.PresenceText(person with { IsOnline = true }, now) == "●  Çevrimiçi",
            "Online status still takes precedence over last-seen data");
        Require(UserPresentation.PresenceText(person with { IsOnline = true, IsViewingConversation = true }, now) == "●  Bu sohbette",
            "Viewing the conversation still takes precedence over online status");
        Require(UserPresentation.PresenceText(person with { User = person.User with { IsAgent = true }, IsOnline = true }, now) ==
            "Yapay zekâ", "Agents still use their own presence label");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Require(UserPresentation.PresenceText(person, now) == "Son görülme 01.10.2026 09:07",
                "The requested numeric Gregorian date is stable with a non-Gregorian current culture");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        return checks;
    }
}
