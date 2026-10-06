using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Claims;

namespace Warranty.Application.Claims;

/// <summary>An uploaded file with its type as judged by its content; <see cref="ContentType"/> is null for an unsupported type.</summary>
internal sealed record InspectedEvidence(EvidenceUpload Upload, EvidenceKind Kind, EvidenceFileType Type, string? ContentType);

/// <summary>
/// File handling shared by <see cref="SubmitClaim"/> and <see cref="SupplementClaim"/> (FR-009, research R11):
/// the type of every file is judged by its magic bytes, never by its name or declared type; per-file
/// errors are keyed <c>invoice</c> or <c>photos</c>; files are stored under the claim's round. The
/// upload sanitizer (T110) belongs here, between inspection and <see cref="IDocumentStore"/>, so that
/// both flows get it.
/// </summary>
internal static class EvidenceUploads
{
    public const string InvoiceKey = "invoice";

    public const string PhotosKey = "photos";

    public static async Task<List<InspectedEvidence>> InspectAsync(
        IReadOnlyList<EvidenceUpload> invoices, IReadOnlyList<EvidenceUpload> photos, CancellationToken ct)
    {
        var files = new List<InspectedEvidence>(invoices.Count + photos.Count);
        foreach (var (upload, kind) in invoices.Select(u => (u, EvidenceKind.Invoice))
                     .Concat(photos.Select(u => (u, EvidenceKind.Photo))))
        {
            var type = EvidenceFileType.Unknown;
            if (upload.Length > 0)
            {
                await using var stream = upload.OpenReadStream();
                type = await EvidenceFileSignature.DetectAsync(stream, ct);
            }

            files.Add(new InspectedEvidence(upload, kind, type, EvidenceFileSignature.ContentTypeOf(type)));
        }

        return files;
    }

    /// <summary>Whether any file is a HEIC/HEIF photo, which is refused outright (415).</summary>
    public static bool HasHeic(IEnumerable<InspectedEvidence> files) => files.Any(f => f.Type == EvidenceFileType.Heic);

    /// <summary>Adds the per-file errors: empty, larger than 15 MB, unsupported type, or a PDF sent as a photo.</summary>
    public static void AddFileErrors(Dictionary<string, List<string>> errors, IEnumerable<InspectedEvidence> files)
    {
        foreach (var file in files)
        {
            var key = file.Kind == EvidenceKind.Invoice ? InvoiceKey : PhotosKey;
            var name = ClaimEvidence.SanitizeFileName(file.Upload.FileName);
            if (file.Upload.Length <= 0)
            {
                AddError(errors, key, $"{name} is empty.");
            }
            else if (file.Upload.Length > ClaimEvidence.MaxSizeBytes)
            {
                AddError(errors, key, $"{name} is larger than 15 MB.");
            }
            else if (file.ContentType is null)
            {
                AddError(errors, key, $"{name} can't be used: send a PDF, JPG, PNG or WebP file.");
            }
            else if (file.Kind == EvidenceKind.Photo && file.Type == EvidenceFileType.Pdf)
            {
                AddError(errors, key, $"{name} can't be used as a photo: send a JPG, PNG or WebP image.");
            }
        }
    }

    /// <summary>Uploads a validated file to <c>claims/{claimId}/{round}/…</c> and returns its evidence record.</summary>
    public static async Task<ClaimEvidence> StoreAsync(
        IDocumentStore documents, Guid tenantId, Guid claimId, int round, InspectedEvidence file, DateTimeOffset now, CancellationToken ct)
    {
        var evidenceId = Guid.CreateVersion7();
        var contentType = file.ContentType!;
        var path = ClaimEvidence.BlobPathFor(claimId, round, evidenceId, contentType);
        await using var content = file.Upload.OpenReadStream();
        var stored = await documents.UploadEvidenceAsync(path, content, contentType, ct);
        return ClaimEvidence.Create(
            evidenceId, tenantId, claimId, round, file.Kind, file.Upload.FileName ?? string.Empty, contentType, stored.SizeBytes,
            stored.Sha256, now);
    }

    /// <summary>Trail payload of stored evidence (no file names: they are claimant-supplied).</summary>
    public static object TrailPayload(ClaimEvidence evidence) => new
    {
        evidenceId = evidence.Id,
        kind = evidence.Kind.ToString(),
        contentType = evidence.ContentType,
        sizeBytes = evidence.SizeBytes,
        sha256 = evidence.Sha256,
        blobPath = evidence.BlobPath,
    };

    public static void AddError(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var list))
        {
            errors[key] = list = [];
        }

        list.Add(message);
    }
}
