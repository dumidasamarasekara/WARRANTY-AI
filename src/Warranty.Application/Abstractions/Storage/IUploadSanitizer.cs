using Warranty.Application.Claims;

namespace Warranty.Application.Abstractions.Storage;

/// <summary>
/// Cleans an uploaded evidence file before it reaches <see cref="IDocumentStore"/> (FR-006a, FR-009,
/// research R11). The type is judged by the file's magic bytes only — the port takes neither a file
/// name nor a declared content type. Photos are re-encoded upright without any embedded metadata;
/// PDFs that are encrypted or carry JavaScript, launch actions or embedded files are rejected. Only
/// <see cref="UploadSanitizerResult.Sanitized.Content"/> may be stored, hashed or sent to a model.
/// Every flow that stores evidence (submission and supplements) must call it first.
/// </summary>
public interface IUploadSanitizer
{
    Task<UploadSanitizerResult> SanitizeAsync(Stream content, CancellationToken ct);
}

/// <summary>Outcome of <see cref="IUploadSanitizer.SanitizeAsync"/>.</summary>
public abstract record UploadSanitizerResult
{
    private UploadSanitizerResult()
    {
    }

    /// <summary>The type the content was identified as (<see cref="EvidenceFileType.Unknown"/> when unrecognised).</summary>
    public abstract EvidenceFileType Type { get; }

    /// <summary>The file can be stored: <paramref name="Content"/> are the sanitized bytes, typed <paramref name="ContentType"/>.</summary>
    public sealed record Sanitized(EvidenceFileType Type, string ContentType, ReadOnlyMemory<byte> Content) : UploadSanitizerResult
    {
        public override EvidenceFileType Type { get; } = Type;
    }

    /// <summary>The file can't be used; <paramref name="Reason"/> is a sentence for the claimant, e.g. "the PDF is encrypted".</summary>
    public sealed record Rejected(EvidenceFileType Type, string Reason) : UploadSanitizerResult
    {
        public override EvidenceFileType Type { get; } = Type;
    }
}
