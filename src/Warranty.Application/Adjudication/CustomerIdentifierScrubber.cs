using System.Text;
using System.Text.RegularExpressions;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Crm;

namespace Warranty.Application.Adjudication;

/// <summary>
/// Replaces one customer's known identifiers in free text with the <see cref="CaseCustomerView"/>
/// placeholders (FR-006a, research R28): the email addresses, phone numbers (as typed or normalized,
/// with or without the international prefix), the street address (with or without the house number
/// and street type) and the name (in full and each part of it). Matching ignores case and stops at
/// letters and digits, so <c>Ann</c> does not match inside <c>Annual</c>. The gateway's pattern
/// redactor still masks other emails and phone numbers; this class covers what patterns cannot know.
/// </summary>
public sealed class CustomerIdentifierScrubber
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Before = @"(?<![\p{L}\p{Nd}])";
    private const string After = @"(?![\p{L}\p{Nd}])";
    private const string WordSeparator = @"[\s,_]+";
    private const string DigitSeparator = @"[\s().\-/]*";
    private const string HouseNumber = @"[\p{L}\p{Nd}/\-]*\d[\p{L}\p{Nd}/\-]*";

    /// <summary>Phone suffixes shorter than this are not matched on their own (too likely to be other numbers).</summary>
    private const int MinPhoneDigits = 7;

    /// <summary>Name parts shorter than this (initials) are not matched on their own.</summary>
    private const int MinNamePartLength = 2;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> PlaceholderWords = new(["CUSTOMER", "EMAIL", "PHONE", "ADDRESS"], StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<(Regex Pattern, string Placeholder)> _rules;

    private CustomerIdentifierScrubber(IReadOnlyList<(Regex Pattern, string Placeholder)> rules) => _rules = rules;

    /// <summary>A scrubber for the customer's master data plus the contact details given on the claim.</summary>
    public static CustomerIdentifierScrubber For(Customer? customer, string? contactEmail = null, string? contactPhone = null)
    {
        string?[] emails = [customer?.Email, contactEmail];
        string?[] phones = [customer?.Phone, contactPhone];
        var rules = new List<(Regex, string)>();
        rules.AddRange(Distinct(emails).OrderByDescending(e => e.Length)
            .Select(e => (Build(Regex.Escape(e)), CaseCustomerView.EmailPlaceholder)));
        rules.AddRange(Distinct(phones).Select(PhonePattern).OfType<string>()
            .Select(p => (Build(p), CaseCustomerView.PhonePlaceholder)));
        if (AddressPattern(customer?.AddressLine) is { } address)
        {
            rules.Add((Build(address), CaseCustomerView.AddressPlaceholder));
        }

        if (NamePattern(customer?.FullName) is { } name)
        {
            rules.Add((Build(name), CaseCustomerView.NamePlaceholder));
        }

        return new CustomerIdentifierScrubber(rules);
    }

    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (pattern, placeholder) in _rules)
        {
            text = pattern.Replace(text, placeholder);
        }

        return text;
    }

    private static Regex Build(string pattern) => new(pattern, Options, MatchTimeout);

    private static IEnumerable<string> Distinct(IEnumerable<string?> values)
        => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The phone's digits with any separators between them, optionally preceded by <c>+</c>, <c>00</c> or a
    /// trunk <c>0</c>; trailing parts of at least <see cref="MinPhoneDigits"/> digits match too, so a
    /// number written without its country or area code is still found.
    /// </summary>
    private static string? PhonePattern(string phone)
    {
        var digits = phone.Where(char.IsAsciiDigit).ToArray();
        if (digits.Length == 0)
        {
            return null;
        }

        var shortest = Math.Min(MinPhoneDigits, digits.Length);
        var suffixes = Enumerable.Range(0, digits.Length - shortest + 1)
            .Select(start => string.Join(DigitSeparator, digits[start..].Select(d => d.ToString())));
        return $@"(?<![\d+])(?:\+\s*|00\s*|0)?\(?(?:{string.Join('|', suffixes)})(?!\d)";
    }

    /// <summary>
    /// The street address as written, or its street name with an optional house number before or after it.
    /// A street name of three or more words also matches without its last word (the street type).
    /// </summary>
    private static string? AddressPattern(string? addressLine)
    {
        var tokens = Tokens(addressLine, ' ', ',');
        if (tokens.Length == 0)
        {
            return null;
        }

        var alternatives = new List<string> { string.Join(WordSeparator, tokens.Select(Regex.Escape)) };
        var street = tokens.Where(t => !t.Any(char.IsAsciiDigit)).Select(Regex.Escape).ToArray();
        if (street.Length > 0)
        {
            var name = street.Length >= 3
                ? $"{string.Join(WordSeparator, street[..^1])}(?:{WordSeparator}{street[^1]})?"
                : string.Join(WordSeparator, street);
            alternatives.Add($"(?:{HouseNumber}{WordSeparator})?{name}(?:{WordSeparator}{HouseNumber})?");
        }

        return Words(alternatives);
    }

    /// <summary>The full name, each whitespace-separated part, and each sub-part of hyphenated or apostrophe names.</summary>
    private static string? NamePattern(string? fullName)
    {
        var words = Tokens(fullName, ' ');
        if (words.Length == 0)
        {
            return null;
        }

        var parts = words
            .Concat(words.SelectMany(w => Tokens(w, '-', '\'', '’', '.')))
            .Where(p => p.Length >= MinNamePartLength && !PlaceholderWords.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length)
            .Select(Regex.Escape);
        return Words([string.Join(WordSeparator, words.Select(Regex.Escape)), .. parts]);
    }

    private static string[] Tokens(string? value, params char[] separators)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([.. separators, '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Words(IEnumerable<string> alternatives)
    {
        var builder = new StringBuilder(Before).Append("(?:");
        builder.AppendJoin('|', alternatives);
        return builder.Append(')').Append(After).ToString();
    }
}
