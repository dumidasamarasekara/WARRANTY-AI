namespace Warranty.Evaluation.Golden;

/// <summary>
/// The stand-in file for an evidence part a golden case deliberately lacks (the intake short-circuit cases:
/// no invoice, no photo). A submission must carry one invoice and at least one photo (FR-007, contract
/// <c>ClaimSubmissionForm</c>; 400 otherwise), while the harness's intake checks <c>INVOICE_PRESENT</c> and
/// <c>PHOTO_PRESENT</c> exist for claims whose round lacks them. The runner therefore submits such a case
/// through the production use case with this placeholder in the missing part and deletes the placeholder's
/// evidence row before the harness runs, so the run sees exactly the case's evidence and production
/// validation stays unchanged. The placeholder is a valid image, so it passes upload sanitizing both as an
/// invoice and as a photo.
/// </summary>
public static class PlaceholderEvidence
{
    public const string FileName = "evaluation-placeholder.png";

    /// <summary>A 1×1 PNG.</summary>
    public static ReadOnlyMemory<byte> Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
