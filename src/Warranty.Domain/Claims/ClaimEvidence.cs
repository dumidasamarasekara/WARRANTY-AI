using System.Collections.Frozen;
using System.Text;

namespace Warranty.Domain.Claims;

/// <summary>An evidence file (invoice or photo) attached to a claim in a given submission round.</summary>
public sealed class ClaimEvidence
{
    /// <summary>Maximum accepted file size (15 MB).</summary>
    public const long MaxSizeBytes = 15L * 1024 * 1024;

    /// <summary>Accepted content types; HEIC is rejected in the PoC (research R11).</summary>
    public static readonly FrozenSet<string> AllowedContentTypes =
        new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" }.ToFrozenSet(StringComparer.Ordinal);

    private ClaimEvidence()
    {
        FileName = ContentType = Sha256 = BlobPath = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    /// <summary>Submission round that added the file (1 = original submission).</summary>
    public int Round { get; private set; }

    public EvidenceKind Kind { get; private set; }

    public string FileName { get; private set; }

    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the content; used for reused-evidence detection within the tenant.</summary>
    public string Sha256 { get; private set; }

    /// <summary>Path inside the tenant's container: <c>claims/{claimId}/{round}/{evidenceId}{ext}</c>.</summary>
    public string BlobPath { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    public static ClaimEvidence Create(
        Guid id,
        Guid tenantId,
        Guid claimId,
        int round,
        EvidenceKind kind,
        string fileName,
        string contentType,
        long sizeBytes,
        string sha256,
        DateTimeOffset uploadedAt)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("Evidence, tenant and claim IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(round);
        if (contentType is null || !AllowedContentTypes.Contains(contentType))
        {
            throw new ArgumentException($"Content type '{contentType}' is not supported.", nameof(contentType));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeBytes, MaxSizeBytes);
        if (sha256 is null || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("SHA-256 must be 64 lower-case hex characters.", nameof(sha256));
        }

        return new ClaimEvidence
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            Round = round,
            Kind = kind,
            FileName = SanitizeFileName(fileName),
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            BlobPath = $"claims/{claimId}/{round}/{id}{ExtensionFor(contentType)}",
            UploadedAt = uploadedAt,
        };
    }

    /// <summary>Keeps only the final path segment and safe characters; never trusted for storage paths.</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/')[^1]);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        var sanitized = builder.ToString().Trim('.', '_');
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[^100..];
        }

        return sanitized.Length == 0 ? "file" : sanitized;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        _ => throw new ArgumentException($"Content type '{contentType}' is not supported.", nameof(contentType)),
    };
}
