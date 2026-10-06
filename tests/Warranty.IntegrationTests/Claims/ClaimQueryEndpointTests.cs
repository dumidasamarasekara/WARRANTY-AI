using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// <c>GET /api/claims</c>, <c>GET /api/claims/{claimId}</c> and the evidence content route (T085): the
/// staff list with its status filter and paging limits, the claim detail with its ETag and the
/// claims-agent projection (FR-005), and evidence streamed from the tenant's container. A synthetic
/// claim matches no recording, so its adjudication fails and it goes to human review.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class ClaimQueryEndpointTests(WarrantyAppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reviewer_gets_the_full_claim_detail_with_an_etag()
    {
        var claimId = await SubmitAndWaitForReviewAsync(Pdf(), Jpeg());
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);

        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldMatch("^\"[0-9]+\"$");
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        detail.GetProperty("claimId").GetGuid().ShouldBe(claimId);
        detail.GetProperty("status").GetString().ShouldBe("UnderReview");
        detail.GetProperty("channel").GetString().ShouldBe("AgentPortal");
        detail.GetProperty("customer").GetProperty("email").GetString().ShouldBe(ContactEmail);
        detail.GetProperty("product").GetProperty("inCatalog").GetBoolean().ShouldBeTrue();
        detail.GetProperty("product").GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("purchase").GetProperty("currency").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("autoInfoRequestCount").GetInt32().ShouldBe(0);
        detail.GetProperty("reviewerInfoRequested").GetBoolean().ShouldBeFalse();
        detail.GetProperty("reviewDecisions").GetArrayLength().ShouldBe(0);

        var evidence = detail.GetProperty("evidence").EnumerateArray().ToList();
        evidence.Select(e => e.GetProperty("kind").GetString()).ShouldBe(["Invoice", "Photo"], ignoreOrder: true);
        evidence.ShouldAllBe(e => e.GetProperty("round").GetInt32() == 1);

        var evaluation = detail.GetProperty("latestEvaluation");
        evaluation.GetProperty("round").GetInt32().ShouldBe(1);
        var guardrails = evaluation.GetProperty("guardrails");
        guardrails.GetProperty("disposition").GetString().ShouldBe("HumanReview");
        guardrails.GetProperty("reasons").GetArrayLength().ShouldBeGreaterThan(0);
        guardrails.GetProperty("checks").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_claims_agent_gets_no_risk_reasoning_guardrail_checks_or_risk_reasons()
    {
        var claimId = await SubmitAndWaitForReviewAsync(Pdf(), Jpeg());
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var evaluation = detail.GetProperty("latestEvaluation");
        evaluation.TryGetProperty("risk", out _).ShouldBeFalse();
        if (evaluation.TryGetProperty("recommendation", out var recommendation))
        {
            recommendation.TryGetProperty("reasoningSummary", out _).ShouldBeFalse();
        }

        var guardrails = evaluation.GetProperty("guardrails");
        guardrails.TryGetProperty("checks", out _).ShouldBeFalse();
        var reasons = guardrails.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToList();
        reasons.ShouldNotBeEmpty();
        reasons.ShouldNotContain(r => r.StartsWith("RISK_", StringComparison.Ordinal));
        reasons.ShouldNotContain("EVIDENCE_CONFLICT");
        reasons.ShouldNotContain("AI_DETERMINISTIC_DISAGREEMENT");
        reasons.ShouldNotContain("UNSAFE_CLAIMANT_TEXT");
    }

    [Fact]
    public async Task Evidence_content_streams_the_stored_file_with_its_content_type()
    {
        var invoice = Pdf();
        var photo = Png();
        var claimId = await SubmitAsync(invoice, photo);
        using var client = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);
        using var detailResponse = await client.GetAsync($"/api/claims/{claimId}", Ct);
        var evidence = (await detailResponse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("evidence").EnumerateArray().ToList();

        foreach (var (kind, file) in new[] { ("Invoice", invoice), ("Photo", photo) })
        {
            var evidenceId = evidence.Single(e => e.GetProperty("kind").GetString() == kind).GetProperty("evidenceId").GetGuid();
            using var response = await client.GetAsync($"/api/claims/{claimId}/evidence/{evidenceId}/content", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, kind);
            response.Content.Headers.ContentType?.MediaType.ShouldBe(file.ContentType, kind);
            (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(await StoredBytesAsync(file, Ct), kind); // photos are stored sanitized (T110)

            // The endpoint's own caching headers survive the API-wide security headers (T111).
            response.Headers.CacheControl!.NoStore.ShouldBeTrue(kind);
            response.Headers.CacheControl.Private.ShouldBeTrue(kind);
            response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff", kind);
        }
    }

    [Fact]
    public async Task The_claim_list_filters_by_status_and_rejects_bad_paging()
    {
        var claimId = await SubmitAndWaitForReviewAsync(Pdf(), Jpeg());
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var underReview = await client.GetAsync("/api/claims?status=UnderReview&pageSize=100", Ct);
        underReview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await underReview.Content.ReadFromJsonAsync<JsonElement>(Ct);
        page.GetProperty("page").GetInt32().ShouldBe(1);
        page.GetProperty("pageSize").GetInt32().ShouldBe(100);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.ShouldAllBe(i => i.GetProperty("status").GetString() == "UnderReview");
        var item = items.Single(i => i.GetProperty("claimId").GetGuid() == claimId);
        item.GetProperty("productName").GetString().ShouldNotBeNullOrWhiteSpace();
        item.GetProperty("disposition").GetString().ShouldBe("HumanReview");

        using var approved = await client.GetAsync("/api/claims?status=Approved&pageSize=100", Ct);
        (await approved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
            .ShouldNotContain(i => i.GetProperty("claimId").GetGuid() == claimId);

        foreach (var query in new[] { "status=Closed", "page=0", "pageSize=0", "pageSize=101" })
        {
            using var bad = await client.GetAsync($"/api/claims?{query}", Ct);
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest, query);
            bad.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", query);
        }
    }

    private async Task<Guid> SubmitAndWaitForReviewAsync(EvidenceFile invoice, EvidenceFile photo)
    {
        var claimId = await SubmitAsync(invoice, photo);
        (await fixture.WaitForClaimStatusAsync(claimId, ClaimStatus.UnderReview)).ShouldBe(ClaimStatus.UnderReview);
        return claimId;
    }

    private async Task<Guid> SubmitAsync(EvidenceFile invoice, EvidenceFile photo)
    {
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var response = await client.PostAsync("/api/claims", Submission(NewSerial(), invoices: [invoice], photos: [photo]), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("claimId").GetGuid();
    }
}
