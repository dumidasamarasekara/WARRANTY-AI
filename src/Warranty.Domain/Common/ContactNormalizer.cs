using System.Text;

namespace Warranty.Domain.Common;

/// <summary>
/// Normalizes claimant contact details for storage and comparison (FR-037a: claimant access
/// matches the reference plus the email or phone given at submission).
/// </summary>
public static class ContactNormalizer
{
    /// <summary>Trimmed, lower-case email address.</summary>
    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// E.164-style phone number: a leading <c>+</c> followed by digits only, with all spaces,
    /// dashes, dots and brackets removed. Numbers without a leading <c>+</c> keep their digits.
    /// </summary>
    public static string NormalizePhone(string phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        var trimmed = phone.Trim();
        var builder = new StringBuilder(trimmed.Length);
        if (trimmed.StartsWith('+'))
        {
            builder.Append('+');
        }

        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
