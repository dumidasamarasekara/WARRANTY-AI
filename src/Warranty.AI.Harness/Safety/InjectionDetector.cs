using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Safety;

/// <summary>One injection phrase found in one piece of claimant-supplied text.</summary>
/// <param name="Source">The <see cref="UntrustedTextPart.Label"/> of the text it was found in (e.g. <c>invoice_text EV-2</c>).</param>
/// <param name="Phrase">The matched phrase as listed in the global injection phrase list.</param>
public sealed record InjectionMatch(string Source, string Phrase);

/// <summary>
/// Deterministic prompt-injection detector (FR-019, research R14): normalized phrase matching from the
/// global injection phrase list over the claimant's description, extracted invoice text and text read
/// from photos. Stub for the T091 tests; implemented by T094.
/// </summary>
public sealed class InjectionDetector
{
    /// <summary>Creates a detector for the phrases of <c>seed/global/injection-phrases.md</c>.</summary>
    public InjectionDetector(IEnumerable<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        throw new NotImplementedException("Pending T094.");
    }

    /// <summary>Every listed phrase found in <paramref name="parts"/>, with the label of the part it was found in.</summary>
    public IReadOnlyList<InjectionMatch> Detect(IEnumerable<UntrustedTextPart> parts)
        => throw new NotImplementedException("Pending T094.");
}
