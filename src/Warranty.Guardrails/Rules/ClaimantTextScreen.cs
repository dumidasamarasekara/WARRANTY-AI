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
    /// <summary>
    /// Screens <paramref name="text"/> and returns <see cref="ClaimantTextScreenResult.Safe"/> or the
    /// first (left-most) offending term, exactly as it appears in the text.
    /// </summary>
    public static ClaimantTextScreenResult Screen(string text) =>
        throw new NotImplementedException("Pending T120");
}
