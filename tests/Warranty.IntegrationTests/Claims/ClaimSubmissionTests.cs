using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Storage.Blobs;
using Npgsql;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;
using static Warranty.IntegrationTests.Claims.SyntheticClaims;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// <c>POST /api/public/claims</c> and <c>POST /api/claims</c> (T057): multipart submission, tenant from
/// the Host or the staff token only, claim + evidence + job + trail rows, and the error responses. Every
/// test uses its own serial and freshly generated evidence bytes, so no claim here raises a
/// duplicate-serial or evidence-reuse signal in another test's adjudication.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class ClaimSubmissionTests(WarrantyAppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_claimant_submission_is_accepted_and_stores_the_claim_its_evidence_its_job_and_the_first_trail_entries()
    {
        var serial = NewSerial();
        var invoice = Pdf();
        var photos = new[] { Jpeg(), Png() };
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var response = await client.PostAsync("/api/public/claims", Submission(serial, invoices: [invoice], photos: photos), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        accepted.TryGetProperty("claimId", out _).ShouldBeFalse("the claimant channel never returns internal IDs");
        var reference = accepted.GetProperty("reference").GetString()!;
        ClaimReference.IsValid(reference).ShouldBeTrue();
        accepted.GetProperty("status").GetString().ShouldBe("Submitted");
        accepted.GetProperty("round").GetInt32().ShouldBe(1);
        response.Headers.Location!.ToString().ShouldBe($"/api/public/claims/{reference}");

        var claim = await ClaimBySerialAsync(serial);
        claim.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId);
        claim.Reference.ShouldBe(reference);
        claim.Channel.ShouldBe(ClaimChannel.ClaimantPortal);
        claim.SubmittedBy.ShouldBe("claimant");
        claim.ProductId.ShouldNotBeNull("AUR-TAB10 is in Aurora's catalog");
        claim.Region.ShouldBe("NA");

        var evidence = await EvidenceAsync(claim.Id);
        evidence.Count.ShouldBe(3);
        evidence.ShouldAllBe(e => e.Round == 1 && e.BlobPath.StartsWith($"claims/{claim.Id}/1/", StringComparison.Ordinal));
        evidence.Single(e => e.Kind == "Invoice").ShouldSatisfyAllConditions(
            e => e.ContentType.ShouldBe("application/pdf"),
            e => e.Sha256.ShouldBe(Sha256(invoice.Bytes)));
        evidence.Where(e => e.Kind == "Photo").Select(e => e.ContentType).ShouldBe(["image/jpeg", "image/png"], ignoreOrder: true);

        // Photos are stored re-encoded without metadata (T110): the hash is the one of the sanitized bytes.
        var sanitizedHashes = new List<string>();
        foreach (var photo in photos)
        {
            sanitizedHashes.Add(Sha256(await StoredBytesAsync(photo, Ct)));
        }

        evidence.Where(e => e.Kind == "Photo").Select(e => e.Sha256).ShouldBe(sanitizedHashes, ignoreOrder: true);

        // The files are in the tenant's own container, and each stored file is exactly what was hashed.
        var container = new BlobServiceClient(fixture.BlobConnectionString).GetBlobContainerClient("tenant-aurora");
        foreach (var item in evidence)
        {
            var blob = await container.GetBlobClient(item.BlobPath).DownloadContentAsync(Ct);
            Sha256(blob.Value.Content.ToArray()).ShouldBe(item.Sha256, item.BlobPath);
        }

        var job = await SingleRowAsync(
            "select tenant_id, round, correlation_id from claims.claim_jobs where claim_id = @id", claim.Id,
            r => (TenantId: r.GetGuid(0), Round: r.GetInt32(1), CorrelationId: r.GetString(2)));
        job.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId);
        job.Round.ShouldBe(1);
        job.CorrelationId.ShouldBe(response.Headers.GetValues("X-Correlation-Id").Single());

        var trail = await TrailAsync(claim.Id);
        trail.Take(3).Select(t => t.Step).ShouldBe([TrailStep.ClaimSubmitted, TrailStep.TenantResolved, TrailStep.EvidenceStored]);
        trail.Take(3).Select(t => t.Seq).ShouldBe([1, 2, 3]);
        trail[0].Actor.ShouldBe("claimant");
        trail.Take(3).ShouldAllBe(t => t.TenantId == TestStaffUsers.AuroraTenantId);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_host_and_tenant_fields_in_the_body_are_ignored()
    {
        var serial = NewSerial();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.BorealisHost);

        using var response = await client.PostAsync(
            "/api/public/claims",
            Submission(serial, claim =>
            {
                claim["tenantId"] = TestStaffUsers.AuroraTenantId.ToString();
                claim["tenant"] = "aurora";
            }),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var claim = await ClaimBySerialAsync(serial);
        claim.TenantId.ShouldBe(TestStaffUsers.BorealisTenantId);
        (await TrailAsync(claim.Id)).ShouldAllBe(t => t.TenantId == TestStaffUsers.BorealisTenantId);
    }

    [Fact]
    public async Task A_submission_on_an_unknown_channel_host_is_not_found_and_creates_nothing()
    {
        var serial = NewSerial();
        using var client = fixture.CreateClaimantClient("unknown.localhost");

        using var response = await client.PostAsync("/api/public/claims", Submission(serial), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ClaimCountAsync(serial)).ShouldBe(0);
    }

    [Fact]
    public async Task A_claims_agent_submits_through_the_staff_api_and_gets_the_claim_id()
    {
        var serial = NewSerial();
        using var client = fixture.CreateStaffClient(TestStaffUsers.AgentAurora);

        using var response = await client.PostAsync("/api/claims", Submission(serial), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var claimId = accepted.GetProperty("claimId").GetGuid();
        accepted.GetProperty("round").GetInt32().ShouldBe(1);
        accepted.GetProperty("status").GetString().ShouldBe("Submitted");
        response.Headers.Location!.ToString().ShouldBe($"/api/claims/{claimId}");

        var claim = await ClaimBySerialAsync(serial);
        claim.Id.ShouldBe(claimId);
        claim.Reference.ShouldBe(accepted.GetProperty("reference").GetString());
        claim.TenantId.ShouldBe(TestStaffUsers.AuroraTenantId);
        claim.Channel.ShouldBe(ClaimChannel.AgentPortal);
        claim.SubmittedBy.ShouldBe(TestStaffUsers.AgentAurora.Subject);
        var trail = await TrailAsync(claimId);
        trail[0].Step.ShouldBe(TrailStep.ClaimSubmitted);
        trail[0].Actor.ShouldBe(TestStaffUsers.AgentAurora.Subject);
    }

    [Fact]
    public async Task Only_claims_agents_may_submit_through_the_staff_api()
    {
        var serial = NewSerial();

        using (var reviewer = fixture.CreateStaffClient(TestStaffUsers.ReviewerAurora))
        using (var response = await reviewer.PostAsync("/api/claims", Submission(serial), Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var auditor = fixture.CreateStaffClient(TestStaffUsers.AuditorAurora))
        using (var response = await auditor.PostAsync("/api/claims", Submission(serial), Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var anonymous = fixture.CreateClient(WarrantyAppFixture.StaffHost))
        using (var response = await anonymous.PostAsync("/api/claims", Submission(serial), Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await ClaimCountAsync(serial)).ShouldBe(0);
    }

    [Fact]
    public async Task A_heic_photo_is_unsupported_media_asking_for_jpeg_or_png()
    {
        var serial = NewSerial();
        var heic = new EvidenceFile("IMG_0001.jpg", [0x00, 0x00, 0x00, 0x18, .. "ftypheic"u8, .. RandomNumberGenerator.GetBytes(64)], "image/jpeg");
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var response = await client.PostAsync("/api/public/claims", Submission(serial, photos: [heic]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("detail").GetString()!.ShouldContain("JPEG or PNG");
        (await ClaimCountAsync(serial)).ShouldBe(0);
    }

    [Fact]
    public async Task Missing_and_unsupported_files_are_validation_problems_keyed_by_part()
    {
        var serial = NewSerial();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var noFiles = await client.PostAsync("/api/public/claims", Submission(serial, invoices: [], photos: []), Ct);
        using var gif = await client.PostAsync(
            "/api/public/claims",
            Submission(serial, invoices: [new EvidenceFile("invoice.pdf", [.. "GIF89a"u8, .. RandomNumberGenerator.GetBytes(64)], "application/pdf")]),
            Ct);

        noFiles.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorKeysAsync(noFiles)).ShouldBe(["invoice", "photos"], ignoreOrder: true);
        gif.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorKeysAsync(gif)).ShouldBe(["invoice"]);
        (await ClaimCountAsync(serial)).ShouldBe(0);
    }

    [Fact]
    public async Task A_pdf_with_javascript_or_an_unreadable_photo_is_rejected_by_upload_sanitizing_and_creates_nothing()
    {
        var serial = NewSerial();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);
        var scripted = new EvidenceFile(
            "invoice.pdf",
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /OpenAction << /S /JavaScript /JS (app.alert\\(1\\)) >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n"u8.ToArray(),
            "application/pdf");
        var broken = new EvidenceFile("photo-1.jpg", [0xFF, 0xD8, 0xFF, 0xE0, .. RandomNumberGenerator.GetBytes(256)], "image/jpeg");

        using var response = await client.PostAsync("/api/public/claims", Submission(serial, invoices: [scripted], photos: [broken]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorKeysAsync(response)).ShouldBe(["invoice", "photos"], ignoreOrder: true);
        (await ClaimCountAsync(serial)).ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_claim_fields_are_reported_by_their_dotted_camel_case_paths()
    {
        var serial = NewSerial();
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var response = await client.PostAsync(
            "/api/public/claims",
            Submission(serial, claim =>
            {
                claim["customer"]!["email"] = "not-an-email";
                claim["purchase"]!["currency"] = "dollars";
                claim["problemDescription"] = "short";
            }),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ErrorKeysAsync(response)).ShouldBe(["customer.email", "purchase.currency", "problemDescription"], ignoreOrder: true);
        (await ClaimCountAsync(serial)).ShouldBe(0);
    }

    [Fact]
    public async Task A_request_that_is_not_multipart_is_unsupported_media()
    {
        using var client = fixture.CreateClaimantClient(WarrantyAppFixture.AuroraHost);

        using var response = await client.PostAsJsonAsync("/api/public/claims", new { problemDescription = "not a form" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<IReadOnlyList<string>> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ToList();
    }

    private async Task<ClaimRow> ClaimBySerialAsync(string serial)
        => await SingleRowAsync(
            "select id, tenant_id, reference, channel, submitted_by, product_id, region from claims.claims where serial_number = @id",
            serial,
            r => new ClaimRow(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), WireName.Parse<ClaimChannel>(r.GetString(3)), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetString(6)));

    private async Task<long> ClaimCountAsync(string serial)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from claims.claims where serial_number = @serial", connection);
        command.Parameters.AddWithValue("serial", serial);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<EvidenceRow>> EvidenceAsync(Guid claimId)
        => await RowsAsync(
            "select kind, content_type, sha256, blob_path, round from claims.claim_evidence where claim_id = @id", claimId,
            r => new EvidenceRow(r.GetString(0), r.GetString(1), r.GetString(2).Trim(), r.GetString(3), r.GetInt32(4)));

    private async Task<List<TrailRow>> TrailAsync(Guid claimId)
        => await RowsAsync(
            "select seq, step, actor, tenant_id from audit.decision_trail_entries where claim_id = @id order by seq", claimId,
            r => new TrailRow(r.GetInt32(0), WireName.Parse<TrailStep>(r.GetString(1)), r.GetString(2), r.GetGuid(3)));

    private async Task<T> SingleRowAsync<T>(string sql, object id, Func<NpgsqlDataReader, T> map)
        => (await RowsAsync(sql, id, map)).ShouldHaveSingleItem();

    /// <summary>Reads as the database owner (no row-level security), for assertions only.</summary>
    private async Task<List<T>> RowsAsync<T>(string sql, object id, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed record ClaimRow(Guid Id, Guid TenantId, string Reference, ClaimChannel Channel, string SubmittedBy, Guid? ProductId, string? Region);

    private sealed record EvidenceRow(string Kind, string ContentType, string Sha256, string BlobPath, int Round);

    private sealed record TrailRow(int Seq, TrailStep Step, string Actor, Guid TenantId);
}
