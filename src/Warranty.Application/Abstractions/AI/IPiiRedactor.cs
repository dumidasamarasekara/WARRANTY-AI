namespace Warranty.Application.Abstractions.AI;

/// <summary>The redacted text and how many values were replaced.</summary>
public sealed record RedactionResult(string Text, int Replacements);

/// <summary>
/// Scrubs personal data from free text before it leaves the system and before it is logged
/// (FR-006a, research R15/R28): email addresses become <c>[EMAIL]</c>, phone numbers <c>[PHONE]</c>.
/// </summary>
public interface IPiiRedactor
{
    RedactionResult Redact(string text);
}
