using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;
using Warranty.IntegrationTests.Scenarios;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// Feature 002 user story 1 (quickstart R1, R2, R3, R13): an approved claim gets the remedy its own policy version
/// gives for the initial scenario (research R13). R1 and R2 are the seeded approved claims of
/// <c>seed/tenants/*/remedy-scenarios.json</c> (T222): Repair set by policy from row <c>coveredDefect</c> and a repair
/// awaiting hand-over at the first eligible centre of the claim's region (priority, then name). R3: a reviewer cannot
/// pick a remedy the Aurora policy does not allow, neither when approving an escalated claim nor afterwards. R13: the
/// 001 S1 claim (replayed) is approved exactly as in 001 and then gets remedy Repair and a suggested centre instead of
/// a simulated repair request (research R8, R14).
/// <para>
/// Seeded claims are only read here, or sent requests that must be refused, so other tests can keep using them.
/// Escalated claims for R3 are synthetic (<c>SyntheticClaims</c>): they match no recording, so their
/// adjudication fails and they go to human review, submitted by <c>agent.aurora</c>, never the deciding reviewer.
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "002")]
public sealed class US1_RemedyDecisionTests(WarrantyAppFixture fixture)
{
    private const string PendingClaimDetailRemedy = "Pending T229/T230";
    private const string PendingRemedyDecisions = "Pending T227/T230";

    /// <summary>Seeded R1: Aurora AUR-TAB10, North America, approved by the system.</summary>
    private static readonly Guid R1 = Guid.Parse("11111111-0005-7000-8000-000000000001");

    /// <summary>Seeded R2: Borealis BOR-SLATE11, Europe, approved by the system.</summary>
    private static readonly Guid R2 = Guid.Parse("22222222-0005-7000-8000-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, int, string> SeededClaims => new()
    {
        { "R1", "AUR-WP", 2, "Aurora Service Center Springfield" },
        { "R2", "BOR-WP", 1, "Borealis Repair Hub Lyon" },
    };

    // ---- R1, R2: the seeded approved claims ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(SeededClaims))]
    public async Task R1_R2_a_covered_defect_approved_automatically_has_remedy_Repair_set_by_its_own_policy_version(
        string scenario, string policyCode, int version, string centreName)
    {
        var claimId = ClaimOf(scenario);

        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);

        var remedy = (await RemedyRowAsync(claimId)).ShouldNotBeNull();
        remedy.State.ShouldBe("InProgress");
        remedy.Remedy.ShouldBe("Repair");
        remedy.Scenario.ShouldBe("CoveredDefect");
        remedy.AllowedRemedies.ShouldBe(["Repair"]);
        remedy.PendingReason.ShouldBeNull();
        remedy.Policy.ShouldBe($"{policyCode} v{version}");

        // One evaluation, then the decision by policy, both from row coveredDefect of the same version.
        var decisions = await RemedyDecisionRowsAsync(claimId);
        decisions.Select(d => $"{d.Seq}:{d.Kind}:{d.TableRow}").ShouldBe(["1:Evaluated:coveredDefect", "2:Decided:coveredDefect"]);
        decisions.ShouldAllBe(d => d.Policy == remedy.Policy && d.Scenario == "CoveredDefect");
        decisions[1].Remedy.ShouldBe("Repair");
        decisions[1].DecidedBy.ShouldBe("Policy");
        decisions[1].Actor.ShouldBe("system");
        decisions[1].Reason.ShouldBeNull();

        // The repair awaits the hand-over at the suggested centre: the first eligible one (priority, then name).
        var repair = (await RepairRowAsync(claimId)).ShouldNotBeNull();
        repair.Status.ShouldBe("AwaitingHandover");
        repair.FailedAttempts.ShouldBe(0);
        var suggested = await FirstEligibleCentreAsync(claimId);
        suggested.Name.ShouldBe(centreName);
        var handover = (await HandoversAsync(claimId)).ShouldHaveSingleItem();
        handover.Assignment.ShouldBe("Suggested");
        handover.IsActive.ShouldBeTrue();
        handover.CentreId.ShouldBe(suggested.Id);
        handover.HandedOverOn.ShouldBeNull();
        (await RepairEventTypesAsync(claimId)).ShouldBe(["CentreSuggested"]);

        // No 001 simulated repair request for a claim whose remedy is a Repair (research R8).
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(0);

        // The trail explains the remedy right after the approval.
        var steps = (await TrailAsync(claimId)).Select(e => e.Step).ToList();
        steps.Take(5).ShouldBe(["ClaimSubmitted", "AutoApproved", "RemedyRecommended", "RemedyDecided", "RepairCentreAssigned"]);
    }

    [Theory(Skip = PendingClaimDetailRemedy)]
    [MemberData(nameof(SeededClaims))]
    public async Task R1_R2_the_claim_detail_shows_the_remedy_its_policy_row_and_its_history(
        string scenario, string policyCode, int version, string centreName)
    {
        _ = centreName;
        var claimId = ClaimOf(scenario);
        var reviewer = scenario == "R1" ? TestStaffUsers.ReviewerAurora : TestStaffUsers.ReviewerBorealis;

        var detail = await DetailAsync(reviewer, claimId);

        detail.Status.ShouldBe(HttpStatusCode.OK);
        detail.Body.GetProperty("status").GetString().ShouldBe("Approved");
        detail.Body.TryGetProperty("remedyPreview", out _).ShouldBeFalse("an approved claim has a remedy, not a preview");
        var remedy = detail.Body.GetProperty("remedy");
        remedy.GetProperty("state").GetString().ShouldBe("InProgress");
        remedy.GetProperty("remedy").GetString().ShouldBe("Repair");
        remedy.GetProperty("scenario").GetString().ShouldBe("CoveredDefect");
        remedy.GetProperty("tableRow").GetString().ShouldBe("coveredDefect");
        Strings(remedy.GetProperty("allowedRemedies")).ShouldBe(["Repair"]);
        remedy.GetProperty("recommendedRemedy").GetString().ShouldBe("Repair");
        remedy.TryGetProperty("pendingReason", out _).ShouldBeFalse();
        remedy.GetProperty("policyVersion").GetProperty("policyCode").GetString().ShouldBe(policyCode);
        remedy.GetProperty("policyVersion").GetProperty("version").GetInt32().ShouldBe(version);
        remedy.GetProperty("etag").GetString().ShouldNotBeNullOrWhiteSpace();

        var history = remedy.GetProperty("history").EnumerateArray().ToList();
        history.Select(h => $"{h.GetProperty("seq").GetInt32()}:{h.GetProperty("kind").GetString()}").ShouldBe(["1:Evaluated", "2:Decided"]);
        var decided = history[1];
        decided.GetProperty("remedy").GetString().ShouldBe("Repair");
        decided.GetProperty("decidedBy").GetString().ShouldBe("Policy");
        decided.GetProperty("actor").GetString().ShouldBe("system");
        history.ShouldAllBe(h => h.GetProperty("scenario").GetString() == "CoveredDefect");
    }

    // ---- R3: a reviewer cannot pick a remedy the policy does not allow ------------------------------

    [Fact]
    public async Task R3_approving_an_escalated_Aurora_claim_with_Refund_is_refused_naming_Repair_and_with_Repair_is_recorded()
    {
        var claimId = await EscalatedAuroraClaimAsync();
        var reviewer = TestStaffUsers.ReviewerAurora;

        // While under review, the preview shows what approving would set under AUR-WP's rules.
        var detail = await DetailAsync(reviewer, claimId);
        var preview = detail.Body.GetProperty("remedyPreview");
        Strings(preview.GetProperty("allowedRemedies")).ShouldBe(["Repair"]);
        preview.GetProperty("needsChoice").GetBoolean().ShouldBeFalse();

        var refused = await ReviewDecisionAsync(reviewer, claimId, detail.ETag, Approval("Refund"));

        refused.ShouldBeValidationProblem("remedy");
        refused.Body.ShouldContain("Repair");
        (await ClaimRowAsync(claimId)).Status.ShouldBe(ClaimStatus.UnderReview);
        (await CountAsync("select count(*) from review.review_decisions where claim_id = @value", claimId)).ShouldBe(0);
        (await RemedyRowAsync(claimId)).ShouldBeNull();

        var recorded = await ReviewDecisionAsync(reviewer, claimId, detail.ETag, Approval("Repair"));

        recorded.Status.ShouldBe(HttpStatusCode.Created, recorded.Body);
        await ShouldBeApprovedByTheReviewerWithRemedyRepairAsync(claimId);
    }

    [Fact]
    public async Task R3_approving_an_escalated_Aurora_claim_without_a_remedy_sets_the_only_allowed_remedy_Repair()
    {
        var claimId = await EscalatedAuroraClaimAsync();
        var reviewer = TestStaffUsers.ReviewerAurora;
        var detail = await DetailAsync(reviewer, claimId);

        var recorded = await ReviewDecisionAsync(reviewer, claimId, detail.ETag, Approval(remedy: null));

        recorded.Status.ShouldBe(HttpStatusCode.Created, recorded.Body);
        await ShouldBeApprovedByTheReviewerWithRemedyRepairAsync(claimId);
    }

    [Fact(Skip = PendingRemedyDecisions)]
    public async Task R3_changing_the_remedy_of_an_approved_Aurora_claim_to_Refund_is_refused_naming_Repair()
    {
        var reviewer = TestStaffUsers.ReviewerAurora;
        var detail = await DetailAsync(reviewer, R1);
        var remedyETag = detail.Body.GetProperty("remedy").GetProperty("etag").GetString().ShouldNotBeNull();
        var decisionsBefore = await RemedyDecisionRowsAsync(R1);

        var refused = await RemedyDecisionAsync(
            reviewer, R1, remedyETag,
            new JsonObject { ["remedy"] = "Refund", ["reason"] = "The customer asked for their money back instead of a repair." });

        refused.ShouldBeValidationProblem("remedy");
        refused.Body.ShouldContain("Repair");
        var remedy = (await RemedyRowAsync(R1)).ShouldNotBeNull();
        remedy.Remedy.ShouldBe("Repair");
        remedy.State.ShouldBe("InProgress");
        (await RemedyDecisionRowsAsync(R1)).Count.ShouldBe(decisionsBefore.Count);
    }

    // ---- R13: S1 end to end on its recordings ------------------------------------------------------

    [Fact]
    public async Task R13_S1_is_approved_by_the_system_as_in_001_and_then_gets_remedy_Repair_with_a_suggested_centre()
    {
        var scenario = GoldenScenario.Load("S1");
        var claimId = await ScenarioClaims.SubmitOnceAsync(scenario, SubmitAsClaimantAsync);
        var status = await fixture.WaitForClaimStatusAsync(
            claimId, s => s is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation), TimeSpan.FromSeconds(90));

        // The 001 outcome, unchanged (SC-009).
        status.ShouldBe(ClaimStatus.Approved);
        var claim = await ClaimRowAsync(claimId);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        (await ScalarAsync<string>(
                "select disposition from adjudication.adjudication_runs where claim_id = @value order by round desc limit 1", claimId))
            .ShouldBe(WireName.Of(Disposition.AutoApprove));

        // Then the remedy from AUR-WP's coveredDefect row and a repair awaiting hand-over at the suggested centre.
        var remedy = (await RemedyRowAsync(claimId)).ShouldNotBeNull();
        remedy.State.ShouldBe("InProgress");
        remedy.Remedy.ShouldBe("Repair");
        remedy.Scenario.ShouldBe("CoveredDefect");
        remedy.Policy.ShouldStartWith("AUR-WP v");
        var decisions = await RemedyDecisionRowsAsync(claimId);
        decisions.Select(d => $"{d.Kind}:{d.TableRow}").ShouldBe(["Evaluated:coveredDefect", "Decided:coveredDefect"]);
        decisions[1].DecidedBy.ShouldBe("Policy");

        var repair = (await RepairRowAsync(claimId)).ShouldNotBeNull();
        repair.Status.ShouldBe("AwaitingHandover");
        var handover = (await HandoversAsync(claimId)).ShouldHaveSingleItem();
        handover.Assignment.ShouldBe("Suggested");
        handover.CentreId.ShouldBe((await FirstEligibleCentreAsync(claimId)).Id);

        // No simulated repair request any more (research R8).
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(0);

        // The remedy steps follow the automatic approval in the trail.
        var steps = (await TrailAsync(claimId)).Select(e => e.Step).ToList();
        var approved = steps.IndexOf("AutoApproved");
        approved.ShouldBeGreaterThan(0);
        string[] remedySteps = ["RemedyRecommended", "RemedyDecided", "RepairCentreAssigned"];
        remedySteps.Select(step => steps.IndexOf(step)).ShouldAllBe(i => i > approved);
        remedySteps.Select(step => steps.IndexOf(step)).ShouldBeInOrder(SortDirection.Ascending);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static Guid ClaimOf(string scenario) => scenario switch
    {
        "R1" => R1,
        "R2" => R2,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    private static JsonObject Approval(string? remedy)
    {
        var approval = new JsonObject
        {
            ["decision"] = "Approve",
            ["claimantExplanation"] = "Your Aurora Tab 10 is covered by the warranty; we are arranging a free repair at an authorized service center.",
        };
        if (remedy is not null)
        {
            approval["remedy"] = remedy;
        }

        return approval;
    }

    private async Task ShouldBeApprovedByTheReviewerWithRemedyRepairAsync(Guid claimId)
    {
        var claim = await ClaimRowAsync(claimId);
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);

        var remedy = (await RemedyRowAsync(claimId)).ShouldNotBeNull();
        remedy.Remedy.ShouldBe("Repair");
        remedy.State.ShouldBe("InProgress");
        remedy.Scenario.ShouldBe("CoveredDefect");
        remedy.AllowedRemedies.ShouldBe(["Repair"]);
        (await RepairRowAsync(claimId)).ShouldNotBeNull().Status.ShouldBe("AwaitingHandover");
        (await CountAsync("select count(*) from integration.repair_requests where claim_id = @value", claimId)).ShouldBe(0);

        var detail = await DetailAsync(TestStaffUsers.ReviewerAurora, claimId);
        detail.Body.GetProperty("remedy").GetProperty("remedy").GetString().ShouldBe("Repair");
    }

    /// <summary>A synthetic Aurora claim (AUR-TAB10, North America) submitted by <c>agent.aurora</c> and escalated to review.</summary>
    private async Task<Guid> EscalatedAuroraClaimAsync()
    {
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);
        using var response = await client.PostAsync("/api/claims", Submission(NewSerial()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var claimId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("claimId").GetGuid();
        (await fixture.WaitForClaimStatusAsync(claimId, ClaimStatus.UnderReview)).ShouldBe(ClaimStatus.UnderReview);
        return claimId;
    }

    /// <summary>Submits S1 through the claimant channel as the 001 US1 tests do; the claim is shared through <see cref="ScenarioClaims"/>.</summary>
    private async Task<Guid> SubmitAsClaimantAsync(GoldenScenario scenario)
    {
        using var client = fixture.CreateClaimantClient(scenario.ChannelHost);
        using var response = await client.PostAsync("/api/public/claims", scenario.ToSubmission(), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        return await ScalarAsync<Guid>("select id from claims.claims where reference = @value", reference);
    }

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    // ---- API ---------------------------------------------------------------------------------------

    private async Task<DetailResponse> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return new DetailResponse(response.StatusCode, response.Headers.ETag?.Tag, await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private Task<PostResponse> ReviewDecisionAsync(TestStaffUser user, Guid claimId, string? ifMatch, JsonObject body)
        => PostAsync(user, $"/api/claims/{claimId}/review-decisions", ifMatch, body);

    private Task<PostResponse> RemedyDecisionAsync(TestStaffUser user, Guid claimId, string ifMatch, JsonObject body)
        => PostAsync(user, $"/api/claims/{claimId}/remedy-decisions", ifMatch, body);

    private async Task<PostResponse> PostAsync(TestStaffUser user, string path, string? ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await client.SendAsync(request, Ct);
        return new PostResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct));
    }

    // ---- Database reads (as the owner, across tenants, for assertions only) ------------------------

    private async Task<ClaimRow> ClaimRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select status, final_outcome, final_decided_by from claims.claims where id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"claim {claimId} not found");
        return new ClaimRow(
            WireName.Parse<ClaimStatus>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : WireName.Parse<FinalOutcome>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : WireName.Parse<DecidedBy>(reader.GetString(2)));
    }

    private async Task<RemedyRow?> RemedyRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.state, r.remedy, r.scenario, r.allowed_remedies, r.pending_reason, p.code || ' v' || v.version
            from claims.claim_remedies r
            join policy.policy_versions v on v.id = r.policy_version_id
            join policy.warranty_policies p on p.id = v.policy_id
            where r.claim_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            return null;
        }

        return new RemedyRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<string[]>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5));
    }

    private async Task<List<RemedyDecisionRow>> RemedyDecisionRowsAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select d.seq, d.kind, d.scenario, d.table_row, d.remedy, d.decided_by, d.actor, d.reason, p.code || ' v' || v.version
            from claims.remedy_decisions d
            join policy.policy_versions v on v.id = d.policy_version_id
            join policy.warranty_policies p on p.id = v.policy_id
            where d.claim_id = @id order by d.seq
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<RemedyDecisionRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new RemedyDecisionRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }

    private async Task<RepairRow?> RepairRowAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select status, failed_attempts from claims.repairs where claim_id = @id", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? new RepairRow(reader.GetString(0), reader.GetInt32(1)) : null;
    }

    /// <summary>The repair's hand-overs in the order they were recorded.</summary>
    private async Task<List<HandoverRow>> HandoversAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select h.service_center_id, h.assignment, h.is_active, h.handed_over_on
            from claims.repair_handovers h join claims.repairs r on r.id = h.repair_id
            where r.claim_id = @id order by h.recorded_at, h.id
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<HandoverRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HandoverRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3)));
        }

        return rows;
    }

    private async Task<List<string>> RepairEventTypesAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select type from audit.repair_events where claim_id = @id order by seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var types = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }

    /// <summary>
    /// The centre the suggestion rule (FR-018) picks for the claim: active, of the claim's tenant and region, able to
    /// repair the product's category, highest priority (lowest number) first, then by name.
    /// </summary>
    private async Task<CentreRow> FirstEligibleCentreAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select sc.id, sc.name
            from claims.claims c
            join catalog.products p on p.id = c.product_id
            join integration.service_centers sc on sc.tenant_id = c.tenant_id and sc.region = c.region
            where c.id = @id and sc.is_active and p.category = any(sc.capabilities)
            order by sc.priority, sc.name
            limit 1
            """, connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"no repair centre serves claim {claimId}");
        return new CentreRow(reader.GetGuid(0), reader.GetString(1));
    }

    private async Task<List<TrailRow>> TrailAsync(Guid claimId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select step, actor from audit.decision_trail_entries where claim_id = @id order by seq", connection);
        command.Parameters.AddWithValue("id", claimId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<TrailRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new TrailRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private Task<long> CountAsync(string sql, object value) => ScalarAsync<long>(sql, value);

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

    private sealed record DetailResponse(HttpStatusCode Status, string? ETag, JsonElement Body);

    private sealed record PostResponse(HttpStatusCode Status, string? MediaType, string Body)
    {
        /// <summary>A 400 ValidationProblemDetails naming <paramref name="member"/> among its errors.</summary>
        public void ShouldBeValidationProblem(string member)
        {
            Status.ShouldBe(HttpStatusCode.BadRequest, Body);
            MediaType.ShouldBe("application/problem+json");
            JsonSerializer.Deserialize<JsonElement>(Body).GetProperty("errors").EnumerateObject().Select(e => e.Name)
                .ShouldContain(name => name.Equals(member, StringComparison.OrdinalIgnoreCase), Body);
        }
    }

    private sealed record ClaimRow(ClaimStatus Status, FinalOutcome? FinalOutcome, DecidedBy? FinalDecidedBy);

    private sealed record RemedyRow(string State, string? Remedy, string Scenario, string[] AllowedRemedies, string? PendingReason, string Policy);

    private sealed record RemedyDecisionRow(
        int Seq, string Kind, string Scenario, string TableRow, string? Remedy, string? DecidedBy, string? Actor, string? Reason, string Policy);

    private sealed record RepairRow(string Status, int FailedAttempts);

    private sealed record HandoverRow(Guid CentreId, string Assignment, bool IsActive, DateOnly? HandedOverOn);

    private sealed record CentreRow(Guid Id, string Name);

    private sealed record TrailRow(string Step, string Actor);
}
