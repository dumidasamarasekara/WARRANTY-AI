using System.Globalization;
using System.Text;
using System.Text.Json;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.AI.Harness.Agents;

/// <summary>
/// The Intake Agent (contracts/agents-and-tools.md). A deterministic part runs the FR-009 checks and
/// lists the missing items; a model part structures the claimant's problem description on the
/// <c>extraction</c> route into an <c>intake-extraction</c> output. Before the model call the agent
/// runs <c>customer_lookup</c> and <c>product_lookup</c> itself through the run's scoped tool invoker
/// (so the calls are allow-list checked and recorded like model tool calls); the model is offered the
/// same two tools. The description travels only as untrusted content and the user turn carries case
/// facts without customer identifiers (FR-006a, FR-019).
/// </summary>
/// <remarks>
/// The <see cref="IntakeResult"/> is added to the run's unit of work (<c>adjudication.intake_results</c>)
/// whatever the model did; the caller commits it. When the model step fails, the result keeps the
/// deterministic validation, stores <c>{}</c> as extraction and comes back as the output of the failed
/// <see cref="AgentResult{T}"/>, so the guardrails can still evaluate it (FR-031).
/// </remarks>
public sealed class IntakeAgent(
    AgentTurnLoop loop,
    ContextBuilder contextBuilder,
    SchemaValidator schemas,
    IAdjudicationRepository adjudication,
    TimeProvider time) : IAgent<CaseContext, IntakeResult>
{
    public const string Route = "extraction";

    /// <summary>The extraction stored when the model step produced none.</summary>
    public const string NoExtraction = "{}";

    /// <summary>The Intake Agent's descriptor (budgets: contracts/agents-and-tools.md).</summary>
    public static AgentDescriptor Agent { get; } = new(
        AgentNames.Intake,
        Route,
        new PromptRef("intake", 1),
        "warranty-ai/intake-extraction/v1",
        [ToolNames.CustomerLookup, ToolNames.ProductLookup],
        MaxTurns: 2,
        InputTokenBudget: 6_000);

    /// <summary>The only variable of the <c>intake</c> prompt template.</summary>
    private static readonly IReadOnlyDictionary<string, string> PromptVariables =
        new Dictionary<string, string>(StringComparer.Ordinal) { [UntrustedContent.PreambleVariable] = UntrustedContent.Preamble };

    private static readonly IReadOnlySet<string> InvoiceTypes =
        new HashSet<string>(["application/pdf", "image/jpeg", "image/png", "image/webp"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> PhotoTypes =
        new HashSet<string>(["image/jpeg", "image/png", "image/webp"], StringComparer.Ordinal);

    public AgentDescriptor Descriptor => Agent;

    public async Task<AgentResult<IntakeResult>> RunAsync(CaseContext input, AgentExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);
        if (input.ClaimId != ctx.Run.ClaimId)
        {
            throw new ArgumentException("The case is not the run's claim.", nameof(input));
        }

        using var span = ctx.Trace.StartSpan($"agent.{Agent.Name}", new Dictionary<string, object?> { ["agent"] = Agent.Name, ["claim_id"] = input.ClaimId });
        var diagnostics = new List<string>();

        var customer = await LookupAsync<CustomerLookupResult>(ctx, ToolNames.CustomerLookup, new { }, diagnostics, ct);
        var product = IsBlank(input.ProductModelCode) || IsBlank(input.SerialNumber)
            ? null
            : await LookupAsync<ProductLookupResult>(
                ctx, ToolNames.ProductLookup, new { modelCode = input.ProductModelCode, serialNumber = input.SerialNumber }, diagnostics, ct);

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var (validation, missingItems) = Validate(input, customer, today);

        var extraction = await ExtractAsync(input, product, ctx, ct);
        diagnostics.AddRange(extraction.Diagnostics);

        var result = IntakeResult.Create(
            ctx.Run.RunId, ctx.Run.Tenant.TenantId, validation, extraction.Json ?? NoExtraction, missingItems);
        adjudication.AddIntakeResult(result);

        return extraction.Status == AgentStatus.Succeeded
            ? AgentResult<IntakeResult>.Success(result, [.. diagnostics])
            : new AgentResult<IntakeResult>(extraction.Status, result, diagnostics);
    }

    /// <summary>
    /// The deterministic FR-009 checks, in a fixed order, and the items to request for the failed ones.
    /// Dates and file types were already enforced at submission; these checks re-verify them as a backstop.
    /// </summary>
    public static (IReadOnlyList<ValidationCheck> Validation, IReadOnlyList<RequestedItem> MissingItems) Validate(
        CaseContext input, CustomerLookupResult? customer, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(input);
        var checks = new List<ValidationCheck>();
        var missing = new List<RequestedItem>();

        // REQUIRED_FIELDS
        var missingFields = new List<string>();
        AddIf(missingFields, IsBlank(input.ProductModelCode), "product.modelCode");
        AddIf(missingFields, IsBlank(input.SerialNumber), "product.serialNumber");
        AddIf(missingFields, input.PurchaseDate == default, "purchase.date");
        AddIf(missingFields, IsBlank(input.PurchasePlace), "purchase.place");
        AddIf(missingFields, input.PurchasePrice <= 0, "purchase.price");
        AddIf(missingFields, IsBlank(input.ProblemDescription), "problemDescription");
        checks.Add(Check(ValidationCheckCodes.RequiredFields, missingFields.Count == 0, missingFields.Count == 0 ? null : $"Missing: {string.Join(", ", missingFields)}."));
        if (input.PurchaseDate == default)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.PurchaseDate, "Please tell us the date you bought the product."));
        }

        if (IsBlank(input.ProblemDescription))
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.ProblemDetails, "Please describe the problem with the product."));
        }

        var otherFields = missingFields.Where(f => f is not "purchase.date" and not "problemDescription").ToList();
        if (otherFields.Count > 0)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.Other, $"Please complete the missing claim details: {string.Join(", ", otherFields)}."));
        }

        // PHOTO_PRESENT / INVOICE_PRESENT
        var photos = input.Evidence.Where(e => e.Kind == EvidenceKind.Photo).ToList();
        var invoices = input.Evidence.Where(e => e.Kind == EvidenceKind.Invoice).ToList();
        checks.Add(Check(ValidationCheckCodes.PhotoPresent, photos.Count > 0, Count(photos.Count, "photo")));
        if (photos.Count == 0)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.PhotoOfDamage, "Please add at least one photo that shows the product and the problem."));
        }

        checks.Add(Check(ValidationCheckCodes.InvoicePresent, invoices.Count > 0, Count(invoices.Count, "invoice")));
        if (invoices.Count == 0)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.Invoice, "Please add the invoice or receipt of the purchase."));
        }

        // FILE_TYPES — the stored content type was detected from the file content at upload.
        var badInvoices = invoices.Where(e => !IsSupported(e, InvoiceTypes)).ToList();
        var badPhotos = photos.Where(e => !IsSupported(e, PhotoTypes)).ToList();
        var badOthers = input.Evidence.Where(e => e.Kind is not (EvidenceKind.Invoice or EvidenceKind.Photo)).ToList();
        var unsupported = badInvoices.Concat(badPhotos).Concat(badOthers).ToList();
        checks.Add(Check(
            ValidationCheckCodes.FileTypes,
            unsupported.Count == 0,
            unsupported.Count == 0 ? null : $"Unsupported: {string.Join(", ", unsupported.Select(e => $"{e.Kind} {e.ContentType}"))}."));
        if (badInvoices.Count > 0 && badInvoices.Count == invoices.Count)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.Invoice, "Please add the invoice as a PDF, JPEG, PNG or WebP file."));
        }

        if (badPhotos.Count > 0 && badPhotos.Count == photos.Count)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.PhotoOfDamage, "Please add photos as JPEG, PNG or WebP files."));
        }

        // PURCHASE_DATE_NOT_FUTURE / PURCHASE_DATE_BEFORE_CLAIM
        var dated = input.PurchaseDate != default;
        var notFuture = dated && input.PurchaseDate <= today;
        var beforeClaim = dated && input.PurchaseDate <= input.ClaimDate;
        checks.Add(Check(ValidationCheckCodes.PurchaseDateNotFuture, notFuture, notFuture || !dated ? null : $"Purchase date {Iso(input.PurchaseDate)} is after {Iso(today)}."));
        checks.Add(Check(
            ValidationCheckCodes.PurchaseDateBeforeClaim,
            beforeClaim,
            beforeClaim || !dated ? null : $"Purchase date {Iso(input.PurchaseDate)} is after the claim date {Iso(input.ClaimDate)}."));
        if (dated && (!notFuture || !beforeClaim))
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.PurchaseDate, "The purchase date cannot be in the future or after the claim date. Please check it."));
        }

        // REGION_DETERMINED — from the purchase information, else the customer's address.
        var region = RegionOf(input, customer);
        checks.Add(Check(ValidationCheckCodes.RegionDetermined, region is not null, region?.ToString()));
        if (region is null)
        {
            missing.Add(RequestedItem.Create(RequestedItemCodes.Other, "Please tell us the country where you bought the product."));
        }

        return (checks, missing.Distinct().ToList());
    }

    /// <summary>The claim's region: the purchase region, else the customer's region from the case or <c>customer_lookup</c>.</summary>
    public static Region? RegionOf(CaseContext input, CustomerLookupResult? customer)
    {
        ArgumentNullException.ThrowIfNull(input);
        if ((input.Region ?? input.Customer.Region) is { } known)
        {
            return known;
        }

        return Enum.TryParse<Region>(customer?.Region, ignoreCase: false, out var looked) && Enum.IsDefined(looked) ? looked : null;
    }

    private async Task<Extraction> ExtractAsync(CaseContext input, ProductLookupResult? product, AgentExecutionContext ctx, CancellationToken ct)
    {
        var context = contextBuilder.Build(
            [
                ContextItem.Text("instructions", ContextPriority.Instructions, "Structure the claimant's problem description below. Return only the intake extraction."),
                ContextItem.Text("case-facts", ContextPriority.CaseFacts, CaseFacts(input, product)),
                new ContextItem("description", ContextPriority.CaseFacts, [UntrustedContent.ClaimantDescription(input.ProblemDescription ?? string.Empty)]),
            ],
            Agent.InputTokenBudget);
        if (context.IsOverflow)
        {
            return Extraction.Failed(
                AgentStatus.Failed,
                string.Create(CultureInfo.InvariantCulture, $"context_overflow: the case facts and description need ~{context.EstimatedTokens} tokens; the budget is {context.Budget}."));
        }

        var conversation = new AiConversation();
        conversation.AddUser([.. context.Parts]);
        var outcome = await loop.RunAsync(
            new AgentTurnLoopRequest(Agent, conversation, PromptVariables, schemas.GetOutputSchema(Agent.OutputSchemaId!), AgentTurnLoopRequest.DefaultTurnTimeout),
            ctx,
            ct);

        if (outcome.Status != AgentStatus.Succeeded)
        {
            var failure = outcome.LastTurn.Failure;
            var message = $"{outcome.Outcome}: {failure?.Message ?? $"the model stopped with '{outcome.LastTurn.Stop}'."}";
            return Extraction.Failed(outcome.Status, [message, .. failure?.ValidationErrors ?? []]);
        }

        if (outcome.LastTurn.StructuredOutput is not { } output)
        {
            return Extraction.Failed(AgentStatus.InvalidOutput, "The model returned no structured output.");
        }

        var validation = schemas.Validate(Agent.OutputSchemaId!, output);
        return validation.IsValid
            ? new Extraction(AgentStatus.Succeeded, output.GetRawText(), [])
            : Extraction.Failed(AgentStatus.InvalidOutput, [.. validation.Errors]);
    }

    /// <summary>Case facts for the user turn: product, dates and region only — never customer identifiers (FR-006a).</summary>
    private static string CaseFacts(CaseContext input, ProductLookupResult? lookup)
    {
        var name = input.Product?.Name ?? (lookup is { Found: true } ? lookup.ProductName : null);
        var category = input.Product?.Category ?? (lookup is { Found: true } ? lookup.Category : null);
        var region = input.Region ?? input.Customer.Region;
        var facts = new StringBuilder("Case facts:\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Product model code: {input.ProductModelCode}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Serial number: {input.SerialNumber}\n");
        facts.Append(name is null
            ? "- Product: not found in the catalog\n"
            : string.Create(CultureInfo.InvariantCulture, $"- Product: {name} (category: {category ?? "unknown"})\n"));
        facts.Append(CultureInfo.InvariantCulture, $"- Region: {region?.ToString() ?? "unknown"}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Purchase date: {Iso(input.PurchaseDate)}\n");
        facts.Append(CultureInfo.InvariantCulture, $"- Claim date: {Iso(input.ClaimDate)}");
        return facts.ToString();
    }

    /// <summary>
    /// Runs a lookup tool through the run's scoped invoker (allow-list check and <c>aiops.tool_calls</c> row
    /// included). A failed lookup is a diagnostic, not a failure: the checks then use the case data alone.
    /// </summary>
    private static async Task<T?> LookupAsync<T>(AgentExecutionContext ctx, string tool, object arguments, List<string> diagnostics, CancellationToken ct)
        where T : class
    {
        var result = await ctx.Tools.InvokeAsync(
            new AiToolCall($"{Agent.Name}-{tool}", tool, JsonSerializer.SerializeToElement(arguments, ToolJson.Options)), ct);
        if (result.IsError)
        {
            diagnostics.Add($"{tool}: {ErrorOf(result.Result)}");
            return null;
        }

        try
        {
            return result.Result.Deserialize<T>(ToolJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"{tool}: unreadable result ({ex.Message}).");
            return null;
        }
    }

    private static string ErrorOf(JsonElement result)
        => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()!
            : "the lookup failed.";

    private static bool IsSupported(CaseEvidence evidence, IReadOnlySet<string> types)
        => types.Contains(evidence.ContentType) && evidence.SizeBytes is > 0 and <= ClaimEvidence.MaxSizeBytes;

    private static ValidationCheck Check(string code, bool passed, string? detail) => new(code, passed, detail);

    private static void AddIf(List<string> fields, bool condition, string field)
    {
        if (condition)
        {
            fields.Add(field);
        }
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Count(int count, string noun)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The model step's outcome: the validated extraction JSON, or a failing status with diagnostics.</summary>
    private sealed record Extraction(AgentStatus Status, string? Json, IReadOnlyList<string> Diagnostics)
    {
        public static Extraction Failed(AgentStatus status, params string[] diagnostics) => new(status, null, diagnostics);
    }
}
