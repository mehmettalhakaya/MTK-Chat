using System.Globalization;
using System.Text.RegularExpressions;

namespace MTKChat.Desktop;

// This parser is UI-independent so the shared HTTP client's non-Windows tests
// exercise the same strict domain/path/fragment validation as the desktop form.
internal static partial class GroupInviteLink
{
    // The secret lives in the fragment, so opening a link cannot put it in the
    // website's normal request/access log. Never accept arbitrary external URLs.
    [GeneratedRegex(@"\A(?i:https://mtkaya\.me)/chat/invite/#(?<token>[A-Za-z0-9_-]{43})\z", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    internal static bool TryParse(string? link, out string token)
    {
        token = "";
        if (link is null || link.Length > 512) return false;
        var trimmed = link.Trim();
        var match = LinkPattern().Match(trimmed);
        if (!match.Success || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "mtkaya.me" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.AbsolutePath != "/chat/invite/") return false;
        token = match.Groups["token"].Value;
        return true;
    }

    internal static void RequireToken(string token)
    {
        if (token is null || !TokenPattern().IsMatch(token))
            throw new ArgumentException("Geçerli bir MTK Chat davet bağlantısı gerekli.", nameof(token));
    }

    internal static bool ValidGroupTitle(string? title) => !string.IsNullOrWhiteSpace(title) &&
        title.Length <= 80 && !title.Any(char.IsControl);
    // A far-future compatibility timestamp alone is not an unlimited grant.
    // Reject inconsistent flag/date pairs before formatting or enabling actions.
    internal static bool ValidExpiry(DateTimeOffset expiry, bool neverExpires = false) => neverExpires
        ? expiry == DateTimeOffset.MaxValue : expiry != default && expiry != DateTimeOffset.MaxValue;
    internal static bool Active(DateTimeOffset expiry, bool neverExpires = false) =>
        ValidExpiry(expiry, neverExpires) && (neverExpires || expiry > DateTimeOffset.UtcNow);
    internal static string ExpiryText(DateTimeOffset expiry, bool neverExpires = false) => neverExpires
        ? "Geçerlilik: Sınırsız"
        : "Son kullanım: " + expiry.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}
