using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Claims;

namespace Warranty.Infrastructure.Storage;

/// <summary>
/// Evidence in one private container per tenant (<c>tenant-{slug}</c>, taken from
/// <see cref="ITenantContext"/> only) and platform knowledge sources in <c>knowledge-sources</c>
/// (research R11). Evidence paths are relative to the tenant's container and must have the form
/// <c>claims/…</c>; any other path, including dot segments that a URI could resolve into another
/// container, is refused.
/// </summary>
internal sealed class BlobDocumentStore(BlobServiceClient blobs, ITenantContext tenantContext) : IDocumentStore
{
    public const string KnowledgeSourcesContainer = "knowledge-sources";

    private const string EvidencePrefix = "claims/";

    public async Task<StoredDocument> UploadEvidenceAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        ValidatePath(blobPath, EvidencePrefix);
        var container = await GetTenantContainerAsync(ct);

        await using var hashing = new HashingReadStream(content, ClaimEvidence.MaxSizeBytes);
        await container.GetBlobClient(blobPath).UploadAsync(
            hashing,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, // evidence is never replaced
            },
            ct);

        return new StoredDocument(blobPath, hashing.GetSha256(), hashing.BytesRead);
    }

    public async Task<Stream> OpenEvidenceAsync(string blobPath, CancellationToken ct)
    {
        ValidatePath(blobPath, EvidencePrefix);
        var blob = blobs.GetBlobContainerClient(TenantContainerName()).GetBlobClient(blobPath);
        try
        {
            return await blob.OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            throw new FileNotFoundException("Evidence file not found.", blobPath, e);
        }
    }

    public async Task UploadKnowledgeSourceAsync(string blobPath, string content, CancellationToken ct)
    {
        ValidatePath(blobPath, requiredPrefix: null);
        if (!blobPath.StartsWith("global/", StringComparison.Ordinal) && !blobPath.StartsWith("tenant-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Knowledge sources live under 'global/' or 'tenant-{slug}/'.", nameof(blobPath));
        }

        var container = blobs.GetBlobContainerClient(KnowledgeSourcesContainer);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        await container.GetBlobClient(blobPath).UploadAsync(
            BinaryData.FromString(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "text/markdown; charset=utf-8" } },
            ct);
    }

    /// <summary>Rejects absolute, backslash, empty, <c>.</c> and <c>..</c> segments, and paths outside <paramref name="requiredPrefix"/>.</summary>
    internal static void ValidatePath(string blobPath, string? requiredPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);
        var valid = !blobPath.Contains('\\', StringComparison.Ordinal)
            && !blobPath.Contains('%', StringComparison.Ordinal)
            && !blobPath.Contains("://", StringComparison.Ordinal)
            && (requiredPrefix is null || blobPath.StartsWith(requiredPrefix, StringComparison.Ordinal))
            && blobPath.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != ".." && segment.All(IsSafe));
        if (!valid)
        {
            throw new UnauthorizedAccessException($"Blob path '{blobPath}' is not allowed.");
        }

        static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
    }

    private string TenantContainerName()
        => tenantContext.IsResolved
            ? $"tenant-{tenantContext.TenantSlug}"
            : throw new InvalidOperationException("Evidence storage needs a tenant context.");

    private async Task<BlobContainerClient> GetTenantContainerAsync(CancellationToken ct)
    {
        var container = blobs.GetBlobContainerClient(TenantContainerName());
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        return container;
    }
}
