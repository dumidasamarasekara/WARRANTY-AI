namespace Warranty.Application.Abstractions.Storage;

/// <summary>
/// Evidence and knowledge-source file storage. Evidence always lives in the current tenant's
/// container (<c>tenant-{slug}</c>, from <see cref="ITenantContext"/>); reads outside it are refused
/// (research R11). Files are served only through the API, never by direct links.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Stores evidence at a path relative to the tenant container and returns its SHA-256 and size.</summary>
    Task<StoredDocument> UploadEvidenceAsync(string blobPath, Stream content, string contentType, CancellationToken ct);

    /// <summary>Opens evidence of the current tenant; throws when the path is outside the tenant's container.</summary>
    Task<Stream> OpenEvidenceAsync(string blobPath, CancellationToken ct);

    /// <summary>Stores a platform-owned knowledge source document (migration service only).</summary>
    Task UploadKnowledgeSourceAsync(string blobPath, string content, CancellationToken ct);
}

/// <summary>Result of storing a file: lower-case hex SHA-256 computed while uploading, and the size.</summary>
public sealed record StoredDocument(string BlobPath, string Sha256, long SizeBytes);
