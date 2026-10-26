using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails;
using Warranty.Guardrails.Pipeline;
using Warranty.Guardrails.Rules;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 003 user story 4 (quickstart X13 – X15; FR-029, FR-030, research R11): later claims on replacement units. Each
/// test hands a unit out through <c>POST /api/claims/{claimId}/replacement/events</c> on its own copy of a seeded
/// replacement claim (X4 on a copy of X1 at Aurora, X7 on a copy of X6 at Borealis), which registers the unit in
/// <c>catalog.replacement_serials</c> and <c>catalog.product_serials</c> (research R10). A new claim for the handed-out serial
/// (X13, X14) or for the returned faulty serial (X15) is then taken through the deterministic steps of a run without an AI
/// call (research R15): the case knowledge (<see cref="ICaseKnowledgeProvider"/>), the <c>invoice_validation</c> tool through
/// the <see cref="ToolInvoker"/>, the deterministic risk assessment (<see cref="IRiskAssessor"/>), the coverage window
/// (<see cref="CoverageWindowCalculator"/>) and the guardrail engine on an otherwise clear approval.
/// <para>
/// The new claims are created through the Application ports (no claim job, so no AI run) with the original purchase's
/// date, place and price, as the customer's invoice shows them. The X15 claims are dated 100 days ahead so that the
/// original claim, finalized 25 days ago, is not a duplicate within 90 days, and the copied claim's serial is added to the
/// catalog: the returned-and-replaced signal is then the only risk signal.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class US4_ReplacementWarrantyTests(WarrantyAppFixture fixture)
{
    private const string NewClaimContact = "replacement.warranty@example.test";
    private const string Problem = "The tablet does not charge any more, whatever cable or charger is used.";
    private const string ApproveText = "Your tablet is covered for manufacturing defects, so your claim is approved.";
    private const string InvoiceRef = "EV-1";
    private const string PhotoRef = "EV-2";
    private const string CoverageRef = "POL-1";
    private const string PeriodRef = "POL-2";

    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;

    /// <summary>Seeded X6-borealis: BOR-SLATE11, Europe, BOR-WP v1 (24 months, minimum replacement cover 6 months); awaiting the return.</summary>
    private static readonly Guid X6 = Guid.Parse("22222222-0006-7000-8000-000000000006");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- X13: a claim for a handed-out Aurora unit --------------------------------------------------------

    [Fact]
    public async Task X13_a_claim_for_a_handed_out_unit_finds_the_unit_in_the_catalog_and_raises_no_PRODUCT_NOT_IN_CATALOG()
    {
        var unit = await HandedOutAuroraUnitAsync();
        var claimId = await NewClaimAsync(unit, unit.UnitSerial, DateTimeOffset.UtcNow);

        var run = await DeterministicRunAsync(unit, claimId);

        var product = run.Case.Product.ShouldNotBeNull("the hand-over registers the unit's serial in the catalog (research R10)");
        product.ModelCode.ShouldBe("AUR-TAB10");
        run.Risk.Signals.Select(s => s.Code).ShouldNotContain(RiskSignalCode.ProductNotInCatalog);
        run.Risk.Signals.Select(s => s.Code).ShouldNotContain(RiskSignalCode.ProductReturnedAndReplaced);
    }

    [Fact]
    public async Task X13_the_claim_for_a_handed_out_unit_carries_the_cover_of_the_original_purchase()
    {
        var unit = await HandedOutAuroraUnitAsync();
        var claimId = await NewClaimAsync(unit, unit.UnitSerial, DateTimeOffset.UtcNow);

        var run = await DeterministicRunAsync(unit, claimId);

        // Aurora's minimum replacement cover is 0 months, so the registered cover end is the original window's end.
        var originalEnd = await OriginalCoverageEndAsync(unit);
        run.Case.ReplacementCover.ShouldBe(new ReplacementCover(unit.OriginalSerial, unit.PurchaseDate, originalEnd));
        (await ScalarAsync<string>("select cover_end_date::text from catalog.replacement_serials where serial_number = @value", unit.UnitSerial))
            .ShouldBe(Iso(originalEnd));
    }

    [Fact]
    public async Task X13_the_coverage_of_a_handed_out_Aurora_unit_ends_with_the_original_window()
    {
        // Needs the replacement cover of T349 as well.
        var unit = await HandedOutAuroraUnitAsync();
        var claimId = await NewClaimAsync(unit, unit.UnitSerial, DateTimeOffset.UtcNow);

        var run = await DeterministicRunAsync(unit, claimId);

        var window = CoverageOf(run);
        window.CoverageEndDate.ShouldBe(await OriginalCoverageEndAsync(unit));
        window.WithinComponentCoverage.ShouldBe(true);
    }

    [Fact(Skip = "Pending T351")]
    public async Task X13_the_invoice_of_the_original_purchase_passes_the_serial_check_of_the_handed_out_unit()
    {
        // Needs the replacement cover of T349 as well: the invoice names the original serial, not the unit's.
        var unit = await HandedOutAuroraUnitAsync();
        var claimId = await NewClaimAsync(unit, unit.UnitSerial, DateTimeOffset.UtcNow);

        var run = await DeterministicRunAsync(unit, claimId);

        var fields = InvoiceFields(run.InvoiceCheck);
        fields[InvoiceFieldNames.SerialNumber].ShouldBe("Match");
        fields.Values.ShouldAllBe(match => match == "Match");
    }

    // ---- X14: a Borealis unit after the original window --------------------------------------------------

    [Fact]
    public async Task X14_a_Borealis_unit_is_covered_until_six_months_after_the_hand_over_when_the_original_window_has_ended()
    {
        // Needs the replacement cover of T349 as well. Bought 22 months ago: the original 24-month window ends in about
        // two months, the 6-month minimum replacement cover from today's hand-over later. The claim is dated in 4 months.
        var unit = await HandedOutBorealisUnitAsync(Today.AddMonths(-22));
        var claimDate = Today.AddMonths(4);
        var claimId = await NewClaimAsync(unit, unit.UnitSerial, new DateTimeOffset(claimDate, new TimeOnly(9, 0), TimeSpan.Zero));

        var run = await DeterministicRunAsync(unit, claimId);

        var originalEnd = await OriginalCoverageEndAsync(unit);
        var minimumEnd = unit.HandedOverOn.AddMonths(6);
        originalEnd.ShouldBeLessThan(claimDate, "precondition: the claim is made after the original window");
        run.Case.ReplacementCover.ShouldBe(new ReplacementCover(unit.OriginalSerial, unit.PurchaseDate, minimumEnd));

        var window = CoverageOf(run);
        window.CoverageEndDate.ShouldBe(minimumEnd);
        window.WithinComponentCoverage.ShouldBe(true);
        CoverageWindowCalculator.Calculate(run.Version.Terms, Region.EU, null, unit.PurchaseDate, claimDate)
            .WithinComponentCoverage.ShouldBe(false, "without the minimum cover the claim would be outside the window");
    }

    // ---- X15: a claim for the returned faulty serial ------------------------------------------------------

    [Fact(Skip = "Pending T352")]
    public async Task X15_a_claim_for_the_returned_faulty_serial_carries_PRODUCT_RETURNED_AND_REPLACED_and_goes_to_human_review()
    {
        // Needs the returned-and-replaced flag of T349 as well.
        var unit = await HandedOutAuroraUnitAsync();
        var claimId = await NewClaimAsync(unit, unit.OriginalSerial, DateTimeOffset.UtcNow.AddDays(100));

        var run = await DeterministicRunAsync(unit, claimId);

        var signal = run.Risk.Signals.ShouldHaveSingleItem();
        (signal.Code, signal.Source, signal.Severity).ShouldBe(
            (RiskSignalCode.ProductReturnedAndReplaced, RiskSignalSource.Deterministic, RiskSeverity.Medium));
        run.Risk.Level.ShouldBe(RiskLevel.Medium);

        // Never approved automatically (001 FR-027): RISK_LOW escalates, so an otherwise clear approval goes to a person.
        var outcome = await ClearApprovalOutcomeAsync(unit, run);
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.RiskMedium);
        outcome.Action.ShouldNotBeNull().Kind.ShouldBe(ActionKind.EscalateToReview);
        var riskLow = outcome.Checks.Single(c => c.Code == GuardrailCheckCode.RiskLow);
        riskLow.Passed.ShouldBeFalse();
        riskLow.Actual.ShouldNotBeNull().ShouldContain(WireName.Of(RiskSignalCode.ProductReturnedAndReplaced));
        outcome.Checks.Where(c => !c.Passed).Select(c => c.Code).ShouldBe([GuardrailCheckCode.RiskLow]);
    }

    [Fact]
    public async Task X15_the_same_claim_for_a_serial_that_was_not_returned_raises_no_signal_and_would_be_approved_automatically()
    {
        // X15's facts without the hand-over: the copy of X1 still awaits its unit, so its serial was not returned and replaced.
        // The clear approval below is the one X15 evaluates, so there the new signal alone sends the claim to a person.
        var original = await CopyOfX1InCatalogAsync();
        var claimId = await NewClaimAsync(original, original.OriginalSerial, DateTimeOffset.UtcNow.AddDays(100));

        var run = await DeterministicRunAsync(original, claimId);

        run.Case.ReplacementCover.ShouldBeNull();
        run.Risk.Signals.ShouldBeEmpty();
        run.Risk.Level.ShouldBe(RiskLevel.Low);
        var outcome = await ClearApprovalOutcomeAsync(original, run);
        outcome.Checks.Where(c => !c.Passed).Select(c => c.Code).ShouldBeEmpty();
        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
        outcome.Action.ShouldNotBeNull().Kind.ShouldBe(ActionKind.FinalizeApproved);
    }

    // ---- Arrangements -------------------------------------------------------------------------------

    /// <summary>
    /// X4 on a copy of X1 (US1's <c>CopyOfX1Async</c>, with its serial added to the catalog): a new AUR-TAB10 unit reserved
    /// and handed over today with the faulty product returned.
    /// </summary>
    private async Task<Unit> HandedOutAuroraUnitAsync()
    {
        var original = await CopyOfX1InCatalogAsync();
        var unitSerial = $"AT10-R-{Guid.CreateVersion7():N}".ToUpperInvariant();
        await RecordAsync(TestStaffUsers.AgentAurora, original.ClaimId, Event("UnitReserved", body => body["serialNumber"] = unitSerial));
        await RecordAsync(TestStaffUsers.AgentAurora, original.ClaimId, HandOver(returnConfirmed: true));
        return original with { UnitSerial = unitSerial, HandedOverOn = Today };
    }

    /// <summary>
    /// X7 on a copy of X6 bought on <paramref name="purchaseDate"/>: the faulty product returned with the claim's serial, a
    /// new BOR-SLATE11 unit reserved and handed over today.
    /// </summary>
    private async Task<Unit> HandedOutBorealisUnitAsync(DateOnly purchaseDate)
    {
        var original = await CopyOfX6Async(purchaseDate);
        var unitSerial = $"BS11-R-{Guid.CreateVersion7():N}".ToUpperInvariant();
        await RecordAsync(TestStaffUsers.AgentBorealis, original.ClaimId, Event("ReturnRecorded", body =>
        {
            body["returnedOn"] = Iso(Today);
            body["serialSeen"] = original.OriginalSerial;
            body["source"] = "InPerson";
        }));
        await RecordAsync(TestStaffUsers.AgentBorealis, original.ClaimId, Event("UnitReserved", body => body["serialNumber"] = unitSerial));
        await RecordAsync(TestStaffUsers.AgentBorealis, original.ClaimId, HandOver(returnConfirmed: null));
        return original with { UnitSerial = unitSerial, HandedOverOn = Today };
    }

    /// <summary>US1's copy of X1 (awaiting a unit), with the copy's serial registered to its product in the catalog.</summary>
    private async Task<Unit> CopyOfX1InCatalogAsync()
    {
        var (claimId, _) = await new US1_ReplacementCollectionTests(fixture).CopyOfX1Async();
        var original = await OriginalAsync(claimId, Aurora, "aurora");
        var productId = await ScalarAsync<Guid>("select product_id from claims.claims where id = @value", claimId);
        using (TenantContextScope.Begin(Aurora, "aurora"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ICatalogRepository>().AddSerial(ProductSerial.Create(Aurora, original.OriginalSerial, productId));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        return original;
    }

    /// <summary>
    /// A new claim like X6 bought on <paramref name="purchaseDate"/>: a copy of the seeded X6 claim row with its own ID,
    /// reference, serial, email and purchase date, the remedy Replacement decided by a reviewer on X6's policy version, and a
    /// replacement with X6's rules, status (<c>AwaitingReturn</c>) and suggested location, as the executor creates it.
    /// </summary>
    private async Task<Unit> CopyOfX6Async(DateOnly purchaseDate)
    {
        var claimId = Guid.CreateVersion7();
        await using (var connection = await OpenAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                create temp table claim_copy on commit drop as select * from claims.claims where id = @source;
                update claim_copy set id = @id, reference = @reference, serial_number = @serial, contact_email = @email, purchase_date = @purchased;
                insert into claims.claims select * from claim_copy;
                """,
                connection);
            command.Parameters.AddWithValue("source", X6);
            command.Parameters.AddWithValue("id", claimId);
            command.Parameters.AddWithValue("reference", ClaimReference.Generate());
            command.Parameters.AddWithValue("serial", $"BS11-C-{claimId:N}".ToUpperInvariant());
            command.Parameters.AddWithValue("email", NewClaimContact);
            command.Parameters.AddWithValue("purchased", purchaseDate);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            command.Transaction = transaction;
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
            await transaction.CommitAsync(Ct);
        }

        var source = await ReplacementSourceAsync(X6);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        using (TenantContextScope.Begin(Borealis, "borealis"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedy = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), Borealis, claimId, source.RemedyPolicyVersionId, Enum.Parse<RemedyScenario>(source.RemedyScenario),
                source.AllowedRemedies.Select(Enum.Parse<RemedyType>), RemedyPendingReason.RepairNotEconomical, at);
            remedy.Decide(RemedyType.Replacement, at);
            scope.ServiceProvider.GetRequiredService<IRemedyRepository>().Add(remedy);
            scope.ServiceProvider.GetRequiredService<IReplacementRepository>().Add(Replacement.Create(
                Guid.CreateVersion7(), Borealis, claimId, source.PolicyVersionId, Enum.Parse<ReplacementProductRule>(source.ProductRule),
                Enum.Parse<ReplacementReturnRule>(source.ReturnRule), source.MinimumCoverMonths, Enum.Parse<ReplacementStatus>(source.Status),
                source.ServiceCenterId, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        return await OriginalAsync(claimId, Borealis, "borealis");
    }

    /// <summary>
    /// A new claim for <paramref name="serial"/> submitted on the claimant portal at <paramref name="submittedAt"/> by the
    /// original claim's customer, with the original purchase (date, place, price, model, region) and an invoice of it; the
    /// product is the catalog product of the serial, if any. No claim job is queued, so no AI run starts.
    /// </summary>
    private async Task<Guid> NewClaimAsync(Unit original, string serial, DateTimeOffset submittedAt)
    {
        var claimId = Guid.CreateVersion7();
        using var tenant = TenantContextScope.Begin(original.TenantId, original.TenantSlug);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimRepository>();
        var source = (await claims.GetAsync(original.ClaimId, Ct)).ShouldNotBeNull();
        var catalogSerial = await scope.ServiceProvider.GetRequiredService<ICatalogRepository>().FindSerialAsync(serial, Ct);
        claims.Add(Claim.Submit(
            claimId, original.TenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, source.CustomerId,
            NewClaimContact, null, source.ProductModelCode, catalogSerial?.ProductId, serial, source.PurchaseDate, source.PurchasePlace,
            source.PurchasePrice, source.Region, Problem, submittedAt));
        claims.AddEvidence(ClaimEvidence.Create(
            Guid.CreateVersion7(), original.TenantId, claimId, 1, EvidenceKind.Invoice, "invoice.pdf", "application/pdf", 2048,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), submittedAt));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        return claimId;
    }

    // ---- The deterministic steps of a run -------------------------------------------------------------

    /// <summary>
    /// Round 1 of the claim without an AI call: the case knowledge, <c>invoice_validation</c> on the claim's invoice with
    /// the values of the original invoice (as the Evidence agent would pass them), the deterministic full risk assessment,
    /// and the one policy version that applies to the purchase.
    /// </summary>
    private async Task<DeterministicRun> DeterministicRunAsync(Unit original, Guid claimId)
    {
        using var tenant = TenantContextScope.Begin(original.TenantId, original.TenantSlug);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var @case = await services.GetRequiredService<ICaseKnowledgeProvider>().GetCaseContextAsync(claimId, 1, Ct);

        var references = new ReferenceRegistry();
        references.IssueEvidence(@case.Evidence.Single(e => e.Kind == EvidenceKind.Invoice).EvidenceId).ShouldBe(InvoiceRef);
        var run = new AdjudicationContext(Guid.CreateVersion7(), tenant, @case, references);
        var invoiceCheck = await services.GetRequiredService<ToolInvoker>().InvokeAsync(
            AgentNames.Evidence,
            run,
            new AiToolCall(
                "call-1",
                ToolNames.InvoiceValidation,
                JsonSerializer.SerializeToElement(new
                {
                    invoiceRef = InvoiceRef,
                    extracted = new
                    {
                        invoiceDate = Iso(original.PurchaseDate),
                        modelCode = @case.ProductModelCode,
                        serial = original.OriginalSerial,
                        amount = @case.PurchasePrice,
                        seller = @case.PurchasePlace,
                    },
                })),
            Ct);
        invoiceCheck.IsError.ShouldBeFalse(invoiceCheck.Result.GetRawText());

        var risk = await services.GetRequiredService<IRiskAssessor>().AssessFullAsync(run, AiRiskReading.None, Ct);
        var region = @case.Region.ShouldNotBeNull();
        var version = (await services.GetRequiredService<IPolicyRepository>().GetVersionsAsync(Ct))
            .Where(v => v.AppliesTo(@case.PurchaseDate, region, @case.Product?.Category))
            .ShouldHaveSingleItem();
        return new DeterministicRun(run.RunId, @case, invoiceCheck.Result, risk, version);
    }

    /// <summary>The coverage window of the claim as the guardrail pipeline computes it for a replacement unit (research R11).</summary>
    private static CoverageWindowResult CoverageOf(DeterministicRun run)
    {
        var cover = run.Case.ReplacementCover.ShouldNotBeNull();
        return CoverageWindowCalculator.Calculate(
            run.Version.Terms, run.Case.Region.ShouldNotBeNull(), null, cover.OriginalPurchaseDate, run.Case.ClaimDate,
            minimumCoverEnd: cover.CoverEndDate);
    }

    /// <summary>
    /// The guardrail engine on the run's deterministic facts (case, risk, coverage window, applicable version with its
    /// clauses) and an otherwise clear AI approval: matching invoice and photo, a cited coverage and period clause, high
    /// confidence and a safe explanation.
    /// </summary>
    private async Task<GuardrailOutcome> ClearApprovalOutcomeAsync(Unit original, DeterministicRun run)
    {
        using var tenant = TenantContextScope.Begin(original.TenantId, original.TenantSlug);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = await services.GetRequiredService<ITenantRepository>().GetCurrentSettingsAsync(Ct);
        var clauses = await services.GetRequiredService<IPolicyRepository>().GetClausesAsync(run.Version.Id, Ct);
        var @case = run.Case;

        var facts = new CaseFacts(
            original.TenantId, @case.ClaimId, run.RunId, @case.Product is not null, @case.Product?.Category, @case.Product?.ClaimValue,
            @case.ReviewerInfoRequested, @case.AutoInfoRequestCount, @case.PurchaseDate, @case.History.PriorApprovedAccidental,
            @case.HasProtectionPlan, @case.ReplacementCover);
        var intake = IntakeResult.Create(
            run.RunId, original.TenantId, [new ValidationCheck("INVOICE_PRESENT", true, null), new ValidationCheck("PHOTO_PRESENT", true, null)],
            "{}", []);
        var evidence = new EvidenceFacts(
            [
                new ConsistencyCheck(InvoiceFieldNames.SerialNumber, @case.SerialNumber, @case.SerialNumber, true),
                new ConsistencyCheck(InvoiceFieldNames.PurchaseDate, Iso(@case.PurchaseDate), Iso(@case.PurchaseDate), true),
            ],
            [new PhotoFinding(PhotoRef, ["OTHER"])],
            []);
        var window = CoverageWindowCalculator.Calculate(run.Version.Terms, @case.Region.ShouldNotBeNull(), null, @case.PurchaseDate, @case.ClaimDate);
        var policy = new PolicyFacts(
            PolicyVersionOutcome.Ok,
            run.Version,
            [
                Clause(run, CoverageRef, clauses.First(c => c.ClauseType == ClauseType.Coverage), original.TenantId),
                Clause(run, PeriodRef, clauses.First(c => c.ClauseType == ClauseType.Period), original.TenantId),
            ],
            window,
            AgentReportsAmbiguity: false);
        var recommendation = Recommendation.CreateValid(
            run.RunId, original.TenantId, """{"decision":"APPROVE","confidence":95,"risk":{"level":"LOW","signals":[]}}""",
            AiDecision.Approve, CoverageDetermination.Covered, 95, "Staff-facing reasoning summary.", ApproveText,
            [new EvidenceCitation(InvoiceRef, "Invoice matches the claimed model and purchase."), new EvidenceCitation(PhotoRef, "No impact marks.")],
            [new PolicyCitation(CoverageRef, PolicyRefRelevance.SupportsCoverage), new PolicyCitation(PeriodRef, PolicyRefRelevance.DefinesPeriod)],
            [], false, "claude-opus-5-5", "decision", "v1");

        return services.GetRequiredService<IGuardrailEngine>().Evaluate(new GuardrailInput(
            settings, facts, intake, evidence, policy, run.Risk, recommendation,
            new HashSet<string>([InvoiceRef, PhotoRef, CoverageRef, PeriodRef], StringComparer.Ordinal), ActorInfo.Automation,
            @case.ClaimDate));
    }

    private static RetrievedPolicyRef Clause(DeterministicRun run, string refId, PolicyClause clause, Guid tenantId) => RetrievedPolicyRef.Create(
        Guid.CreateVersion7(), tenantId, run.RunId, refId, Guid.CreateVersion7(), run.Version.Id, clause.ClauseKey, clause.ClauseType,
        clause.ExclusionCode, "Warranty Policy", run.Version.Version, run.Version.EffectiveFrom, run.Version.EffectiveTo, 0.82f);

    /// <summary>
    /// The original window's end as the hand-over registers it: the standard months of the claim's region in the
    /// replacement's policy version, from the original purchase date.
    /// </summary>
    private async Task<DateOnly> OriginalCoverageEndAsync(Unit original)
    {
        using var tenant = TenantContextScope.Begin(original.TenantId, original.TenantSlug);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var versionId = await ScalarAsync<Guid>("select policy_version_id from claims.replacements where claim_id = @value", original.ClaimId);
        var version = (await scope.ServiceProvider.GetRequiredService<IPolicyRepository>().GetVersionAsync(versionId, Ct)).ShouldNotBeNull();
        return original.PurchaseDate.AddMonths(version.Terms.StandardCoverageMonths[original.Region]);
    }

    private static Dictionary<string, string> InvoiceFields(JsonElement result)
        => result.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("field").GetString()!, f => f.GetProperty("match").GetString()!, StringComparer.Ordinal);

    // ---- API ---------------------------------------------------------------------------------------

    /// <summary>Records <paramref name="body"/> on the claim's replacement with the current ETag of the claim detail; it must succeed.</summary>
    private async Task RecordAsync(TestStaffUser user, Guid claimId, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var detail = await client.GetAsync($"/api/claims/{claimId}", Ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = (await detail.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("replacement").GetProperty("etag").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/replacement/events") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    private static JsonObject HandOver(bool? returnConfirmed) => Event("HandedToCustomer", body =>
    {
        body["source"] = "InPerson";
        body["handedOverOn"] = Iso(Today);
        if (returnConfirmed is { } confirmed)
        {
            body["returnConfirmed"] = confirmed;
        }
    });

    /// <summary>A <c>ReplacementEventRequest</c> reported by phone a minute ago, adjusted by <paramref name="adjust"/>.</summary>
    private static JsonObject Event(string type, Action<JsonObject> adjust)
    {
        var body = new JsonObject
        {
            ["type"] = type,
            ["reportedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1),
            ["source"] = "Phone",
        };
        adjust(body);
        return body;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    /// <summary>The original claim's identity, serial, purchase date and region.</summary>
    private async Task<Unit> OriginalAsync(Guid claimId, Guid tenantId, string tenantSlug)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select serial_number, purchase_date, region from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return new Unit(
            claimId, tenantId, tenantSlug, reader.GetString(0), reader.GetFieldValue<DateOnly>(1), Enum.Parse<Region>(reader.GetString(2)),
            UnitSerial: string.Empty, HandedOverOn: default);
    }

    private async Task<ReplacementSource> ReplacementSourceAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.status, r.product_rule, r.return_rule, r.minimum_cover_months, r.policy_version_id, l.service_center_id,
                   m.policy_version_id, m.scenario, m.allowed_remedies
            from claims.replacements r
            left join claims.replacement_locations l on l.id = r.active_location_id
            join claims.claim_remedies m on m.claim_id = r.claim_id
            where r.claim_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} has no replacement");
        return new ReplacementSource(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetGuid(6), reader.GetString(7), reader.GetFieldValue<string[]>(8));
    }

    /// <summary>Runs <paramref name="sql"/> with <paramref name="value"/> as <c>@value</c> and returns the single result.</summary>
    private async Task<T> ScalarAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteScalarAsync(Ct) is T result ? result : throw new ShouldAssertException($"'{sql}' returned no {typeof(T).Name}.");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>
    /// The original claim of a replacement (its serial is the faulty product's) and, once handed over, the unit's serial and
    /// hand-over date.
    /// </summary>
    private sealed record Unit(
        Guid ClaimId,
        Guid TenantId,
        string TenantSlug,
        string OriginalSerial,
        DateOnly PurchaseDate,
        Region Region,
        string UnitSerial,
        DateOnly HandedOverOn);

    /// <summary>What the deterministic steps of round 1 produced for a new claim.</summary>
    private sealed record DeterministicRun(Guid RunId, CaseContext Case, JsonElement InvoiceCheck, RiskAssessment Risk, PolicyVersion Version);

    private sealed record ReplacementSource(
        string Status,
        string ProductRule,
        string ReturnRule,
        int MinimumCoverMonths,
        Guid PolicyVersionId,
        Guid? ServiceCenterId,
        Guid RemedyPolicyVersionId,
        string RemedyScenario,
        string[] AllowedRemedies);
}
