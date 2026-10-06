using Warranty.Api.Auth;
using Warranty.Api.Tenancy;
using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;

namespace Warranty.Api.Endpoints.Claims;

/// <summary>
/// Evidence files of a claim of the staff user's tenant. Files are only ever served through this
/// route, after authorization and the tenant check, never by direct storage links (research R11).
/// </summary>
public static class EvidenceEndpoints
{
    /// <summary><c>GET /api/claims/{claimId}/evidence/{evidenceId}/content</c>: every staff role.</summary>
    public static RouteGroupBuilder MapEvidenceRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/{claimId:guid}/evidence/{evidenceId:guid}/content", GetContentAsync)
            .WithName("GetEvidenceContent")
            .WithSummary("Stream an evidence file after authorization and tenant check")
            .RequireAuthorization(AuthPolicies.AnyStaff)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return group;
    }

    /// <summary>
    /// Streams the file from the tenant's container (<see cref="IDocumentStore"/> refuses any other);
    /// 404 when the claim or the file is not visible here.
    /// </summary>
    private static async Task<IResult> GetContentAsync(
        Guid claimId, Guid evidenceId, ClaimQueries query, IDocumentStore documents, HttpContext http, CancellationToken ct)
    {
        if (await query.GetEvidenceContentAsync(claimId, evidenceId, ct) is not { } content)
        {
            return CrossTenantGuard.NotFound();
        }

        var stream = await documents.OpenEvidenceAsync(content.BlobPath, ct);
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Stream(stream, content.ContentType);
    }
}
