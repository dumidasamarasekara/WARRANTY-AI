using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Review;

/// <summary>
/// <c>POST /api/claims/{claimId}/review-decisions</c> (T088): each response the contract names — 201 with
/// the <c>ReviewDecision</c>, 400 ValidationProblem, 403 for self-review and for non-reviewers, 404, 409
/// for a claim no longer under review and 412 for a missing or stale <c>If-Match</c>. A synthetic claim
/// matches no recording, so its adjudication fails and it goes to human review without a valid
/// recommendation (an approval needs no justification, <c>overridesAi</c> is false).
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class ReviewDecisionEndpointTests(WarrantyAppFixture fixture)
{
    private const string ClaimantExplanation = "Your device is covered and a repair will be arranged for you.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_approval_returns_201_with_the_decision_and_approves_the_claim()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentAurora);
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);

        using var response = await DecideAsync(client, claimId, await ETagAsync(client, claimId), new
        {
            decision = "Approve",
            claimantExplanation = ClaimantExplanation,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location?.OriginalString.ShouldBe($"/api/claims/{claimId}");
        var decision = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        decision.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        decision.GetProperty("decision").GetString().ShouldBe("Approve");
        decision.GetProperty("claimantExplanation").GetString().ShouldBe(ClaimantExplanation);
        decision.GetProperty("overridesAi").GetBoolean().ShouldBeFalse();
        decision.GetProperty("reviewerName").GetString().ShouldNotBeNullOrWhiteSpace();
        decision.GetProperty("requestedItems").GetArrayLength().ShouldBe(0);
        (await fixture.ClaimStatusAsync(claimId, Ct)).ShouldBe(ClaimStatus.Approved);
    }

    [Fact]
    public async Task A_request_for_information_returns_201_and_moves_the_claim_to_pending_information()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentAurora);
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);

        using var response = await DecideAsync(client, claimId, await ETagAsync(client, claimId), new
        {
            decision = "RequestInformation",
            requestedItems = new[] { new { item = "LEGIBLE_INVOICE", reason = "The invoice photo is too blurry to read." } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var decision = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        decision.GetProperty("decision").GetString().ShouldBe("RequestInformation");
        decision.GetProperty("requestedItems").EnumerateArray().Single().GetProperty("item").GetString().ShouldBe("LEGIBLE_INVOICE");
        (await fixture.ClaimStatusAsync(claimId, Ct)).ShouldBe(ClaimStatus.PendingInformation);
    }

    [Fact]
    public async Task Invalid_decisions_return_400_validation_problems_naming_the_field_and_change_nothing()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentAurora);
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);
        var etag = await ETagAsync(client, claimId);

        var cases = new (string Name, object Body, string Field)[]
        {
            ("unknown decision", new { decision = "Escalate", claimantExplanation = ClaimantExplanation }, "decision"),
            ("missing decision", new { claimantExplanation = ClaimantExplanation }, "decision"),
            ("reject without justification", new { decision = "Reject", claimantExplanation = ClaimantExplanation }, "justification"),
            ("approve without claimant explanation", new { decision = "Approve" }, "claimantExplanation"),
            (
                "claimant explanation with a risk term",
                new { decision = "Approve", claimantExplanation = "We found no fraud here, so your repair is approved." },
                "claimantExplanation"),
            (
                "claimant explanation on a request for information",
                new
                {
                    decision = "RequestInformation",
                    claimantExplanation = ClaimantExplanation,
                    requestedItems = new[] { new { item = "LEGIBLE_INVOICE", reason = "Unreadable invoice." } },
                },
                "claimantExplanation"),
            ("request for information without items", new { decision = "RequestInformation" }, "requestedItems"),
        };

        foreach (var (name, body, field) in cases)
        {
            using var response = await DecideAsync(client, claimId, etag, body);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, name);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", name);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(name);
        }

        using var screened = await DecideAsync(client, claimId, etag, cases[4].Body);
        // The review UI renders this on the claimant message field (T090): key claimantExplanation, message naming the term.
        (await screened.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("claimantExplanation")
            .EnumerateArray().ShouldContain(m => m.GetString()!.Contains("\"fraud\"", StringComparison.Ordinal));

        using var smuggled = await DecideAsync(client, claimId, etag, new
        {
            decision = "Approve",
            claimantExplanation = ClaimantExplanation,
            reviewerSub = TestStaffUsers.AuditorAurora.Subject,
        });
        smuggled.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        smuggled.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        (await fixture.ClaimStatusAsync(claimId, Ct)).ShouldBe(ClaimStatus.UnderReview);
        (await ETagAsync(client, claimId)).ShouldBe(etag);
    }

    [Fact]
    public async Task A_reviewer_deciding_a_claim_they_submitted_gets_403_and_the_claim_stays_under_review()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentReviewerAurora);
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentReviewerAurora);

        using var response = await DecideAsync(client, claimId, await ETagAsync(client, claimId), new
        {
            decision = "Approve",
            claimantExplanation = ClaimantExplanation,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString()
            .ShouldBe("You submitted this claim; another reviewer must decide it");
        (await fixture.ClaimStatusAsync(claimId, Ct)).ShouldBe(ClaimStatus.UnderReview);
    }

    [Fact]
    public async Task Users_without_the_reviewer_role_get_403()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentAurora);

        foreach (var user in new[] { TestStaffUsers.AgentAurora, TestStaffUsers.AuditorAurora })
        {
            using var client = fixture.CreateStaffClient(user);
            using var response = await DecideAsync(client, claimId, await ETagAsync(client, claimId), new
            {
                decision = "Approve",
                claimantExplanation = ClaimantExplanation,
            });

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, user.Username);
        }

        (await fixture.ClaimStatusAsync(claimId, Ct)).ShouldBe(ClaimStatus.UnderReview);
    }

    [Fact]
    public async Task An_unknown_claim_gets_404()
    {
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);

        using var response = await DecideAsync(client, Guid.CreateVersion7(), "\"1\"", new
        {
            decision = "Approve",
            claimantExplanation = ClaimantExplanation,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_missing_or_stale_if_match_gets_412_and_changes_nothing()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentAurora);
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);
        var etag = await ETagAsync(client, claimId);
        var stale = $"\"{uint.Parse(etag.Trim('"'), System.Globalization.CultureInfo.InvariantCulture) + 1}\"";

        foreach (var ifMatch in new string?[] { null, stale, "*" })
        {
            using var response = await DecideAsync(client, claimId, ifMatch, new
            {
                decision = "Approve",
                claimantExplanation = ClaimantExplanation,
            });

            response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, ifMatch ?? "(none)");
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", ifMatch ?? "(none)");
        }

        (await fixture.ClaimStatusAsync(claimId, Ct)).ShouldBe(ClaimStatus.UnderReview);
    }

    [Fact]
    public async Task A_decision_on_a_claim_no_longer_under_review_gets_409_and_a_stale_etag_412()
    {
        var claimId = await SubmitAndWaitForReviewAsync(TestStaffUsers.AgentAurora);
        using var client = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora);
        var before = await ETagAsync(client, claimId);
        var approve = new { decision = "Approve", claimantExplanation = ClaimantExplanation };

        using var first = await DecideAsync(client, claimId, before, approve);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var stale = await DecideAsync(client, claimId, before, approve);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        using var decided = await DecideAsync(client, claimId, await ETagAsync(client, claimId), approve);
        decided.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        decided.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var detail = await client.GetAsync($"/api/claims/{claimId}", Ct);
        (await detail.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reviewDecisions").GetArrayLength().ShouldBe(1);
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid claimId, string? ifMatch, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/review-decisions")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> ETagAsync(HttpClient client, Guid claimId)
    {
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag?.Tag ?? throw new InvalidOperationException("The claim detail has no ETag.");
    }

    private async Task<Guid> SubmitAndWaitForReviewAsync(TestStaffUser submitter)
    {
        using var client = fixture.CreateStaffClient(submitter);
        using var response = await client.PostAsync("/api/claims", Submission(NewSerial()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var claimId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("claimId").GetGuid();
        (await fixture.WaitForClaimStatusAsync(claimId, ClaimStatus.UnderReview)).ShouldBe(ClaimStatus.UnderReview);
        return claimId;
    }
}
