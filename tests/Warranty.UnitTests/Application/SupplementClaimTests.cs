using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.UnitTests.Application;

/// <summary>Supplementing a <c>PendingInformation</c> claim: new round, evidence, job and <c>SupplementReceived</c> in one transaction (T100).</summary>
public sealed class SupplementClaimTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("0199b000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Submitted = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Submitted.AddDays(1);

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%synthetic invoice\n"u8.ToArray();
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] HeicBytes = [0x00, 0x00, 0x00, 0x18, .. "ftypheic"u8, 0x00, 0x00, 0x00, 0x00, .. "mif1heic"u8];
    private static readonly byte[] TextBytes = "just some text, not an image"u8.ToArray();

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IUploadSanitizer _sanitizer = Substitute.For<IUploadSanitizer>();
    private readonly IJobQueue _jobs = Substitute.For<IJobQueue>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<string> _events = [];
    private readonly List<string> _uploadedPaths = [];
    private TenantContextScope _tenant = TenantContextScope.Begin(TenantId, "aurora", "claimant", "corr-2");
    private ClaimJob? _job;
    private object? _trailPayload;
    private string? _trailActor;

    public SupplementClaimTests()
    {
        // By default the sanitizer passes the content through, typed by its magic bytes.
        _sanitizer.SanitizeAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            if (ci.ArgAt<Stream?>(0) is not { } content)
            {
                return Task.FromResult<UploadSanitizerResult>(null!); // a test is configuring a more specific call
            }

            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var bytes = copy.ToArray();
            var type = EvidenceFileSignature.Detect(bytes);
            return Task.FromResult<UploadSanitizerResult>(new UploadSanitizerResult.Sanitized(type, EvidenceFileSignature.ContentTypeOf(type)!, bytes));
        });
        _documents.UploadEvidenceAsync(default!, default!, default!, default).ReturnsForAnyArgs(ci =>
        {
            _events.Add("upload");
            _uploadedPaths.Add(ci.ArgAt<string>(0));
            return new StoredDocument(ci.ArgAt<string>(0), new string('b', 64), ci.ArgAt<Stream>(1).Length);
        });
        _unitOfWork.ExecuteInTransactionAsync(default!, default).ReturnsForAnyArgs(async ci =>
        {
            _events.Add("begin");
            await ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None);
            _events.Add("commit");
        });
        _unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(_ =>
        {
            _events.Add("save");
            return Task.CompletedTask;
        });
        _jobs.EnqueueAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            _job = ci.Arg<ClaimJob>();
            return Task.CompletedTask;
        });
        _trail.AppendAsync(default, default, default!, default!, default, default).ReturnsForAnyArgs(ci =>
        {
            _events.Add($"trail:{ci.ArgAt<TrailStep>(1)}");
            _trailActor = ci.ArgAt<string>(2);
            _trailPayload = ci.ArgAt<object?>(4);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _tenant.Dispose();

    [Fact]
    public async Task A_claimant_supplement_starts_the_next_round_with_its_evidence_job_and_trail_in_one_transaction()
    {
        var claim = PendingClaim();

        var result = await Supplement(Command(claim, invoices: [File("invoice.pdf", PdfBytes)], photos: [File("label.jpg", JpegBytes)]));

        var accepted = result.ShouldBeOfType<SupplementClaimResult.Accepted>();
        accepted.ClaimId.ShouldBe(claim.Id);
        accepted.Reference.ShouldBe(claim.Reference);
        accepted.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        accepted.Round.ShouldBe(2);
        claim.CurrentRound.ShouldBe(2);
        claim.RequestedItems.ShouldBeEmpty();

        _claims.Received(2).AddEvidence(Arg.Is<ClaimEvidence>(e => e.Round == 2 && e.ClaimId == claim.Id && e.TenantId == TenantId));
        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.Kind == EvidenceKind.Invoice && e.ContentType == "application/pdf"));
        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.Kind == EvidenceKind.Photo && e.ContentType == "image/jpeg"));
        _uploadedPaths.ShouldAllBe(p => p.StartsWith($"claims/{claim.Id}/2/", StringComparison.Ordinal));

        var job = _job.ShouldNotBeNull();
        job.ClaimId.ShouldBe(claim.Id);
        job.TenantId.ShouldBe(TenantId);
        job.Round.ShouldBe(2);
        job.CorrelationId.ShouldBe("corr-2");
        job.Status.ShouldBe(ClaimJobStatus.Queued);

        _events.ShouldBe(["upload", "upload", "begin", "save", "trail:SupplementReceived", "commit"]);
        _trailActor.ShouldBe(Claim.ClaimantSubmitter);
        var payload = Payload();
        payload.GetProperty("round").GetInt32().ShouldBe(2);
        payload.GetProperty("channel").GetString().ShouldBe("ClaimantPortal");
        payload.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetString()).ShouldBe([RequestedItemCodes.Invoice]);
        payload.GetProperty("evidence").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_claims_agent_supplement_is_recorded_as_the_staff_subject_and_keeps_the_note()
    {
        _tenant.Dispose();
        _tenant = TenantContextScope.Begin(TenantId, "aurora", "staff-sub-1", "corr-3", [Principals.ClaimsAgentRole]);
        var claim = PendingClaim();

        var result = await Supplement(Command(claim, ClaimChannel.AgentPortal, reference: null, note: "  Customer confirmed the purchase date by phone.  "));

        result.ShouldBeOfType<SupplementClaimResult.Accepted>().Round.ShouldBe(2);
        _claims.DidNotReceive().AddEvidence(Arg.Any<ClaimEvidence>());
        _trailActor.ShouldBe("staff-sub-1");
        Payload().GetProperty("note").GetString().ShouldBe("Customer confirmed the purchase date by phone.");
        _job.ShouldNotBeNull().Round.ShouldBe(2);
    }

    [Fact]
    public async Task A_supplement_after_a_reviewer_request_starts_a_new_round_and_keeps_the_reviewer_mark()
    {
        var claim = PendingClaim(reviewer: true);

        (await Supplement(Command(claim, photos: [File("label.png", PngBytes)]))).ShouldBeOfType<SupplementClaimResult.Accepted>();

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        claim.ReviewerInfoRequested.ShouldBeTrue();
        Payload().GetProperty("reviewerInfoRequested").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData(ClaimStatus.Approved)]
    [InlineData(ClaimStatus.UnderReview)]
    [InlineData(ClaimStatus.UnderEvaluation)]
    [InlineData(ClaimStatus.Submitted)]
    public async Task A_claim_that_is_not_pending_information_is_a_conflict_and_nothing_is_stored(ClaimStatus status)
    {
        var claim = ClaimIn(status);

        var result = await Supplement(Command(claim, photos: [File("label.jpg", JpegBytes)]));

        result.ShouldBeOfType<SupplementClaimResult.Conflict>().Detail.ShouldBe(SupplementClaim.NotPendingDetail);
        claim.Status.ShouldBe(status);
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task An_unknown_claim_or_one_of_another_tenant_is_not_found()
    {
        (await Supplement(new SupplementClaimCommand(ClaimChannel.AgentPortal, Guid.CreateVersion7(), null, "note", [], [])))
            .ShouldBeOfType<SupplementClaimResult.NotFound>();

        var foreign = PendingClaim(OtherTenantId);
        (await Supplement(Command(foreign, ClaimChannel.AgentPortal, reference: null, note: "note")))
            .ShouldBeOfType<SupplementClaimResult.NotFound>();
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task A_reference_that_names_another_claim_than_the_claimant_token_is_not_found()
    {
        var claim = PendingClaim();
        var other = ClaimReference.Generate();

        (await Supplement(Command(claim, reference: other, note: "note"))).ShouldBeOfType<SupplementClaimResult.NotFound>();
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task The_reference_is_matched_after_normalization()
    {
        var claim = PendingClaim();

        (await Supplement(Command(claim, reference: claim.Reference.ToLowerInvariant(), note: "note")))
            .ShouldBeOfType<SupplementClaimResult.Accepted>();
    }

    [Fact]
    public async Task An_empty_supplement_is_a_validation_problem()
    {
        var claim = PendingClaim();

        var invalid = (await Supplement(Command(claim, note: "   "))).ShouldBeOfType<SupplementClaimResult.Invalid>();

        invalid.Errors.Keys.ShouldBe([SupplementClaim.SupplementKey]);
        claim.Status.ShouldBe(ClaimStatus.PendingInformation);
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task Too_many_files_and_a_long_note_are_field_errors()
    {
        var claim = PendingClaim();

        var invalid = (await Supplement(Command(
                claim,
                invoices: [File("a.pdf", PdfBytes), File("b.pdf", PdfBytes)],
                photos: Enumerable.Range(1, 9).Select(i => File($"p{i}.jpg", JpegBytes)).ToList(),
                note: new string('n', SupplementClaim.MaxNoteLength + 1))))
            .ShouldBeOfType<SupplementClaimResult.Invalid>();

        invalid.Errors.Keys.ShouldBe(["invoice", "photos", "note"], ignoreOrder: true);
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task Files_are_validated_by_content_as_in_submit_claim()
    {
        var claim = PendingClaim();

        var invalid = (await Supplement(Command(
                claim,
                invoices: [File("invoice.pdf", TextBytes)],
                photos: [File("empty.jpg", []), File("huge.jpg", JpegBytes, ClaimEvidence.MaxSizeBytes + 1), File("scan.pdf", PdfBytes)])))
            .ShouldBeOfType<SupplementClaimResult.Invalid>();

        invalid.Errors["invoice"].ShouldHaveSingleItem().ShouldContain("invoice.pdf");
        invalid.Errors["photos"].Length.ShouldBe(3);
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task A_png_named_jpg_is_stored_as_png()
    {
        var claim = PendingClaim();

        (await Supplement(Command(claim, photos: [File("label.jpg", PngBytes)]))).ShouldBeOfType<SupplementClaimResult.Accepted>();

        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.ContentType == "image/png" && e.BlobPath.EndsWith(".png", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_heic_photo_is_unsupported_media()
    {
        var claim = PendingClaim();

        var result = await Supplement(Command(claim, photos: [File("IMG_0001.jpg", HeicBytes)]));

        result.ShouldBeOfType<SupplementClaimResult.UnsupportedMediaType>().Detail.ShouldBe(SubmitClaim.HeicMessage);
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task A_concurrent_change_on_save_is_a_conflict()
    {
        var claim = PendingClaim();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ConcurrencyConflictException("changed"));

        var result = await Supplement(Command(claim, note: "note"));

        result.ShouldBeOfType<SupplementClaimResult.Conflict>().Detail.ShouldBe(SupplementClaim.ConcurrentChangeDetail);
    }

    [Fact]
    public async Task Supplement_files_go_through_the_upload_sanitizer_and_a_rejected_file_stores_nothing()
    {
        var claim = PendingClaim();
        _sanitizer.SanitizeAsync(Arg.Is<Stream>(s => s.Length == PdfBytes.Length), Arg.Any<CancellationToken>())
            .Returns(new UploadSanitizerResult.Rejected(EvidenceFileType.Pdf, "the PDF is password-protected or encrypted."));

        var result = await Supplement(Command(claim, invoices: [File("invoice.pdf", PdfBytes)], photos: [File("label.jpg", JpegBytes)]));

        var invalid = result.ShouldBeOfType<SupplementClaimResult.Invalid>();
        invalid.Errors.Keys.ShouldBe(["invoice"]);
        invalid.Errors["invoice"].ShouldHaveSingleItem().ShouldBe("invoice.pdf can't be used: the PDF is password-protected or encrypted.");
        await _sanitizer.Received(2).SanitizeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await NothingWasStoredAsync();
    }

    [Fact]
    public async Task Only_the_sanitized_bytes_of_a_supplement_are_stored()
    {
        var claim = PendingClaim();
        byte[] clean = [0xFF, 0xD8, 0xFF, 0xDB, 0x01];
        _sanitizer.SanitizeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new UploadSanitizerResult.Sanitized(EvidenceFileType.Jpeg, "image/jpeg", clean));

        (await Supplement(Command(claim, photos: [File("label.jpg", JpegBytes)]))).ShouldBeOfType<SupplementClaimResult.Accepted>();

        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.Kind == EvidenceKind.Photo && e.SizeBytes == clean.Length && e.ContentType == "image/jpeg"));
    }

    private Task<SupplementClaimResult> Supplement(SupplementClaimCommand command)
        => new SupplementClaim(_tenant, _claims, _documents, _sanitizer, _jobs, _trail, _unitOfWork, _time)
            .ExecuteAsync(command, TestContext.Current.CancellationToken);

    private JsonElement Payload() => JsonSerializer.SerializeToElement(_trailPayload.ShouldNotBeNull());

    private async Task NothingWasStoredAsync()
    {
        _claims.DidNotReceive().AddEvidence(Arg.Any<ClaimEvidence>());
        await _documents.DidNotReceiveWithAnyArgs().UploadEvidenceAsync(default!, default!, default!, default);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default);
        await _trail.DidNotReceiveWithAnyArgs().AppendAsync(default, default, default!, default!, default, default);
    }

    private static SupplementClaimCommand Command(
        Claim claim,
        ClaimChannel channel = ClaimChannel.ClaimantPortal,
        IReadOnlyList<EvidenceUpload>? invoices = null,
        IReadOnlyList<EvidenceUpload>? photos = null,
        string? note = null,
        string? reference = "")
        => new(channel, claim.Id, reference == string.Empty ? claim.Reference : reference, note, invoices ?? [], photos ?? []);

    private static EvidenceUpload File(string name, byte[] content, long? length = null)
        => new(name, length ?? content.Length, () => new MemoryStream(content, writable: false));

    private Claim PendingClaim(Guid? tenantId = null, bool reviewer = false)
    {
        var claim = NewClaim(tenantId ?? TenantId);
        claim.StartEvaluation(Submitted);
        if (reviewer)
        {
            claim.EscalateToReview(Submitted);
            claim.RequestInformation([RequestedItem.Create(RequestedItemCodes.PhotoOfSerialLabel, "Please send a photo of the serial label.")], DecidedBy.Reviewer, Submitted);
        }
        else
        {
            claim.RequestInformation([RequestedItem.Create(RequestedItemCodes.Invoice, "Please send the invoice.")], DecidedBy.System, Submitted);
        }

        Register(claim);
        return claim;
    }

    private Claim ClaimIn(ClaimStatus status)
    {
        var claim = NewClaim(TenantId);
        if (status != ClaimStatus.Submitted)
        {
            claim.StartEvaluation(Submitted);
        }

        if (status == ClaimStatus.UnderReview)
        {
            claim.EscalateToReview(Submitted);
        }
        else if (status == ClaimStatus.Approved)
        {
            claim.FinalizeApproved("Your claim is approved.", DecidedBy.System, Submitted);
        }

        claim.Status.ShouldBe(status);
        Register(claim);
        return claim;
    }

    private void Register(Claim claim)
        => _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);

    private static Claim NewClaim(Guid tenantId) => Claim.Submit(
        Guid.CreateVersion7(), tenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, Guid.CreateVersion7(),
        "x@example.com", null, "AUR-TAB10", null, "AT10-99-0001", new DateOnly(2026, 6, 1), "Store", 450m, Region.NA,
        "The tablet stopped turning on and does not charge any more.", Submitted);
}
