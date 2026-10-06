using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Claims;

namespace Warranty.Application.Claims;

/// <summary>An uploaded file with its type as judged by its content; <see cref="ContentType"/> is null for an unsupported type.</summary>
internal sealed record InspectedEvidence(EvidenceUpload Upload, EvidenceKind Kind, EvidenceFileType Type, string? ContentType);

/// <summary>A file that passed <see cref="IUploadSanitizer"/>: only <see cref="Clean"/>'s bytes are stored, hashed and sent to a model.</summary>
internal sealed record SanitizedEvidence(InspectedEvidence File, UploadSanitizerResult.Sanitized Clean);

/// <summary>
/// File handling shared by <see cref="SubmitClaim"/> and <see cref="SupplementClaim"/> (FR-009, research R11):
/// the type of every file is judged by its magic bytes, never by its name or declared type; per-file
/// errors are keyed <c>invoice</c> or <c>photos</c>; every file is cleaned by <see cref="IUploadSanitizer"/>
/// (T110: metadata stripped, dangerous PDFs refused) between inspection and <see cref="IDocumentStore"/>,
/// and only the sanitized bytes are stored under the claim's round.
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
            if (FileError(file) is { } message)
            {
                AddError(errors, KeyOf(file), message);
            }
        }
    }

    /// <summary>
    /// Runs every file without a size or type error through <paramref name="sanitizer"/>; a rejected file
    /// (e.g. an encrypted PDF or an unreadable photo) becomes a per-file error, so nothing is stored.
    /// </summary>
    public static async Task<List<SanitizedEvidence>> SanitizeAsync(
        IUploadSanitizer sanitizer, Dictionary<string, List<string>> errors, IEnumerable<InspectedEvidence> files, CancellationToken ct)
    {
        var sanitized = new List<SanitizedEvidence>();
        foreach (var file in files.Where(f => FileError(f) is null))
        {
            await using var stream = file.Upload.OpenReadStream();
            switch (await sanitizer.SanitizeAsync(stream, ct))
            {
                case UploadSanitizerResult.Sanitized clean:
                    sanitized.Add(new SanitizedEvidence(file, clean));
                    break;
                case UploadSanitizerResult.Rejected rejected:
                    AddError(errors, KeyOf(file), $"{ClaimEvidence.SanitizeFileName(file.Upload.FileName)} can't be used: {rejected.Reason}");
                    break;
            }
        }

        return sanitized;
    }

    /// <summary>Uploads the sanitized bytes of a file to <c>claims/{claimId}/{round}/…</c> and returns its evidence record.</summary>
    public static async Task<ClaimEvidence> StoreAsync(
        IDocumentStore documents, Guid tenantId, Guid claimId, int round, SanitizedEvidence evidence, DateTimeOffset now, CancellationToken ct)
    {
        var (file, clean) = evidence;
        var evidenceId = Guid.CreateVersion7();
        var contentType = clean.ContentType;
        var path = ClaimEvidence.BlobPathFor(claimId, round, evidenceId, contentType);
        await using var content = new MemoryStream(clean.Content.ToArray(), writable: false);
        var stored = await documents.UploadEvidenceAsync(path, content, contentType, ct);
        return ClaimEvidence.Create(
            evidenceId, tenantId, claimId, round, file.Kind, file.Upload.FileName ?? string.Empty, contentType, stored.SizeBytes,
            stored.Sha256, now);
    }

    /// <summary>The error of a file judged by its size and detected type alone; null when it can go to the sanitizer.</summary>
    private static string? FileError(InspectedEvidence file)
    {
        var name = ClaimEvidence.SanitizeFileName(file.Upload.FileName);
        return file.Upload.Length <= 0 ? $"{name} is empty."
            : file.Upload.Length > ClaimEvidence.MaxSizeBytes ? $"{name} is larger than 15 MB."
            : file.ContentType is null ? $"{name} can't be used: send a PDF, JPG, PNG or WebP file."
            : file.Kind == EvidenceKind.Photo && file.Type == EvidenceFileType.Pdf ? $"{name} can't be used as a photo: send a JPG, PNG or WebP image."
            : null;
    }

    private static string KeyOf(InspectedEvidence file) => file.Kind == EvidenceKind.Invoice ? InvoiceKey : PhotosKey;

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
