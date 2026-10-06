using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;

namespace Warranty.Api.Endpoints.Claims;

/// <summary><c>SubmissionAccepted</c>; <see cref="ClaimId"/> is returned to staff only.</summary>
public sealed record SubmissionAccepted(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ClaimId,
    string Reference,
    ClaimStatus Status,
    int Round);

/// <summary>
/// Reads the multipart <c>ClaimSubmissionForm</c> shared by <c>POST /api/public/claims</c> and
/// <c>POST /api/claims</c> (part <c>claim</c> as JSON — a form field or a file part — one <c>invoice</c>
/// part and one <c>photos</c> part per photo) and maps the use case's result to HTTP: 202
/// <c>SubmissionAccepted</c>, 400 ValidationProblem, 413 or 415 ProblemDetails.
/// </summary>
internal static class ClaimSubmissionRequest
{
    /// <summary>One invoice and eight photos of 15 MB each, plus the claim JSON and multipart framing.</summary>
    public const long MaxRequestBytes = 140L * 1024 * 1024;

    public const string ClaimPart = "claim";

    public const string InvoicePart = "invoice";

    public const string PhotosPart = "photos";

    public const string NotePart = "note";

    private const int MaxClaimJsonBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Raises the request and multipart limits to <see cref="MaxRequestBytes"/> for a submission route.</summary>
    public static RouteHandlerBuilder WithSubmissionLimits(this RouteHandlerBuilder builder)
        => builder
            .WithMetadata(new RequestSizeLimit(MaxRequestBytes))
            .WithFormOptions(multipartBodyLengthLimit: MaxRequestBytes)
            .Accepts<IFormCollection>("multipart/form-data")
            .Produces<SubmissionAccepted>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

    /// <summary>Submits the request's form through <see cref="SubmitClaim"/> for <paramref name="channel"/>.</summary>
    public static async Task<IResult> SubmitAsync(
        HttpRequest request, SubmitClaim submitClaim, ClaimChannel channel, Func<SubmitClaimResult.Accepted, IResult> accepted, CancellationToken ct)
    {
        var (form, failure) = await ReadFormAsync(request, "Send the claim as multipart/form-data.", ct);
        if (form is null)
        {
            return failure!;
        }

        var command = new SubmitClaimCommand(
            channel,
            await ReadClaimAsync(form, ct),
            form.Files.GetFiles(InvoicePart).Select(ToUpload).ToList(),
            form.Files.GetFiles(PhotosPart).Select(ToUpload).ToList());

        return await submitClaim.ExecuteAsync(command, ct) switch
        {
            SubmitClaimResult.Accepted ok => accepted(ok),
            SubmitClaimResult.Invalid invalid => Results.ValidationProblem(
                invalid.Errors, detail: "The claim has missing or invalid information; no claim was created."),
            SubmitClaimResult.UnsupportedMediaType unsupported => Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported file type", detail: unsupported.Detail),
            var other => throw new InvalidOperationException($"Unexpected submission result {other.GetType().Name}."),
        };
    }

    /// <summary>
    /// Supplements a claim through <see cref="SupplementClaim"/> from the multipart <c>SupplementForm</c>
    /// (<c>note</c>, one <c>invoice</c> part, one <c>photos</c> part per photo) and maps the result to
    /// HTTP: 202 <c>SubmissionAccepted</c>, 404, 409, 400 ValidationProblem, 413 or 415 ProblemDetails.
    /// </summary>
    public static async Task<IResult> SupplementAsync(
        HttpRequest request,
        SupplementClaim supplementClaim,
        ClaimChannel channel,
        Guid claimId,
        string? reference,
        Func<SupplementClaimResult.Accepted, IResult> accepted,
        CancellationToken ct)
    {
        var (form, failure) = await ReadFormAsync(request, "Send the supplement as multipart/form-data.", ct);
        if (form is null)
        {
            return failure!;
        }

        var command = new SupplementClaimCommand(
            channel,
            claimId,
            reference,
            form[NotePart].ToString(),
            form.Files.GetFiles(InvoicePart).Select(ToUpload).ToList(),
            form.Files.GetFiles(PhotosPart).Select(ToUpload).ToList());

        return await supplementClaim.ExecuteAsync(command, ct) switch
        {
            SupplementClaimResult.Accepted ok => accepted(ok),
            SupplementClaimResult.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound),
            SupplementClaimResult.Conflict conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: conflict.Detail),
            SupplementClaimResult.Invalid invalid => Results.ValidationProblem(
                invalid.Errors, detail: "The supplement has missing or invalid information; nothing was added to the claim."),
            SupplementClaimResult.UnsupportedMediaType unsupported => Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported file type", detail: unsupported.Detail),
            var other => throw new InvalidOperationException($"Unexpected supplement result {other.GetType().Name}."),
        };
    }

    /// <summary>The request's multipart form, or the 415 (not multipart) or 413 (over the limits) to return instead.</summary>
    private static async Task<(IFormCollection? Form, IResult? Failure)> ReadFormAsync(HttpRequest request, string notMultipartDetail, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, detail: notMultipartDetail));
        }

        try
        {
            return (await request.ReadFormAsync(ct), null);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge());
        }
        catch (InvalidDataException)
        {
            // The multipart reader's own limits (body length, part headers).
            return (null, TooLarge());
        }
    }

    /// <summary>The <c>claim</c> part, or null when it is missing or not valid JSON (reported as a <c>claim</c> field error).</summary>
    private static async Task<ClaimSubmissionData?> ReadClaimAsync(IFormCollection form, CancellationToken ct)
    {
        try
        {
            if (form.Files.GetFile(ClaimPart) is { } file)
            {
                if (file.Length is 0 or > MaxClaimJsonBytes)
                {
                    return null;
                }

                await using var stream = file.OpenReadStream();
                return await JsonSerializer.DeserializeAsync<ClaimSubmissionData>(stream, JsonOptions, ct);
            }

            var text = form[ClaimPart].ToString();
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<ClaimSubmissionData>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EvidenceUpload ToUpload(IFormFile file) => new(file.FileName, file.Length, file.OpenReadStream);

    private static IResult TooLarge()
        => Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            detail: $"The submission is too large: send one invoice and at most {SubmitClaim.MaxPhotos} photos of up to 15 MB each.");

    private sealed class RequestSizeLimit(long maxRequestBodySize) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = maxRequestBodySize;
    }
}
