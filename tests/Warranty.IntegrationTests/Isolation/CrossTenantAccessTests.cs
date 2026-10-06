using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Isolation;

/// <summary>
/// Cross-tenant access through the API (S12, FR-041a, research R30): every Aurora staff user calls
/// every staff endpoint that takes a claim or evidence ID with Borealis IDs and with random unknown
/// IDs. Both get the same 404 ProblemDetails without Borealis data and the same tenant-visible
/// <c>ACCESS_DENIED</c> event; only the Borealis IDs also write an operator-only
/// <c>CROSS_TENANT_ACCESS_DENIED</c> (<c>tenant_id = NULL</c>, read with the owner connection). A
/// claimant token never crosses channels, and a tool call cannot reach another claim's evidence.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class CrossTenantAccessTests(WarrantyAppFixture fixture)
{
    private const string AccessDenied = "ACCESS_DENIED";

    private const string CrossTenantAccessDenied = "CROSS_TENANT_ACCESS_DENIED";

    private const string BorealisProblem = "The coffee grinder burr stopped turning after a power cut in the Borealis showroom.";

    /// <summary>Problem members that legitimately differ per request.</summary>
    private static readonly string[] VolatileProblemMembers = ["traceId", "correlationId", "instance"];

    private static readonly TestStaffUser[] AuroraStaff =
        [TestStaffUsers.AgentAurora, TestStaffUsers.ReviewerAurora, TestStaffUsers.AuditorAurora, TestStaffUsers.AgentReviewerAurora];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Staff endpoints: identical 404s --------------------------------------------------------

    [Theory]
    [InlineData("claim-detail")]
    [InlineData("evidence-content")]
    [InlineData("evidence-content-of-own-claim")]
    [InlineData("trace")]
    [InlineData("review-decision")]
    [InlineData("supplement")]
    public async Task Borealis_ids_and_unknown_ids_get_the_same_404_problem_without_borealis_data(string endpointName)
    {
        var endpoint = StaffEndpoint.Named(endpointName);
        var (aurora, borealis) = await SubmitClaimsAsync();

        foreach (var user in endpoint.Users)
        {
            using var client = fixture.CreateStaffClient(user);
            var foreignIds = endpoint.Ids(aurora, borealis.Ids);
            var unknownIds = endpoint.Ids(aurora, ClaimIds.Random());

            using var foreign = await SendAsync(client, endpoint, foreignIds);
            using var unknown = await SendAsync(client, endpoint, unknownIds);

            foreach (var response in new[] { foreign, unknown })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{endpoint.Name} as {user.Username}");
                response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", $"{endpoint.Name} as {user.Username}");
                var body = await response.Content.ReadAsStringAsync(Ct);
                foreach (var leak in borealis.Identifying)
                {
                    body.ShouldNotContain(leak, Case.Insensitive, $"{endpoint.Name} as {user.Username}");
                }
            }

            (await ProblemAsync(foreign)).ShouldBe(await ProblemAsync(unknown), $"{endpoint.Name} as {user.Username}");
        }
    }

    // ---- Staff endpoints: security events -------------------------------------------------------

    [Theory]
    [InlineData("claim-detail")]
    [InlineData("evidence-content")]
    [InlineData("evidence-content-of-own-claim")]
    [InlineData("trace")]
    [InlineData("review-decision")]
    [InlineData("supplement")]
    public async Task Each_denied_lookup_writes_one_identical_access_denied_event_and_borealis_ids_also_an_operator_event(string endpointName)
    {
        var endpoint = StaffEndpoint.Named(endpointName);
        var (aurora, borealis) = await SubmitClaimsAsync();

        foreach (var user in endpoint.Users)
        {
            using var client = fixture.CreateStaffClient(user);
            var foreignIds = endpoint.Ids(aurora, borealis.Ids);
            var unknownIds = endpoint.Ids(aurora, ClaimIds.Random());

            var foreignEvents = await EventsWrittenByAsync(() => SendAsync(client, endpoint, foreignIds));
            var unknownEvents = await EventsWrittenByAsync(() => SendAsync(client, endpoint, unknownIds));
            var context = $"{endpoint.Name} as {user.Username}";

            var foreignDenied = foreignEvents.Where(e => e.Kind == AccessDenied).ShouldHaveSingleItem(context);
            var unknownDenied = unknownEvents.Where(e => e.Kind == AccessDenied).ShouldHaveSingleItem(context);
            foreach (var (denied, ids) in new[] { (foreignDenied, foreignIds), (unknownDenied, unknownIds) })
            {
                denied.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId, context);
                ids.Supplied.ShouldContain(denied.Target, context);
                denied.Details.ShouldNotContain(TestStaffUsers.BorealisTenantId.ToString(), Case.Insensitive, context);
                denied.Details.ShouldNotContain("borealis", Case.Insensitive, context);
            }

            // Nothing in the tenant-visible event tells a Borealis ID from an unknown one.
            foreignDenied.Normalized(foreignIds).ShouldBe(unknownDenied.Normalized(unknownIds), context);

            var crossTenant = foreignEvents.Where(e => e.Kind == CrossTenantAccessDenied).ShouldHaveSingleItem(context);
            crossTenant.TenantId.ShouldBeNull(context);
            crossTenant.Details.ShouldContain(TestStaffUsers.AuroraTenantId.ToString(), Case.Insensitive, context);
            crossTenant.Details.ShouldContain(TestStaffUsers.BorealisTenantId.ToString(), Case.Insensitive, context);
            unknownEvents.ShouldNotContain(e => e.Kind == CrossTenantAccessDenied, context);
        }
    }

    // ---- Lists ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_claim_list_of_every_aurora_staff_user_holds_only_aurora_claims()
    {
        var (aurora, borealis) = await SubmitClaimsAsync();

        foreach (var user in AuroraStaff)
        {
            using var client = fixture.CreateStaffClient(user);
            var listed = new List<Guid>();
            var bodies = new StringBuilder();
            for (var page = 1; ; page++)
            {
                using var response = await client.GetAsync($"/api/claims?page={page}&pageSize=100", Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
                var body = await response.Content.ReadAsStringAsync(Ct);
                bodies.Append(body);
                var items = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("claimId").GetGuid()).ToList();
                listed.AddRange(items);
                if (items.Count < 100)
                {
                    break;
                }
            }

            listed.ShouldContain(aurora.ClaimId, user.Username);
            listed.ShouldNotContain(borealis.Ids.ClaimId, user.Username);
            (await TenantsOfAsync(listed)).ShouldBe([TestStaffUsers.AuroraTenantId], user.Username);
            foreach (var leak in borealis.Identifying)
            {
                bodies.ToString().ShouldNotContain(leak, Case.Insensitive, user.Username);
            }
        }
    }

    [Fact]
    public async Task The_review_queue_of_aurora_reviewers_never_shows_a_borealis_claim()
    {
        var (_, borealis) = await SubmitClaimsAsync();

        // No recording matches a synthetic claim, so its adjudication fails and it goes to human review.
        (await fixture.WaitForClaimStatusAsync(borealis.Ids.ClaimId, ClaimStatus.UnderReview)).ShouldBe(ClaimStatus.UnderReview);
        using (var borealisReviewer = fixture.CreateStaffClient(TestStaffUsers.ReviewerBorealis))
        {
            (await QueueAsync(borealisReviewer)).ShouldContain(borealis.Ids.ClaimId);
        }

        foreach (var user in new[] { TestStaffUsers.ReviewerAurora, TestStaffUsers.AgentReviewerAurora })
        {
            using var client = fixture.CreateStaffClient(user);
            using var response = await client.GetAsync("/api/review-queue", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
            var body = await response.Content.ReadAsStringAsync(Ct);
            foreach (var leak in borealis.Identifying)
            {
                body.ShouldNotContain(leak, Case.Insensitive, user.Username);
            }

            var queued = JsonDocument.Parse(body).RootElement.EnumerateArray().Select(item => item.GetProperty("claimId").GetGuid()).ToList();
            queued.ShouldNotContain(borealis.Ids.ClaimId, user.Username);
            (await TenantsOfAsync(queued)).ShouldBeSubsetOf([TestStaffUsers.AuroraTenantId], user.Username);
        }
    }

    // ---- Claimant channel -----------------------------------------------------------------------

    [Fact]
    public async Task A_claimant_token_for_an_aurora_claim_opens_nothing_on_the_borealis_channel()
    {
        var reference = await SubmitAsClaimantAsync();
        var claimId = await ClaimIdOfAsync(reference);
        var token = await ClaimantTokenAsync(reference);

        using var borealis = fixture.CreateClaimantClient(WarrantyAppFixture.BorealisHost, token);
        using var issued = await borealis.GetAsync($"/api/public/claims/{reference}", Ct);

        // A token naming Borealis for the Aurora claim (as if forged with the signing key) still finds nothing.
        using var relabelled = fixture.CreateClaimantClient(
            WarrantyAppFixture.BorealisHost, fixture.IssueClaimantToken(TestStaffUsers.BorealisTenantId, claimId));
        using var forged = await relabelled.GetAsync($"/api/public/claims/{reference}", Ct);

        foreach (var response in new[] { issued, forged })
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
            var body = await response.Content.ReadAsStringAsync(Ct);
            body.ShouldNotContain("productName");
            body.ShouldNotContain("Aurora Tab 10", Case.Insensitive);
        }

        // The same token still works on its own channel.
        using var aurora = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost, token);
        using var own = await aurora.GetAsync($"/api/public/claims/{reference}", Ct);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Tools ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_tool_call_with_a_borealis_evidence_reference_is_rejected()
    {
        var (aurora, borealis) = await SubmitClaimsAsync();
        var auroraInvoice = await EvidenceIdsAsync(aurora.ClaimId, "Invoice");
        var borealisInvoice = (await EvidenceIdsAsync(borealis.Ids.ClaimId, "Invoice")).Single();

        // The Borealis run issued EV-1 … EV-4 for its invoice and three photos; the Aurora run only EV-1 and EV-2.
        var borealisRun = new ReferenceRegistry();
        foreach (var evidenceId in await EvidenceIdsAsync(borealis.Ids.ClaimId))
        {
            borealisRun.IssueEvidence(evidenceId);
        }

        borealisRun.Entries.Count.ShouldBe(4);

        using var tenant = TenantContextScope.Begin(TestStaffUsers.AuroraTenantId, "aurora");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var @case = await scope.ServiceProvider.GetRequiredService<ICaseKnowledgeProvider>().GetCaseContextAsync(aurora.ClaimId, 1, Ct);
        var tools = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        var auroraRun = new ReferenceRegistry();
        auroraRun.IssueEvidence(auroraInvoice.Single());
        foreach (var photo in await EvidenceIdsAsync(aurora.ClaimId, "Photo"))
        {
            auroraRun.IssueEvidence(photo);
        }

        // A reference issued only by the Borealis run is not a reference of this run.
        var notIssued = await tools.InvokeAsync(
            AgentNames.Evidence, new AdjudicationContext(Guid.CreateVersion7(), tenant, @case, auroraRun), InvoiceCall("EV-4"), Ct);
        notIssued.IsError.ShouldBeTrue();
        notIssued.Result.GetRawText().ShouldContain("EV-4 was not issued for this run");

        // A tampered reference map pointing EV-1 at the Borealis invoice resolves to nothing visible to the claim.
        var tampered = ReferenceRegistry.FromMap(new Dictionary<string, Guid> { ["EV-1"] = borealisInvoice });
        var crossTenant = await tools.InvokeAsync(
            AgentNames.Evidence, new AdjudicationContext(Guid.CreateVersion7(), tenant, @case, tampered), InvoiceCall("EV-1"), Ct);
        crossTenant.IsError.ShouldBeTrue();
        crossTenant.Result.GetRawText().ShouldContain("EV-1 is not an invoice of this claim");
        foreach (var leak in borealis.Identifying)
        {
            crossTenant.Result.GetRawText().ShouldNotContain(leak, Case.Insensitive);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static AiToolCall InvoiceCall(string invoiceRef)
        => new(
            "call-1",
            ToolNames.InvoiceValidation,
            JsonSerializer.SerializeToElement(new
            {
                invoiceRef,
                extracted = new { invoiceDate = "UNKNOWN", modelCode = "UNKNOWN", serial = "UNKNOWN", amount = 0, seller = "UNKNOWN" },
            }));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, StaffEndpoint endpoint, ClaimIds ids)
    {
        using var request = new HttpRequestMessage(endpoint.Method, endpoint.Path(ids)) { Content = endpoint.Body() };
        if (endpoint.NeedsIfMatch)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The problem without its per-request members.</summary>
    private static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return string.Join(
            "|",
            problem.EnumerateObject().Where(p => !VolatileProblemMembers.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    /// <summary>An Aurora claim (staff channel) and a Borealis claim with an invoice and three photos.</summary>
    private async Task<(AuroraClaim Aurora, BorealisClaim Borealis)> SubmitClaimsAsync()
    {
        var auroraId = await SubmitAsStaffAsync(TestStaffUsers.AgentAurora, Submission(NewSerial()));

        var borealisSerial = NewSerial();
        var borealisId = await SubmitAsStaffAsync(
            TestStaffUsers.AgentBorealis,
            Submission(
                borealisSerial,
                claim =>
                {
                    claim["product"] = new JsonObject { ["modelCode"] = "BOR-SLATE11", ["serialNumber"] = borealisSerial };
                    claim["purchase"]!["place"] = "Borealis Showroom";
                    claim["problemDescription"] = BorealisProblem;
                },
                photos: [Jpeg(), Png(), Jpeg()]));
        var reference = await ScalarAsync<string>("select reference from claims.claims where id = @id", borealisId);
        var invoice = (await EvidenceIdsAsync(borealisId, "Invoice")).Single();

        return (new AuroraClaim(auroraId), new BorealisClaim(new ClaimIds(borealisId, invoice), [reference, borealisSerial, BorealisProblem, "Borealis"]));
    }

    private async Task<Guid> SubmitAsStaffAsync(TestStaffUser agent, MultipartFormDataContent submission)
    {
        using var client = fixture.CreateStaffClient(agent);
        using var response = await client.PostAsync("/api/claims", submission, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("claimId").GetGuid();
    }

    private async Task<string> SubmitAsClaimantAsync()
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsync("/api/public/claims", Submission(NewSerial()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
    }

    private async Task<string> ClaimantTokenAsync(string reference)
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact = ContactEmail }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    private async Task<List<Guid>> QueueAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/review-queue", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Select(item => item.GetProperty("claimId").GetGuid()).ToList();
    }

    /// <summary>The access-denial events written while <paramref name="send"/> ran, read as the owner (operator events included).</summary>
    private async Task<List<SecurityEventRow>> EventsWrittenByAsync(Func<Task<HttpResponseMessage>> send)
    {
        var before = (await DenialEventsAsync()).Select(e => e.Id).ToHashSet();
        using (await send())
        {
        }

        return (await DenialEventsAsync()).Where(e => !before.Contains(e.Id)).ToList();
    }

    private async Task<List<SecurityEventRow>> DenialEventsAsync()
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select id, tenant_id, kind, actor, target, coalesce(details::text, ''), coalesce(source_ip::text, '') " +
            "from audit.security_events where kind = any(@kinds)",
            connection);
        command.Parameters.AddWithValue("kinds", new[] { AccessDenied, CrossTenantAccessDenied });
        var rows = new List<SecurityEventRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new SecurityEventRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return rows;
    }

    private async Task<List<Guid>> EvidenceIdsAsync(Guid claimId, string? kind = null)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select id from claims.claim_evidence where claim_id = @id and (@kind::text is null or kind = @kind) order by uploaded_at, id",
            connection);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)kind ?? DBNull.Value });
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<List<Guid>> TenantsOfAsync(IReadOnlyCollection<Guid> claimIds)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select distinct tenant_id from claims.claims where id = any(@ids)", connection);
        command.Parameters.AddWithValue("ids", claimIds.ToArray());
        var tenants = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            tenants.Add(reader.GetGuid(0));
        }

        return tenants;
    }

    private Task<Guid> ClaimIdOfAsync(string reference) => ScalarAsync<Guid>("select id from claims.claims where reference = @id", reference);

    private async Task<T> ScalarAsync<T>(string sql, object id)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record AuroraClaim(Guid ClaimId);

    /// <summary>The Borealis claim's IDs and the values that must never appear in an Aurora response.</summary>
    private sealed record BorealisClaim(ClaimIds Ids, IReadOnlyList<string> Identifying);

    /// <summary>A claim ID and an evidence ID as put into a request path.</summary>
    private sealed record ClaimIds(Guid ClaimId, Guid EvidenceId)
    {
        public IReadOnlyList<string> Supplied => [ClaimId.ToString(), EvidenceId.ToString()];

        public static ClaimIds Random() => new(Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    private sealed record SecurityEventRow(Guid Id, Guid? TenantId, string Kind, string? Actor, string? Target, string Details, string SourceIp)
    {
        /// <summary>The event with the supplied IDs replaced by placeholders, for comparing a Borealis-ID event with an unknown-ID one.</summary>
        public string Normalized(ClaimIds ids)
        {
            string Mask(string? value) => (value ?? string.Empty)
                .Replace(ids.ClaimId.ToString(), "{claimId}", StringComparison.OrdinalIgnoreCase)
                .Replace(ids.EvidenceId.ToString(), "{evidenceId}", StringComparison.OrdinalIgnoreCase);

            return $"{TenantId}|{Kind}|{Actor}|{Mask(Target)}|{Mask(Details)}|{SourceIp}";
        }
    }

    /// <summary>A staff endpoint that looks up a claim or evidence ID, with the Aurora users whose role allows it.</summary>
    private sealed record StaffEndpoint(
        string Name,
        HttpMethod Method,
        Func<ClaimIds, string> Path,
        Func<HttpContent?> Body,
        IReadOnlyList<TestStaffUser> Users,
        bool NeedsIfMatch = false,
        bool OwnClaim = false)
    {
        private static readonly TestStaffUser[] Reviewers = [TestStaffUsers.ReviewerAurora, TestStaffUsers.AgentReviewerAurora];

        private static readonly TestStaffUser[] Agents = [TestStaffUsers.AgentAurora, TestStaffUsers.AgentReviewerAurora];

        private static readonly StaffEndpoint[] All =
        [
            new("claim-detail", HttpMethod.Get, ids => $"/api/claims/{ids.ClaimId}", () => null, AuroraStaff),
            new(
                "evidence-content",
                HttpMethod.Get,
                ids => $"/api/claims/{ids.ClaimId}/evidence/{ids.EvidenceId}/content",
                () => null,
                AuroraStaff),

            // An Aurora claim with an evidence ID of Borealis (or an unknown one).
            new(
                "evidence-content-of-own-claim",
                HttpMethod.Get,
                ids => $"/api/claims/{ids.ClaimId}/evidence/{ids.EvidenceId}/content",
                () => null,
                AuroraStaff,
                OwnClaim: true),
            new(
                "trace",
                HttpMethod.Get,
                ids => $"/api/claims/{ids.ClaimId}/trace",
                () => null,
                [TestStaffUsers.ReviewerAurora, TestStaffUsers.AuditorAurora, TestStaffUsers.AgentReviewerAurora]),
            new(
                "review-decision",
                HttpMethod.Post,
                ids => $"/api/claims/{ids.ClaimId}/review-decisions",
                () => JsonContent.Create(new
                {
                    decision = "Approve",
                    justification = "Cross-tenant probe: valid justification text.",
                    claimantExplanation = "Your claim is approved and a repair will be arranged.",
                }),
                Reviewers,
                NeedsIfMatch: true),
            new(
                "supplement",
                HttpMethod.Post,
                ids => $"/api/claims/{ids.ClaimId}/supplements",
                () =>
                {
                    var form = new MultipartFormDataContent { { new StringContent("Cross-tenant probe supplement."), "note" } };
                    var photo = Jpeg();
                    form.Add(photo.Content(), "photos", photo.Name);
                    return form;
                },
                Agents),
        ];

        public static StaffEndpoint Named(string name) => All.Single(endpoint => endpoint.Name == name);

        /// <summary>The IDs for the request path: <paramref name="probe"/>, or the Aurora claim with the probe's evidence ID.</summary>
        public ClaimIds Ids(AuroraClaim aurora, ClaimIds probe) => OwnClaim ? probe with { ClaimId = aurora.ClaimId } : probe;
    }
}
