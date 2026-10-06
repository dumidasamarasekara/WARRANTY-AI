using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Application.Claims;

/// <summary>
/// A supplement to a <c>PendingInformation</c> claim (contracts/rest-api.openapi.yaml, <c>SupplementForm</c>).
/// <paramref name="ClaimId"/> is the claim the caller may act on: the claimant token's claim on the
/// claimant channel, the path's claim ID for a claims agent. <paramref name="Reference"/> is the path
/// reference of the claimant route and must name the same claim; it is null for staff. The tenant comes
/// from <see cref="ITenantContext"/> only.
/// </summary>
public sealed record SupplementClaimCommand(
    ClaimChannel Channel,
    Guid ClaimId,
    string? Reference,
    string? Note,
    IReadOnlyList<EvidenceUpload> Invoices,
    IReadOnlyList<EvidenceUpload> Photos);

/// <summary>Outcome of <see cref="SupplementClaim.ExecuteAsync"/>.</summary>
public abstract record SupplementClaimResult
{
    private SupplementClaimResult()
    {
    }

    /// <summary>202: the supplement was stored and the new round's job queued; the claim is <c>UnderEvaluation</c>.</summary>
    public sealed record Accepted(Guid ClaimId, string Reference, ClaimStatus Status, int Round) : SupplementClaimResult;

    /// <summary>404: no such claim in the current tenant, or the reference names another claim than the claimant token.</summary>
    public sealed record NotFound : SupplementClaimResult;

    /// <summary>409: the claim is not <c>PendingInformation</c> (e.g. already decided), or another request changed it concurrently.</summary>
    public sealed record Conflict(string Detail) : SupplementClaimResult;

    /// <summary>400 ValidationProblem: field (<c>invoice</c>, <c>photos</c>, <c>note</c>, <c>supplement</c>) → messages. Nothing was stored.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SupplementClaimResult;

    /// <summary>415: a file type the PoC refuses outright (HEIC). Nothing was stored.</summary>
    public sealed record UnsupportedMediaType(string Detail) : SupplementClaimResult;
}

/// <summary>
/// Supplements a claim that is waiting for information (FR-010, FR-037a, research R24). In order: the
/// claim must be visible in the tenant (and, on the claimant route, be the claim of the reference); it
/// must be <c>PendingInformation</c> (409 otherwise — terminal claims are never reopened); the files are
/// validated as in <see cref="SubmitClaim"/> (content judged by magic bytes, HEIC → 415) and at least
/// one file or a note is required. The files are then stored for the next round, and in one transaction
/// <see cref="Claim.AddSupplement"/> starts that round (<c>UnderEvaluation</c>), the evidence and the
/// round's adjudication job are saved and a <c>SupplementReceived</c> trail entry records the round, the
/// stored evidence and the note. Earlier rounds, their runs and their evidence stay untouched; the new
/// round is evaluated in full by the worker. A concurrent change of the claim is a 409.
/// </summary>
public sealed class SupplementClaim(
    ITenantContext tenant,
    IClaimRepository claims,
    IDocumentStore documents,
    IJobQueue jobs,
    IDecisionTrailWriter trail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public const int MaxNoteLength = 2000;

    public const string NotPendingDetail = "This claim is not waiting for information; it can't be supplemented.";

    public const string ConcurrentChangeDetail = "This claim was changed by another request; reload it and try again.";

    public const string NoteKey = "note";

    public const string SupplementKey = "supplement";

    public async Task<SupplementClaimResult> ExecuteAsync(SupplementClaimCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved || (command.Channel == ClaimChannel.AgentPortal && tenant.IsSystem))
        {
            throw new InvalidOperationException("A claim can only be supplemented by the claimant or a staff principal of a resolved tenant.");
        }

        var claim = await claims.GetAsync(command.ClaimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId || !MatchesReference(claim, command.Reference))
        {
            return new SupplementClaimResult.NotFound();
        }

        if (claim.Status != ClaimStatus.PendingInformation)
        {
            return new SupplementClaimResult.Conflict(NotPendingDetail);
        }

        var files = await EvidenceUploads.InspectAsync(command.Invoices, command.Photos, ct);
        if (EvidenceUploads.HasHeic(files))
        {
            return new SupplementClaimResult.UnsupportedMediaType(SubmitClaim.HeicMessage);
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        var errors = Validate(command, note, files);
        if (errors.Count > 0)
        {
            return new SupplementClaimResult.Invalid(errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        }

        var now = time.GetUtcNow();
        var requestedItems = claim.RequestedItems.Select(i => i.Item).ToArray();
        var round = claim.CurrentRound + 1;

        // Stored before the transaction (the path is known in advance), as in SubmitClaim: a failed commit
        // can leave unreferenced blobs but never a round without its files.
        var evidence = new List<ClaimEvidence>(files.Count);
        foreach (var file in files)
        {
            evidence.Add(await EvidenceUploads.StoreAsync(documents, tenant.TenantId, claim.Id, round, file, now, ct));
        }

        // Applied once, outside the (retriable) transaction body; the status was checked above.
        claim.AddSupplement(now);
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    foreach (var item in evidence)
                    {
                        claims.AddEvidence(item);
                    }

                    await jobs.EnqueueAsync(
                        ClaimJob.Enqueue(Guid.CreateVersion7(), tenant.TenantId, claim.Id, claim.CurrentRound, tenant.CorrelationId, now), token);
                    await unitOfWork.SaveChangesAsync(token);
                    await WriteTrailAsync(claim, command.Channel, requestedItems, evidence, note, token);
                },
                ct);
        }
        catch (ConcurrencyConflictException)
        {
            return new SupplementClaimResult.Conflict(ConcurrentChangeDetail);
        }

        return new SupplementClaimResult.Accepted(claim.Id, claim.Reference, claim.Status, claim.CurrentRound);
    }

    private static bool MatchesReference(Claim claim, string? reference)
        => reference is null || string.Equals(ClaimReference.Normalize(reference), claim.Reference, StringComparison.Ordinal);

    private static Dictionary<string, List<string>> Validate(SupplementClaimCommand command, string? note, IReadOnlyList<InspectedEvidence> files)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (command.Invoices.Count > 1)
        {
            EvidenceUploads.AddError(errors, EvidenceUploads.InvoiceKey, "Add one invoice file.");
        }

        if (command.Photos.Count > SubmitClaim.MaxPhotos)
        {
            EvidenceUploads.AddError(errors, EvidenceUploads.PhotosKey, $"Add at most {SubmitClaim.MaxPhotos} photos.");
        }

        if (note is { Length: > MaxNoteLength })
        {
            EvidenceUploads.AddError(errors, NoteKey, $"Use at most {MaxNoteLength} characters.");
        }

        if (files.Count == 0 && note is null)
        {
            EvidenceUploads.AddError(errors, SupplementKey, "Add the requested files or a note.");
        }

        EvidenceUploads.AddFileErrors(errors, files);
        return errors;
    }

    private async Task WriteTrailAsync(
        Claim claim, ClaimChannel channel, string[] requestedItems, IReadOnlyList<ClaimEvidence> evidence, string? note, CancellationToken ct)
    {
        var claimant = channel == ClaimChannel.ClaimantPortal;
        var invoices = evidence.Count(e => e.Kind == EvidenceKind.Invoice);
        var photos = evidence.Count(e => e.Kind == EvidenceKind.Photo);
        var parts = new List<string>(3);
        if (invoices > 0)
        {
            parts.Add($"{invoices} invoice{(invoices == 1 ? string.Empty : "s")}");
        }

        if (photos > 0)
        {
            parts.Add($"{photos} photo{(photos == 1 ? string.Empty : "s")}");
        }

        if (note is not null)
        {
            parts.Add("a note");
        }

        await trail.AppendAsync(
            claim.Id,
            TrailStep.SupplementReceived,
            claimant ? Claim.ClaimantSubmitter : tenant.PrincipalId,
            claimant
                ? $"Supplement received through the claimant channel ({string.Join(", ", parts)}); round {claim.CurrentRound} queued for evaluation."
                : $"Supplement added by claims agent {tenant.PrincipalName} ({string.Join(", ", parts)}); round {claim.CurrentRound} queued for evaluation.",
            new
            {
                round = claim.CurrentRound,
                channel = channel.ToString(),
                requestedItems,
                autoInfoRequestCount = claim.AutoInfoRequestCount,
                reviewerInfoRequested = claim.ReviewerInfoRequested,
                note,
                evidence = evidence.Select(EvidenceUploads.TrailPayload).ToList(),
            },
            ct);
    }
}
