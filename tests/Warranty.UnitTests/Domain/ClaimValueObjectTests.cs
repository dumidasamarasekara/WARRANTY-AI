using Warranty.Domain.Claims;

namespace Warranty.UnitTests.Domain;

/// <summary>Claim reference, evidence and job rules. Claim state transitions are covered by ClaimStateMachineTests (T044).</summary>
public sealed class ClaimValueObjectTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Generated_references_are_valid_crockford_base32_and_vary()
    {
        var references = Enumerable.Range(0, 200).Select(_ => ClaimReference.Generate()).ToList();

        references.ShouldAllBe(r => ClaimReference.IsValid(r));
        references.Distinct().Count().ShouldBe(200);
        references.ShouldAllBe(r => !r.Any(c => "ILOU".Contains(c, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(" 7k2m-9xq4ab ", "7K2M9XQ4AB")]
    [InlineData("oi1lx0x0x0", "0111X0X0X0")]
    public void User_typed_references_are_normalized(string typed, string expected)
        => ClaimReference.Normalize(typed).ShouldBe(expected);

    [Theory]
    [InlineData("7K2M9XQ4A")]
    [InlineData("7K2M9XQ4ABC")]
    [InlineData("7K2M9XQ4AU")]
    [InlineData(null)]
    public void Invalid_references_are_rejected(string? reference)
        => ClaimReference.IsValid(reference).ShouldBeFalse();

    [Fact]
    public void Evidence_builds_a_tenant_relative_blob_path_from_ids_not_the_file_name()
    {
        var claimId = Guid.CreateVersion7();
        var evidenceId = Guid.CreateVersion7();

        var evidence = ClaimEvidence.Create(
            evidenceId, Guid.CreateVersion7(), claimId, 2, EvidenceKind.Photo, "../../etc/passwd.jpg", "image/jpeg", 1024, Sha, DateTimeOffset.UnixEpoch);

        evidence.BlobPath.ShouldBe($"claims/{claimId}/2/{evidenceId}.jpg");
        evidence.FileName.ShouldBe("passwd.jpg");
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Invoice 2026 (final).pdf", "Invoice_2026__final_.pdf")]
    [InlineData("..", "file")]
    [InlineData(null, "file")]
    public void File_names_are_sanitized(string? input, string expected)
        => ClaimEvidence.SanitizeFileName(input).ShouldBe(expected);

    [Theory]
    [InlineData("image/heic", 1024L)]
    [InlineData("text/html", 1024L)]
    [InlineData("image/jpeg", 0L)]
    [InlineData("image/jpeg", 15L * 1024 * 1024 + 1)]
    public void Evidence_rejects_unsupported_types_and_sizes(string contentType, long size)
        => Should.Throw<ArgumentException>(() => ClaimEvidence.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, EvidenceKind.Photo, "p.jpg", contentType, size, Sha, DateTimeOffset.UnixEpoch));

    [Fact]
    public void Evidence_accepts_exactly_15_MB()
        => ClaimEvidence.Create(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, EvidenceKind.Invoice, "i.pdf", "application/pdf",
                ClaimEvidence.MaxSizeBytes, Sha, DateTimeOffset.UnixEpoch)
            .SizeBytes.ShouldBe(ClaimEvidence.MaxSizeBytes);

    // Attempts are incremented by the dequeue function in the database (T019); the permanent-failure
    // path after MaxAttempts is covered by the job queue integration tests.
    [Fact]
    public void Job_backoff_doubles_per_attempt()
    {
        ClaimJob.BackoffFor(1).ShouldBe(TimeSpan.FromSeconds(5));
        ClaimJob.BackoffFor(2).ShouldBe(TimeSpan.FromSeconds(10));
        ClaimJob.BackoffFor(3).ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Infrastructure_failure_requeues_with_backoff_and_completion_marks_done()
    {
        var now = DateTimeOffset.UnixEpoch;
        var job = ClaimJob.Enqueue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "corr-1", now);

        job.RecordInfrastructureFailure(now);

        job.Status.ShouldBe(ClaimJobStatus.Queued);
        job.AvailableAt.ShouldBe(now + TimeSpan.FromSeconds(5));

        job.Complete();
        job.Status.ShouldBe(ClaimJobStatus.Done);
    }
}
