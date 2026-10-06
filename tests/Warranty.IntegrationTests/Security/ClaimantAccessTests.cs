using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Security;

/// <summary>
/// S15 (FR-037a, research R10/R21) and the public submission limit, on a dedicated API host with the
/// production rate limits (the shared host relaxes them): right reference + wrong email gets the same
/// generic 401 as an unknown reference and a <c>CLAIMANT_ACCESS_FAILED</c> event; the 6th access
/// attempt for a reference within 15 minutes is a 429 with <c>Retry-After</c>, and the 11th submission
/// within an hour likewise. The test server gives every request the same (unknown) client IP.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class ClaimantAccessTests(WarrantyAppFixture fixture) : IAsyncDisposable
{
    private readonly WarrantyApiFactory _limited = fixture.CreateFactory(new Dictionary<string, string?>
    {
        // The shared host's worker adjudicates the claims; this host only serves the public endpoints.
        ["ClaimJobWorker:Enabled"] = "false",
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _limited.DisposeAsync();

    [Fact]
    public async Task S15_a_wrong_email_gets_the_generic_401_and_the_sixth_attempt_within_15_minutes_is_429()
    {
        var reference = await SubmitAsync();
        var unknown = ClaimReference.Generate();
        using var client = LimitedClient();

        using var wrongEmail = await AccessAsync(client, reference, "someone.else@example.test");
        using var unknownReference = await AccessAsync(client, unknown, ContactEmail);

        wrongEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownReference.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemAsync(wrongEmail)).ShouldBe(await ProblemAsync(unknownReference));
        (await AccessFailuresAsync(reference)).ShouldBe(1);

        // Attempts 2–5 on the same reference (any spelling of it) are still answered.
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            using var response = await AccessAsync(client, attempt % 2 == 0 ? reference.ToLowerInvariant() : reference, $"guess{attempt}@example.test");
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // The 6th is rejected before the contact is checked, even with the right email.
        using var sixth = await AccessAsync(client, reference, ContactEmail);

        sixth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        sixth.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var retryAfter = sixth.Headers.RetryAfter?.Delta;
        retryAfter.ShouldNotBeNull();
        retryAfter.Value.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15));
        (await sixth.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetInt32().ShouldBe(429);
        (await AccessFailuresAsync(reference)).ShouldBe(5);

        // The limit is per reference: the same client can still open another claim.
        var other = await SubmitAsync();
        using var otherClaim = await AccessAsync(client, other, ContactEmail);
        otherClaim.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_eleventh_claim_submission_within_an_hour_is_429()
    {
        using var client = LimitedClient();

        // Every request that reaches the route counts, whatever its outcome; these lack the claim part.
        for (var submission = 1; submission <= 10; submission++)
        {
            using var form = new MultipartFormDataContent { { new StringContent("none"), "note" } };
            using var response = await client.PostAsync("/api/public/claims", form, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var eleventh = await client.PostAsync("/api/public/claims", Submission(NewSerial()), Ct);

        eleventh.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        eleventh.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var retryAfter = eleventh.Headers.RetryAfter?.Delta;
        retryAfter.ShouldNotBeNull();
        retryAfter.Value.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
    }

    private HttpClient LimitedClient()
        => _limited.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{WarrantyAppFixture.AuroraHost}"),
            AllowAutoRedirect = false,
        });

    /// <summary>Submits a fresh synthetic Aurora claim through the shared host, so it uses none of the limited host's permits.</summary>
    private async Task<string> SubmitAsync()
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        using var response = await client.PostAsync("/api/public/claims", Submission(NewSerial()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
    }

    private static Task<HttpResponseMessage> AccessAsync(HttpClient client, string reference, string contact)
        => client.PostAsJsonAsync("/api/public/claims/access", new { reference, contact }, Ct);

    /// <summary>The problem without its per-request correlation ID.</summary>
    private static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return string.Join(
            "|",
            problem.EnumerateObject().Where(p => p.Name is not ("correlationId" or "traceId")).OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    private async Task<long> AccessFailuresAsync(string reference)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select count(*) from audit.security_events
            where kind = 'CLAIMANT_ACCESS_FAILED' and upper(target) = @target and tenant_id = @tenant
            """,
            connection);
        command.Parameters.AddWithValue("target", reference);
        command.Parameters.AddWithValue("tenant", TestStaffUsers.AuroraTenantId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
