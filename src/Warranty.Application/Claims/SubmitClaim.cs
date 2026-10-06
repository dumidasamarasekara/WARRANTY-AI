using System.Globalization;
using System.Text.RegularExpressions;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Application.Claims;

/// <summary>
/// The <c>claim</c> part of a submission (contracts/rest-api.openapi.yaml, <c>ClaimSubmissionData</c>).
/// Every member is nullable so that missing values become field errors instead of parse failures. It
/// has no tenant field: the tenant comes from <see cref="ITenantContext"/> only, and any tenant-like
/// property in the JSON is ignored.
/// </summary>
public sealed record ClaimSubmissionData(
    ClaimSubmissionCustomer? Customer,
    ClaimSubmissionProduct? Product,
    ClaimSubmissionPurchase? Purchase,
    string? ProblemDescription);

public sealed record ClaimSubmissionCustomer(
    string? FullName,
    string? Email,
    string? Phone,
    string? AddressLine,
    string? City,
    string? PostalCode,
    string? Country);

public sealed record ClaimSubmissionProduct(string? ModelCode, string? SerialNumber);

/// <summary>The purchase; <see cref="Date"/> is kept as text (<c>yyyy-MM-dd</c>) so an invalid date is reported as <c>purchase.date</c>.</summary>
public sealed record ClaimSubmissionPurchase(string? Date, string? Place, decimal? Price, string? Currency, string? Country);

/// <summary>An uploaded file. <paramref name="OpenReadStream"/> must return a new stream over the whole content on every call.</summary>
public sealed record EvidenceUpload(string? FileName, long Length, Func<Stream> OpenReadStream);

/// <summary>
/// A submission through the claimant channel or by a claims agent. <paramref name="Claim"/> is null
/// when the <c>claim</c> part was missing or not valid JSON.
/// </summary>
public sealed record SubmitClaimCommand(
    ClaimChannel Channel,
    ClaimSubmissionData? Claim,
    IReadOnlyList<EvidenceUpload> Invoices,
    IReadOnlyList<EvidenceUpload> Photos);

/// <summary>Outcome of <see cref="SubmitClaim.ExecuteAsync"/>.</summary>
public abstract record SubmitClaimResult
{
    private SubmitClaimResult()
    {
    }

    /// <summary>The claim and its round-1 job were stored; adjudication runs asynchronously.</summary>
    public sealed record Accepted(Guid ClaimId, string Reference, ClaimStatus Status, int Round) : SubmitClaimResult;

    /// <summary>400 ValidationProblem: field paths (<c>purchase.date</c>, <c>customer.email</c>, <c>invoice</c>, <c>photos</c>) → messages. No claim was created.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SubmitClaimResult;

    /// <summary>415: a file type the PoC refuses outright (HEIC). No claim was created.</summary>
    public sealed record UnsupportedMediaType(string Detail) : SubmitClaimResult;
}

/// <summary>
/// Submits a claim (FR-007, FR-009, research R10–R12): validates the claim data and the files
/// (content judged by magic bytes), finds or creates the customer through the CRM, resolves the
/// product from the tenant's catalog, derives the region, generates a random reference, stores the
/// evidence for round 1, and saves the claim, its evidence and its adjudication job in one
/// transaction together with the trail entries <c>ClaimSubmitted</c>, <c>TenantResolved</c> and
/// <c>EvidenceStored</c>. The tenant is the one of <see cref="ITenantContext"/> — never the request body.
/// Evidence is uploaded before the transaction (the blob path is known in advance), so a failed
/// commit can leave unreferenced blobs but never a claim without its files.
/// </summary>
public sealed partial class SubmitClaim(
    ITenantContext tenant,
    ICrmClient crm,
    ICatalogRepository catalog,
    IClaimRepository claims,
    IDocumentStore documents,
    IJobQueue jobs,
    IDecisionTrailWriter trail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public const int MaxPhotos = 8;

    public const string HeicMessage = "HEIC photos are not supported. Please send photos as JPEG or PNG.";

    private const int MaxReferenceAttempts = 5;

    private const decimal MaxPrice = 9_999_999_999.99m;

    public async Task<SubmitClaimResult> ExecuteAsync(SubmitClaimCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("A claim can only be submitted for a resolved tenant.");
        }

        var now = time.GetUtcNow();
        var claimDate = DateOnly.FromDateTime(now.UtcDateTime);

        var files = await EvidenceUploads.InspectAsync(command.Invoices, command.Photos, ct);
        if (EvidenceUploads.HasHeic(files))
        {
            return new SubmitClaimResult.UnsupportedMediaType(HeicMessage);
        }

        var errors = Validate(command.Claim, claimDate);
        AddFileErrors(errors, command, files);
        if (errors.Count > 0)
        {
            return new SubmitClaimResult.Invalid(errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        }

        var data = Normalize(command.Claim!);
        var customer = await crm.FindOrCreateCustomerAsync(
            new CustomerDetails(data.FullName, data.Email, data.CustomerCountry, data.Phone, data.AddressLine, data.City, data.PostalCode), ct);
        var product = await catalog.FindProductByModelAsync(data.ModelCode, ct);
        var serial = await catalog.FindSerialAsync(data.SerialNumber, ct);
        var serialRegistered = product is not null && serial is not null && serial.ProductId == product.Id;
        var region = DeriveRegion(data.PurchaseCountry, data.CustomerCountry);
        var reference = await NewReferenceAsync(ct);
        var submittedBy = command.Channel == ClaimChannel.ClaimantPortal ? Claim.ClaimantSubmitter : tenant.PrincipalId;

        var claimId = Guid.CreateVersion7();
        var claim = Claim.Submit(
            claimId, tenant.TenantId, reference, command.Channel, submittedBy, customer.Id, data.Email, data.Phone,
            data.ModelCode, product?.Id, data.SerialNumber, data.PurchaseDate, data.Place, data.Price, region, data.ProblemDescription, now);

        var evidence = new List<ClaimEvidence>(files.Count);
        foreach (var file in files)
        {
            evidence.Add(await EvidenceUploads.StoreAsync(documents, tenant.TenantId, claimId, claim.CurrentRound, file, now, ct));
        }

        var job = ClaimJob.Enqueue(Guid.CreateVersion7(), tenant.TenantId, claimId, claim.CurrentRound, tenant.CorrelationId, now);

        await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                claims.Add(claim);
                foreach (var item in evidence)
                {
                    claims.AddEvidence(item);
                }

                await jobs.EnqueueAsync(job, token);
                await unitOfWork.SaveChangesAsync(token);
                await WriteTrailAsync(claim, product is not null, serialRegistered, customer.Id, evidence, token);
            },
            ct);

        return new SubmitClaimResult.Accepted(claim.Id, claim.Reference, claim.Status, claim.CurrentRound);
    }

    /// <summary>
    /// Field errors of the claim data, keyed by dotted camelCase path as in <c>ClaimSubmissionData</c>
    /// (e.g. <c>purchase.date</c>). <paramref name="claimDate"/> is the submission date (UTC).
    /// </summary>
    public static Dictionary<string, List<string>> Validate(ClaimSubmissionData? claim, DateOnly claimDate)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        void Text(string key, string? value, int maxLength, string? requiredMessage)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                if (requiredMessage is not null)
                {
                    Add(key, requiredMessage);
                }
            }
            else if (trimmed.Length > maxLength)
            {
                Add(key, $"Use at most {maxLength} characters.");
            }
        }

        void Code(string key, string? value, Regex pattern, string requiredMessage, string formatMessage)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                Add(key, requiredMessage);
            }
            else if (!pattern.IsMatch(trimmed.ToUpperInvariant()))
            {
                Add(key, formatMessage);
            }
        }

        if (claim is null)
        {
            Add("claim", "The claim details are missing or not valid JSON.");
            return errors;
        }

        var customer = claim.Customer;
        Text("customer.fullName", customer?.FullName, 200, "Enter the full name.");
        var email = customer?.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            Add("customer.email", "Enter an email address.");
        }
        else if (email.Length > 254 || !EmailPattern().IsMatch(email))
        {
            Add("customer.email", "Enter an email address like name@example.com.");
        }

        var phone = customer?.Phone?.Trim();
        if (!string.IsNullOrEmpty(phone))
        {
            var digits = phone.Count(char.IsAsciiDigit);
            if (phone.Length > 30)
            {
                Add("customer.phone", "Use at most 30 characters.");
            }
            else if (digits is < 5 or > 15 || !PhonePattern().IsMatch(phone))
            {
                Add("customer.phone", "Enter a valid phone number.");
            }
        }

        Text("customer.addressLine", customer?.AddressLine, 200, null);
        Text("customer.city", customer?.City, 100, null);
        Text("customer.postalCode", customer?.PostalCode, 20, null);
        Code("customer.country", customer?.Country, CountryPattern(), "Enter the country.", "Use the two-letter country code, e.g. US.");

        Text("product.modelCode", claim.Product?.ModelCode, 50, "Enter the model code.");
        Text("product.serialNumber", claim.Product?.SerialNumber, 50, "Enter the serial number.");

        var purchase = claim.Purchase;
        var date = purchase?.Date?.Trim();
        if (string.IsNullOrEmpty(date))
        {
            Add("purchase.date", "Enter the purchase date.");
        }
        else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var purchaseDate))
        {
            Add("purchase.date", "Enter a valid date (yyyy-MM-dd).");
        }
        else if (purchaseDate > claimDate)
        {
            Add("purchase.date", "The purchase date can't be in the future or after the claim date.");
        }

        Text("purchase.place", purchase?.Place, 200, "Enter where the product was bought.");
        if (purchase?.Price is not { } price)
        {
            Add("purchase.price", "Enter the price paid.");
        }
        else if (price <= 0 || price > MaxPrice)
        {
            Add("purchase.price", "Enter the price as a number greater than 0.");
        }

        Code("purchase.currency", purchase?.Currency, CurrencyPattern(), "Enter the currency.", "Use the three-letter currency code, e.g. USD.");
        Code("purchase.country", purchase?.Country, CountryPattern(), "Enter the country of purchase.", "Use the two-letter country code, e.g. US.");

        var description = claim.ProblemDescription?.Trim() ?? string.Empty;
        if (description.Length == 0)
        {
            Add("problemDescription", "Describe what went wrong.");
        }
        else if (description.Length < Claim.MinDescriptionLength)
        {
            Add("problemDescription", $"Describe the problem in at least {Claim.MinDescriptionLength} characters.");
        }
        else if (description.Length > Claim.MaxDescriptionLength)
        {
            Add("problemDescription", $"Use at most {Claim.MaxDescriptionLength} characters.");
        }

        return errors;
    }

    /// <summary>Region of purchase, else the customer's; null when neither country is in NA or the EU.</summary>
    public static Region? DeriveRegion(string? purchaseCountry, string? customerCountry)
        => RegionResolver.TryFromCountry(purchaseCountry, out var region) ? region
            : RegionResolver.TryFromCountry(customerCountry, out region) ? region
            : null;

    private static void AddFileErrors(Dictionary<string, List<string>> errors, SubmitClaimCommand command, IReadOnlyList<InspectedEvidence> files)
    {
        if (command.Invoices.Count == 0)
        {
            EvidenceUploads.AddError(errors, EvidenceUploads.InvoiceKey, "Add the invoice.");
        }
        else if (command.Invoices.Count > 1)
        {
            EvidenceUploads.AddError(errors, EvidenceUploads.InvoiceKey, "Add one invoice file.");
        }

        if (command.Photos.Count == 0)
        {
            EvidenceUploads.AddError(errors, EvidenceUploads.PhotosKey, "Add at least one photo of the product.");
        }
        else if (command.Photos.Count > MaxPhotos)
        {
            EvidenceUploads.AddError(errors, EvidenceUploads.PhotosKey, $"Add at most {MaxPhotos} photos.");
        }

        EvidenceUploads.AddFileErrors(errors, files);
    }

    /// <summary>A random reference not yet used in the tenant (collisions are improbable at 50 bits, but checked).</summary>
    private async Task<string> NewReferenceAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxReferenceAttempts; attempt++)
        {
            var reference = ClaimReference.Generate();
            if (await claims.FindByReferenceAsync(reference, ct) is null)
            {
                return reference;
            }
        }

        throw new InvalidOperationException("Could not generate an unused claim reference.");
    }

    private async Task WriteTrailAsync(
        Claim claim, bool productInCatalog, bool serialRegistered, Guid customerId, IReadOnlyList<ClaimEvidence> evidence, CancellationToken ct)
    {
        var claimant = claim.Channel == ClaimChannel.ClaimantPortal;
        await trail.AppendAsync(
            claim.Id,
            TrailStep.ClaimSubmitted,
            claim.SubmittedBy,
            claimant
                ? $"Claim {claim.Reference} submitted through the claimant channel."
                : $"Claim {claim.Reference} submitted by claims agent {tenant.PrincipalName}.",
            new
            {
                reference = claim.Reference,
                channel = claim.Channel.ToString(),
                submittedBy = claim.SubmittedBy,
                round = claim.CurrentRound,
                customerId,
                productModelCode = claim.ProductModelCode,
                productId = claim.ProductId,
                productInCatalog,
                serialRegistered,
                serialNumber = claim.SerialNumber,
                purchaseDate = claim.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                claimDate = claim.ClaimDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                region = claim.Region?.ToString(),
            },
            ct);

        await trail.AppendAsync(
            claim.Id,
            TrailStep.TenantResolved,
            "system",
            claimant
                ? $"Tenant {tenant.TenantSlug} resolved from the claimant channel host."
                : $"Tenant {tenant.TenantSlug} resolved from the claims agent's access token.",
            new
            {
                tenantId = tenant.TenantId,
                tenantSlug = tenant.TenantSlug,
                source = claimant ? "ChannelHost" : "StaffToken",
            },
            ct);

        var invoices = evidence.Count(e => e.Kind == EvidenceKind.Invoice);
        var photos = evidence.Count(e => e.Kind == EvidenceKind.Photo);
        await trail.AppendAsync(
            claim.Id,
            TrailStep.EvidenceStored,
            "system",
            $"{invoices} invoice{(invoices == 1 ? string.Empty : "s")} and {photos} photo{(photos == 1 ? string.Empty : "s")} stored for round {claim.CurrentRound}.",
            new
            {
                round = claim.CurrentRound,
                evidence = evidence.Select(EvidenceUploads.TrailPayload).ToList(),
            },
            ct);
    }

    private static NormalizedSubmission Normalize(ClaimSubmissionData claim)
    {
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var customer = claim.Customer!;
        var purchase = claim.Purchase!;
        return new NormalizedSubmission(
            customer.FullName!.Trim(),
            customer.Email!.Trim(),
            Optional(customer.Phone),
            Optional(customer.AddressLine),
            Optional(customer.City),
            Optional(customer.PostalCode),
            customer.Country!.Trim().ToUpperInvariant(),
            claim.Product!.ModelCode!.Trim(),
            claim.Product.SerialNumber!.Trim(),
            DateOnly.ParseExact(purchase.Date!.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            purchase.Place!.Trim(),
            purchase.Price!.Value,
            purchase.Country!.Trim().ToUpperInvariant(),
            claim.ProblemDescription!.Trim());
    }

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+?[0-9 ()./-]+$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    private sealed record NormalizedSubmission(
        string FullName,
        string Email,
        string? Phone,
        string? AddressLine,
        string? City,
        string? PostalCode,
        string CustomerCountry,
        string ModelCode,
        string SerialNumber,
        DateOnly PurchaseDate,
        string Place,
        decimal Price,
        string PurchaseCountry,
        string ProblemDescription);
}
