using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Api.Tenancy;
using Warranty.Application.Claims;
using Warranty.Application.Review;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Audit;

/// <summary>
/// Security events for tenant auditors (quickstart S21, FR-041a, research R30). Each test produces the
/// four S21 events of Aurora: a lookup of a Borealis claim ID and of an unknown claim ID by
/// <c>reviewer.aurora</c> (two <c>ACCESS_DENIED</c> that read alike), a failed claimant access on
/// <c>aurora.localhost</c> (<c>CLAIMANT_ACCESS_FAILED</c>) and a refused self-review by
/// <c>agent-reviewer.aurora</c> (<c>SELF_REVIEW_REFUSED</c>). <c>auditor.aurora</c> reads them through
/// <c>GET /api/security-events</c> newest first, without details, source IPs or operator-only kinds;
/// <c>auditor.borealis</c> sees none of them and non-auditors are refused.
/// <para>
/// The fixture's database is shared with every other test of the collection, so "exactly those four"
/// means: of the listed events, exactly four belong to the targets produced here. The self-review is
/// refused through <see cref="RecordReviewDecision"/> under the staff tenant context the API resolves
/// for <c>agent-reviewer.aurora</c> (the same principal, display name and tenant as a request), so the
/// listing tests do not depend on the review-decisions endpoint (T088).
/// </para>
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class SecurityEventsTests(WarrantyAppFixture fixture)
{
    private const string PendingEndpoint = "Pending T127";

    private const string AccessDenied = "ACCESS_DENIED";

    private const string CrossTenantAccessDenied = "CROSS_TENANT_ACCESS_DENIED";

    private const string ClaimantAccessFailed = "CLAIMANT_ACCESS_FAILED";

    private const string SelfReviewRefused = "SELF_REVIEW_REFUSED";

    /// <summary>Kinds stored without a tenant and never shown to a tenant (SecurityEventKind.IsOperatorOnly).</summary>
    private static readonly string[] OperatorOnlyKinds = [CrossTenantAccessDenied, "UNKNOWN_CHANNEL"];

    /// <summary>The members of a listed event (contracts/rest-api.openapi.yaml, <c>SecurityEvent</c>).</summary>
    private static readonly string[] EventMembers = ["id", "occurredAt", "kind", "actor", "target"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Stored events (no endpoint needed) -----------------------------------------------------

    [Fact]
    public async Task The_four_S21_events_are_stored_for_aurora_and_only_the_borealis_lookup_adds_an_operator_event()
    {
        var produced = await ProduceAsync();

        var rows = await StoredEventsAsync(produced.Targets);

        var tenantRows = rows.Where(r => r.TenantId is not null).ToList();
        tenantRows.Select(r => r.Kind).ShouldBe([AccessDenied, AccessDenied, ClaimantAccessFailed, SelfReviewRefused], ignoreOrder: true);
        tenantRows.ShouldAllBe(r => r.TenantId == TestStaffUsers.AuroraTenantId);

        var foreign = tenantRows.Single(r => r.Target == produced.BorealisClaimId.ToString());
        var unknown = tenantRows.Single(r => r.Target == produced.UnknownClaimId.ToString());
        foreach (var denied in new[] { foreign, unknown })
        {
            denied.Kind.ShouldBe(AccessDenied);
            denied.Actor.ShouldBe(TestStaffUsers.ReviewerAurora.Username);
            denied.Details.ShouldNotContain(TestStaffUsers.BorealisTenantId.ToString(), Case.Insensitive);
            denied.Details.ShouldNotContain("borealis", Case.Insensitive);
        }

        // Nothing stored for the tenant tells the Borealis ID from the unknown one.
        foreign.WithoutIdentity().ShouldBe(unknown.WithoutIdentity());

        var claimant = tenantRows.Single(r => r.Kind == ClaimantAccessFailed);
        claimant.Target.ShouldBe(produced.ClaimantReference);
        claimant.Actor.ShouldBe(ClaimantAccess.ClaimantChannelActor);

        var selfReview = tenantRows.Single(r => r.Kind == SelfReviewRefused);
        selfReview.Target.ShouldBe(produced.SelfSubmittedClaimId.ToString());
        selfReview.Actor.ShouldBe(TestStaffUsers.AgentReviewerAurora.Username);

        // Only the Borealis ID adds the operator-only companion, stored without a tenant.
        var operatorRow = rows.Where(r => r.TenantId is null).ShouldHaveSingleItem();
        operatorRow.Kind.ShouldBe(CrossTenantAccessDenied);
        operatorRow.Target.ShouldBe(produced.BorealisClaimId.ToString());
    }

    [Fact]
    public async Task Row_level_security_shows_the_four_events_to_aurora_only_and_the_operator_event_to_no_tenant()
    {
        var produced = await ProduceAsync();

        var asAurora = await VisibleEventsAsync(TestStaffUsers.AuroraTenantId, produced.Targets);
        var asBorealis = await VisibleEventsAsync(TestStaffUsers.BorealisTenantId, produced.Targets);

        asAurora.ShouldBe([AccessDenied, AccessDenied, ClaimantAccessFailed, SelfReviewRefused], ignoreOrder: true);
        asBorealis.ShouldBeEmpty();
    }

    // ---- GET /api/security-events ---------------------------------------------------------------

    [Fact(Skip = PendingEndpoint)]
    public async Task The_aurora_auditor_lists_exactly_the_four_events_newest_first_without_details_or_ips()
    {
        var produced = await ProduceAsync();
        using var auditor = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);

        var listed = await ListAllAsync(auditor);

        // The whole listing is newest first and holds no operator-only kind.
        listed.Select(e => e.OccurredAt).ShouldBeInOrder(SortDirection.Descending);
        listed.ShouldAllBe(e => !OperatorOnlyKinds.Contains(e.Kind));
        listed.Select(e => e.Id).ShouldBeUnique();

        var mine = listed.Where(e => produced.Targets.Contains(e.Target)).ToList();
        mine.Select(e => (e.Kind, e.Target)).ShouldBe(
        [
            (SelfReviewRefused, produced.SelfSubmittedClaimId.ToString()),
            (ClaimantAccessFailed, produced.ClaimantReference),
            (AccessDenied, produced.UnknownClaimId.ToString()),
            (AccessDenied, produced.BorealisClaimId.ToString()),
        ]);
        mine.Select(e => e.Actor).ShouldBe(
        [
            TestStaffUsers.AgentReviewerAurora.Username,
            ClaimantAccess.ClaimantChannelActor,
            TestStaffUsers.ReviewerAurora.Username,
            TestStaffUsers.ReviewerAurora.Username,
        ]);

        // The two denials are indistinguishable apart from their ID, time and the ID as supplied.
        Without(mine[2].Json, "id", "occurredAt", "target").ShouldBe(Without(mine[3].Json, "id", "occurredAt", "target"));

        foreach (var e in mine)
        {
            e.Json.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(EventMembers, e.Kind);
            e.Json.GetRawText().ShouldNotContain("borealis", Case.Insensitive, e.Kind);
            e.Json.GetRawText().ShouldNotContain(TestStaffUsers.BorealisTenantId.ToString(), Case.Insensitive, e.Kind);
        }

        // The stored details and source IP never reach the auditor.
        foreach (var stored in (await StoredEventsAsync(produced.Targets)).Where(r => r.TenantId is not null))
        {
            var listedEvent = mine.Single(e => e.Id == stored.Id);
            if (stored.Details.Length > 2)
            {
                listedEvent.Json.GetRawText().ShouldNotContain(stored.Details);
            }

            if (stored.SourceIp.Length > 0)
            {
                listedEvent.Json.GetRawText().ShouldNotContain(stored.SourceIp);
            }
        }
    }

    [Theory(Skip = PendingEndpoint)]
    [InlineData(AccessDenied)]
    [InlineData(ClaimantAccessFailed)]
    [InlineData(SelfReviewRefused)]
    public async Task The_kind_filter_lists_only_events_of_that_kind(string kind)
    {
        var produced = await ProduceAsync();
        using var auditor = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);

        var listed = await ListAllAsync(auditor, kind);

        listed.ShouldNotBeEmpty();
        listed.ShouldAllBe(e => e.Kind == kind);
        listed.Select(e => e.OccurredAt).ShouldBeInOrder(SortDirection.Descending);
        var expected = produced.KindsByTarget.Where(p => p.Value == kind).Select(p => p.Key).ToList();
        listed.Where(e => produced.Targets.Contains(e.Target)).Select(e => e.Target!).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact(Skip = PendingEndpoint)]
    public async Task An_operator_only_kind_filter_lists_nothing()
    {
        await ProduceAsync();
        using var auditor = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);

        using var response = await auditor.GetAsync($"/api/security-events?kind={CrossTenantAccessDenied}", Ct);

        // Not a kind of the contract: refused as invalid, or answered with no events — never with operator events.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").GetArrayLength().ShouldBe(0);
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact(Skip = PendingEndpoint)]
    public async Task Pages_split_the_newest_first_listing_without_gaps_or_overlap()
    {
        await ProduceAsync();
        using var auditor = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);

        var (firstFour, total) = await PageAsync(auditor, page: 1, pageSize: 4);
        var (page1, total1) = await PageAsync(auditor, page: 1, pageSize: 2);
        var (page2, total2) = await PageAsync(auditor, page: 2, pageSize: 2);

        total.ShouldBeGreaterThanOrEqualTo(4);
        total1.ShouldBe(total);
        total2.ShouldBe(total);
        firstFour.Count.ShouldBe(4);
        page1.Count.ShouldBe(2);
        page2.Count.ShouldBe(2);
        page1.Concat(page2).Select(e => e.Id).ShouldBe(firstFour.Select(e => e.Id));

        // The default page size is 50 and a page beyond the end is empty.
        using (var response = await auditor.GetAsync("/api/security-events", Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("page").GetInt32().ShouldBe(1);
            body.GetProperty("pageSize").GetInt32().ShouldBe(50);
        }

        var (beyond, _) = await PageAsync(auditor, page: (total / 100) + 2, pageSize: 100);
        beyond.ShouldBeEmpty();
    }

    [Fact(Skip = PendingEndpoint)]
    public async Task The_borealis_auditor_sees_none_of_the_aurora_events()
    {
        var produced = await ProduceAsync();
        using var aurora = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora);
        using var borealis = fixture.CreateStaffClient(TestStaffUsers.AuditorBorealis);

        var auroraIds = (await ListAllAsync(aurora)).Where(e => produced.Targets.Contains(e.Target)).Select(e => e.Id).ToList();
        var listed = await ListAllAsync(borealis);

        auroraIds.Count.ShouldBe(4);
        listed.Select(e => e.Id).ShouldNotContain(id => auroraIds.Contains(id));
        listed.ShouldNotContain(e => produced.Targets.Contains(e.Target));
        listed.ShouldAllBe(e => !OperatorOnlyKinds.Contains(e.Kind));
    }

    [Theory(Skip = PendingEndpoint)]
    [InlineData("agent.aurora")]
    [InlineData("reviewer.aurora")]
    [InlineData("agent-reviewer.aurora")]
    public async Task Staff_without_the_auditor_role_are_refused(string username)
    {
        using var client = fixture.CreateStaffClient(username);

        using var response = await client.GetAsync("/api/security-events", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    // ---- Producing the S21 events ---------------------------------------------------------------

    /// <summary>
    /// Writes the four S21 events in a fixed order: the Borealis-ID lookup, the unknown-ID lookup, the
    /// failed claimant access and the refused self-review (so newest first is the reverse).
    /// </summary>
    private async Task<ProducedEvents> ProduceAsync()
    {
        var borealisClaimId = await SubmitAsStaffAsync(TestStaffUsers.AgentBorealis, BorealisSubmission());
        var selfSubmittedClaimId = await SubmitAsStaffAsync(TestStaffUsers.AgentReviewerAurora, Submission(NewSerial()));
        var claimantReference = await SubmitAsClaimantAsync();
        var unknownClaimId = Guid.CreateVersion7();

        using (var reviewer = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora))
        {
            foreach (var claimId in new[] { borealisClaimId, unknownClaimId })
            {
                using var response = await reviewer.GetAsync($"/api/claims/{claimId}", Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
        }

        using (var channel = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost))
        {
            using var response = await channel.PostAsJsonAsync(
                "/api/public/claims/access", new { reference = claimantReference, contact = "someone.else@example.test" }, Ct);
            response.IsSuccessStatusCode.ShouldBeFalse();
        }

        (await RefuseSelfReviewAsync(selfSubmittedClaimId)).ShouldBeOfType<RecordReviewDecisionResult.SelfReviewRefused>();

        return new ProducedEvents(borealisClaimId, unknownClaimId, claimantReference, selfSubmittedClaimId);
    }

    /// <summary>
    /// <c>agent-reviewer.aurora</c> decides the claim they submitted, under the tenant context the API
    /// resolves for their token (subject, display name, tenant, roles).
    /// </summary>
    private async Task<RecordReviewDecisionResult> RefuseSelfReviewAsync(Guid claimId)
    {
        var user = TestStaffUsers.AgentReviewerAurora;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContext>()
            .Resolve(new ResolvedTenant(user.TenantId, "aurora"), user.Subject, user.Username, user.Roles, Guid.CreateVersion7().ToString("N"));

        return await scope.ServiceProvider.GetRequiredService<RecordReviewDecision>().ExecuteAsync(
            new RecordReviewDecisionCommand(
                claimId,
                "\"1\"",
                ReviewDecisionKind.Approve,
                "Self-review probe: the evidence supports a covered defect.",
                "Your claim is approved and a repair will be arranged for you.",
                null),
            Ct);
    }

    private static MultipartFormDataContent BorealisSubmission()
    {
        var serial = NewSerial();
        return Submission(
            serial,
            claim =>
            {
                claim["product"] = new JsonObject { ["modelCode"] = "BOR-SLATE11", ["serialNumber"] = serial };
                claim["purchase"]!["place"] = "Borealis Showroom";
            });
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

    // ---- Reading events -------------------------------------------------------------------------

    /// <summary>Every event the client can list (optionally of one kind), page by page.</summary>
    private static async Task<List<ListedEvent>> ListAllAsync(HttpClient client, string? kind = null)
    {
        var all = new List<ListedEvent>();
        for (var page = 1; ; page++)
        {
            var (items, total) = await PageAsync(client, page, 100, kind);
            all.AddRange(items);
            if (items.Count < 100 || all.Count >= total)
            {
                return all;
            }
        }
    }

    private static async Task<(List<ListedEvent> Items, int Total)> PageAsync(HttpClient client, int page, int pageSize, string? kind = null)
    {
        var query = $"/api/security-events?page={page}&pageSize={pageSize}" + (kind is null ? string.Empty : $"&kind={kind}");
        using var response = await client.GetAsync(query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, query);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("page").GetInt32().ShouldBe(page, query);
        body.GetProperty("pageSize").GetInt32().ShouldBe(pageSize, query);
        var items = body.GetProperty("items").EnumerateArray().Select(ListedEvent.From).ToList();
        items.Count.ShouldBeLessThanOrEqualTo(pageSize, query);
        return (items, body.GetProperty("total").GetInt32());
    }

    /// <summary>The stored rows (operator events included) with the given targets, read as the database owner.</summary>
    private async Task<List<StoredEvent>> StoredEventsAsync(IReadOnlyCollection<string> targets)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select id, tenant_id, kind, actor, target, coalesce(details::text, ''), coalesce(source_ip::text, '') " +
            "from audit.security_events where target = any(@targets)",
            connection);
        command.Parameters.AddWithValue("targets", targets.ToArray());
        var rows = new List<StoredEvent>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new StoredEvent(
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

    /// <summary>The kinds of the events with the given targets that the API's login sees for <paramref name="tenant"/>.</summary>
    private async Task<List<string>> VisibleEventsAsync(Guid tenant, IReadOnlyCollection<string> targets)
    {
        // No pooling: the session variable must not leak to another test.
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString()) { Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using (var set = new NpgsqlCommand("select set_config('app.tenant_id', @tenant, false)", connection))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand("select kind from audit.security_events where target = any(@targets)", connection);
        command.Parameters.AddWithValue("targets", targets.ToArray());
        var kinds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            kinds.Add(reader.GetString(0));
        }

        return kinds;
    }

    /// <summary>The object's members except <paramref name="names"/>, as sorted <c>name=value</c> pairs.</summary>
    private static string Without(JsonElement element, params string[] names)
        => string.Join(
            "|",
            element.EnumerateObject().Where(p => !names.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value.GetRawText()}"));

    /// <summary>The targets of the four S21 events (each unique to one <see cref="ProduceAsync"/> call).</summary>
    private sealed record ProducedEvents(Guid BorealisClaimId, Guid UnknownClaimId, string ClaimantReference, Guid SelfSubmittedClaimId)
    {
        public IReadOnlyDictionary<string, string> KindsByTarget => new Dictionary<string, string>
        {
            [BorealisClaimId.ToString()] = AccessDenied,
            [UnknownClaimId.ToString()] = AccessDenied,
            [ClaimantReference] = ClaimantAccessFailed,
            [SelfSubmittedClaimId.ToString()] = SelfReviewRefused,
        };

        public IReadOnlyCollection<string> Targets => KindsByTarget.Keys.ToList();
    }

    private sealed record StoredEvent(Guid Id, Guid? TenantId, string Kind, string? Actor, string? Target, string Details, string SourceIp)
    {
        /// <summary>Everything but the row ID and the ID as supplied.</summary>
        public string WithoutIdentity() => $"{TenantId}|{Kind}|{Actor}|{Details}|{SourceIp}";
    }

    private sealed record ListedEvent(Guid Id, DateTimeOffset OccurredAt, string Kind, string? Actor, string? Target, JsonElement Json)
    {
        public static ListedEvent From(JsonElement json)
            => new(
                json.GetProperty("id").GetGuid(),
                json.GetProperty("occurredAt").GetDateTimeOffset(),
                json.GetProperty("kind").GetString()!,
                json.TryGetProperty("actor", out var actor) ? actor.GetString() : null,
                json.TryGetProperty("target", out var target) ? target.GetString() : null,
                json.Clone());
    }
}

