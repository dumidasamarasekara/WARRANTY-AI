using System.Text.RegularExpressions;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Guardrails.Rules;

/// <summary>Outcome of <see cref="ClaimantTextScreen.Screen"/>.</summary>
/// <param name="IsSafe"><see langword="true"/> when the text contains no disclosure term.</param>
/// <param name="OffendingTerm">The first offending term as it appears in the text; <see langword="null"/> when safe.</param>
public sealed record ClaimantTextScreenResult(bool IsSafe, string? OffendingTerm)
{
    /// <summary>The text contains no disclosure term.</summary>
    public static ClaimantTextScreenResult Safe { get; } = new(true, null);

    /// <summary>The text contains <paramref name="term"/>, the first offending term found.</summary>
    public static ClaimantTextScreenResult Offending(string term) => new(false, term);
}

/// <summary>
/// Deterministic screen for claimant-facing text (research R25): flags risk/fraud vocabulary, risk
/// signal codes and internal reference IDs (<c>EV-n</c>, <c>POL-n</c>, <c>GLB-n</c>). Used by the
/// <c>CLAIMANT_TEXT_SAFE</c> guardrail check on AI text and by reviewer decisions. Pure; no AI reference.
/// </summary>
public static class ClaimantTextScreen
{
    /// <summary>Risk/fraud vocabulary in normalized (lower-case, singular) form; matched case-insensitively, plural <c>s</c> allowed.</summary>
    private static readonly string[] Vocabulary =
    [
        "risk",
        "fraud",
        "suspicious",
        "manipulation",
        "reused",
        "duplicate",
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Pattern = BuildPattern();

    /// <summary>
    /// Screens <paramref name="text"/> and returns <see cref="ClaimantTextScreenResult.Safe"/> or the
    /// first (left-most) offending term, exactly as it appears in the text.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">Screening took longer than the match timeout.</exception>
    public static ClaimantTextScreenResult Screen(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var match = Pattern.Match(text);
        return match.Success ? ClaimantTextScreenResult.Offending(match.Value) : ClaimantTextScreenResult.Safe;
    }

    /// <summary>
    /// One alternation, so the left-most offending term wins regardless of its category. Signal codes
    /// (every <see cref="RiskSignalCode"/> wire name except the generic <c>OTHER</c>) and reference IDs
    /// are matched case-sensitively and whole (a word boundary does not split at <c>_</c>); vocabulary
    /// is matched case-insensitively as whole words.
    /// </summary>
    private static Regex BuildPattern()
    {
        var other = WireName.Of(RiskSignalCode.Other);
        var codes = WireName.All<RiskSignalCode>()
            .Where(code => code != other)
            .OrderByDescending(code => code.Length)
            .Select(Regex.Escape);
        var words = Vocabulary.Select(Regex.Escape);

        var pattern =
            $@"(?-i:\b(?:{string.Join('|', codes)})\b)" +
            @"|(?-i:\b(?:EV|POL|GLB)-\d+\b)" +
            $@"|\b(?:{string.Join('|', words)})s?\b";

        return new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            MatchTimeout);
    }
}
