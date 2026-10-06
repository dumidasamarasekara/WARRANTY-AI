using System.Globalization;

namespace Warranty.Application.Claims;

/// <summary>
/// Reads the claim ETag a review decision sends back as <c>If-Match</c>: the claim's row version as
/// written by <see cref="ClaimQueries.FormatETag"/> (contracts/rest-api.openapi.yaml).
/// </summary>
public static class ClaimETag
{
    /// <summary>
    /// Reads an <c>If-Match</c> value: a strong tag (<c>"123"</c>) or the bare number. Weak tags,
    /// lists and <c>*</c> are not accepted, so they never match.
    /// </summary>
    public static bool TryParse(string? value, out uint rowVersion)
    {
        rowVersion = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return text.Length > 0
            && text.All(char.IsAsciiDigit)
            && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out rowVersion);
    }
}
