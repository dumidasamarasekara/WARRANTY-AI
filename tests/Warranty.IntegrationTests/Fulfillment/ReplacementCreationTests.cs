using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.Guardrails.Remedies;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Fulfillment;

/// <summary>
/// The replacement is created by the <c>ActionExecutor</c> in the transaction in which the remedy becomes Replacement
/// (003 research R3, T324): a Borealis reviewer choosing Replacement for a repair that is not economical
/// (<c>POST /api/claims/{claimId}/remedy-decisions</c>) gets one replacement with the rules of the claim's BOR-WP version
/// (same model, returned before reservation, 6 months), the first replacement location serving the claim and the
/// initial status <c>AwaitingReturn</c> (the customer still has the product: the claim has no repair, research R9).
/// The claim is a copy of an approved seeded Borealis claim, so the seeded claims stay untouched.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
[Trait("Feature", "003")]
public sealed class ReplacementCreationTests(WarrantyAppFixture fixture)
{
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;

    /// <summary>Seeded R1-borealis (BOR-SLATE11, Europe, claim value 480, BOR-WP v1): only its rows are copied.</summary>
    private static readonly Guid SourceClaim = Guid.Parse("22222222-0005-7000-8000-000000000001");

    private const string Reason = "The repair is not economical; the customer receives the same model instead.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reviewer_choosing_Replacement_after_an_uneconomical_estimate_creates_the_replacement_in_the_decision_transaction()
    {
        var claimId = await CopyApprovedClaimAsync();
        var versionId = await ScalarAsync<Guid>("select policy_version_id from claims.claim_remedies where claim_id = @value", SourceClaim);
        await ArrangeRemedyAwaitingDecisionAsync(claimId, versionId);
        var expectedLocation = await FirstReplacementLocationAsync();

        var reviewer = TestStaffUsers.ReviewerBorealis;
        var remedyETag = (await DetailAsync(reviewer, claimId)).GetProperty("remedy").GetProperty("etag").GetString().ShouldNotBeNull();
        var decided = await RemedyDecisionAsync(reviewer, claimId, remedyETag, new JsonObject { ["remedy"] = "Replacement", ["reason"] = Reason });

        decided.Status.ShouldBe(HttpStatusCode.Created, decided.Body);
        await using var connection = await OpenAsync();
        await using (var command = new NpgsqlCommand(
            """
            select r.id, r.status, r.product_rule, r.return_rule, r.minimum_cover_months, r.policy_version_id, r.return_state,
                   l.service_center_id, l.assignment
            from claims.replacements r left join claims.replacement_locations l on l.id = r.active_location_id
            where r.claim_id = @id
            """, connection))
        {
            command.Parameters.AddWithValue("id", claimId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue("the remedy decision creates the replacement");
            (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)).ShouldBe(
                ("AwaitingReturn", "SameModel", "BeforeReservation", 6));
            reader.GetGuid(5).ShouldBe(versionId);
            reader.GetString(6).ShouldBe("NotYetReturned");
            (reader.GetGuid(7), reader.GetString(8)).ShouldBe((expectedLocation, "Suggested"));
            (await reader.ReadAsync(Ct)).ShouldBeFalse("a claim has one replacement");
        }

        (await ListAsync("select type from audit.replacement_events where claim_id = @value order by seq", claimId))
            .ShouldBe(["LocationSuggested"]);
        (await ScalarAsync<string>("select remedy from claims.claim_remedies where claim_id = @value", claimId)).ShouldBe("Replacement");
        (await ScalarAsync<string>(
                "select actor from audit.decision_trail_entries where claim_id = @value and step = 'ReplacementLocationAssigned'", claimId))
            .ShouldBe(reviewer.Subject);
        (await ScalarAsync<long>("select count(*) from claims.repairs where claim_id = @value", claimId)).ShouldBe(0);
    }

    // ---- Arrangement ------------------------------------------------------------------------------

    /// <summary>
    /// The copied claim's remedy as after an uneconomical estimate: Repair set by policy, then returned to a reviewer for
    /// the repairNotEconomical row (Refund or Replacement) with the estimate of 300.00 above 60 % of 480.
    /// </summary>
    private async Task ArrangeRemedyAwaitingDecisionAsync(Guid claimId, Guid versionId)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        using (TenantContextScope.Begin(Borealis, "borealis"))
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var remedies = scope.ServiceProvider.GetRequiredService<IRemedyRepository>();
            var remedy = ClaimRemedy.SetByPolicy(
                Guid.CreateVersion7(), Borealis, claimId, versionId, RemedyScenario.CoveredDefect, RemedyType.Repair, [RemedyType.Repair], at);
            RemedyType[] allowed = [RemedyType.Refund, RemedyType.Replacement];
            remedy.ReturnToDecision(versionId, RemedyScenario.RepairNotEconomical, allowed, RemedyPendingReason.RepairNotEconomical, at);
            remedies.Add(remedy);
            remedies.AddDecision(RemedyDecision.Evaluated(
                Guid.CreateVersion7(), Borealis, claimId, 1, RemedyScenario.CoveredDefect, versionId, "coveredDefect", [RemedyType.Repair],
                0, null, at));
            remedies.AddDecision(RemedyDecision.Decided(
                Guid.CreateVersion7(), Borealis, claimId, 2, RemedyScenario.CoveredDefect, versionId, "coveredDefect", [RemedyType.Repair],
                RemedyType.Repair, RemedyDecidedBy.Policy, RemedyDecision.SystemActor, null, 0, null, at));
            remedies.AddDecision(RemedyDecision.Evaluated(
                Guid.CreateVersion7(), Borealis, claimId, 3, RemedyScenario.RepairNotEconomical, versionId,
                RemedyPolicyEvaluator.TableRowOf(RemedyScenario.RepairNotEconomical), allowed, 0, 300.00m, at));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }
    }

    /// <summary>A new approved claim: a copy of the seeded source claim with its own ID, reference and serial.</summary>
    private async Task<Guid> CopyApprovedClaimAsync()
    {
        var claimId = Guid.CreateVersion7();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            create temp table claim_copy on commit drop as select * from claims.claims where id = @source;
            update claim_copy set id = @id, reference = @reference, serial_number = @serial;
            insert into claims.claims select * from claim_copy;
            """,
            connection);
        command.Parameters.AddWithValue("source", SourceClaim);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("reference", ClaimReference.Generate());
        command.Parameters.AddWithValue("serial", $"REPLACE-{claimId:N}".ToUpperInvariant());
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        command.Transaction = transaction;
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        await transaction.CommitAsync(Ct);
        (await ScalarAsync<string>("select status from claims.claims where id = @value", claimId)).ShouldBe("Approved");
        return claimId;
    }

    /// <summary>The first active Borealis centre in Europe that issues replacements and serves tablets (priority, then name).</summary>
    private Task<Guid> FirstReplacementLocationAsync() => ScalarAsync<Guid>(
        """
        select id from integration.service_centers
        where tenant_id = @value and region = 'EU' and is_active and issues_replacements and 'tablet' = any(capabilities)
        order by priority, name, id limit 1
        """,
        Borealis);

    // ---- API and database ---------------------------------------------------------------------------

    private async Task<JsonElement> DetailAsync(TestStaffUser user, Guid claimId)
    {
        using var client = fixture.CreateStaffClient(user);
        using var response = await client.GetAsync($"/api/claims/{claimId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, user.Username);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<PostResponse> RemedyDecisionAsync(TestStaffUser user, Guid claimId, string ifMatch, JsonObject body)
    {
        using var client = fixture.CreateStaffClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/claims/{claimId}/remedy-decisions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await client.SendAsync(request, Ct);
        return new PostResponse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<T> ScalarAsync<T>(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is DBNull or null ? default! : (T)result;
    }

    private async Task<List<string>> ListAsync(string sql, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record PostResponse(HttpStatusCode Status, string Body);
}
