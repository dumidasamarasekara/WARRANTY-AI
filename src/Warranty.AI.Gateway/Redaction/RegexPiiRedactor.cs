using System.Text.RegularExpressions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;

namespace Warranty.AI.Gateway.Redaction;

/// <summary>
/// The PoC <see cref="IPiiRedactor"/> (research R15): pattern-based masking of email addresses and
/// phone numbers in free text. Numbers with an international prefix (<c>+47 …</c>, <c>0047 …</c>) are
/// always phone numbers; national numbers count only when written in phone shape — digit groups split
/// by spaces, dashes, dots or a bracketed area code — because bare digit runs, dates and amounts in
/// claim text are serial numbers, invoice numbers and prices the adjudication needs.
/// </summary>
public sealed partial class RegexPiiRedactor : IPiiRedactor
{
    private const int MinNationalDigits = 8;
    private const int MinInternationalDigits = 7;
    private const int MaxDigits = 15;

    public RedactionResult Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var replacements = 0;
        var redacted = EmailPattern().Replace(text, _ =>
        {
            replacements++;
            return CaseCustomerView.EmailPlaceholder;
        });
        redacted = PhonePattern().Replace(redacted, match =>
        {
            if (!IsPhoneNumber(match))
            {
                return match.Value;
            }

            replacements++;
            return CaseCustomerView.PhonePlaceholder;
        });

        return new RedactionResult(redacted, replacements);
    }

    private static bool IsPhoneNumber(Match match)
    {
        var value = match.Value;
        var digits = value.Count(char.IsAsciiDigit);
        if (match.Groups["intl"].Success)
        {
            if (value.StartsWith("00", StringComparison.Ordinal))
            {
                digits -= 2;
            }

            return digits is >= MinInternationalDigits and <= MaxDigits;
        }

        var groups = value.Split([' ', '.', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        return groups.Length > 1
               && digits is >= MinNationalDigits and <= MaxDigits
               && groups.All(group => group.Length > 1)
               && !DatePattern().IsMatch(value)
               && !AmountPattern().IsMatch(value);
    }

    [GeneratedRegex(@"(?<![\w.%+-])[A-Za-z0-9._%+-]+@(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    // Candidates only; IsPhoneNumber decides. Not glued to identifiers (SN-…, INV/…, a@…) on either side.
    [GeneratedRegex(
        @"(?<![\w+./@-])(?<intl>\+\d{1,3}[ .-]?|00\d{1,3}[ .-])?(?:\(\d{1,4}\)[ .-]?)?\d+(?:[ .-]\d+)*(?![\w/@-]|\.\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^(?:\d{4}[ .-]\d{2}[ .-]\d{2}|\d{2}[ .-]\d{2}[ .-]\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^[^.]*\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();
}
