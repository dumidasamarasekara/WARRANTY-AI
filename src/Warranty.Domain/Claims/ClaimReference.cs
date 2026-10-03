using System.Security.Cryptography;

namespace Warranty.Domain.Claims;

/// <summary>
/// Claimant-facing claim reference: 10 random Crockford base32 characters (50 bits), so
/// references cannot be enumerated (research R10). Unique per tenant.
/// </summary>
public static class ClaimReference
{
    public const int Length = 10;

    // Crockford base32: no I, L, O or U.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>Upper-cases user input and maps Crockford look-alikes (I/L → 1, O → 0) before validation.</summary>
    public static string Normalize(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Trim().ToUpperInvariant()
            .Replace('I', '1').Replace('L', '1').Replace('O', '0')
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    public static bool IsValid(string? reference)
        => reference is { Length: Length } && reference.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}
