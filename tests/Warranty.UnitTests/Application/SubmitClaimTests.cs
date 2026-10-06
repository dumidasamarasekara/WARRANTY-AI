using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;

namespace Warranty.UnitTests.Application;

/// <summary>Claim submission: validation, file types by content, reference, region, one transaction with job and trail (T056).</summary>
public sealed class SubmitClaimTests : IDisposable
{
    private const string ModelCode = "AUR-TAB10";
    private const string Serial = "AT10-99-0001";

    private static readonly Guid TenantId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] WebPBytes = "RIFF\x24\x00\x00\x00WEBPVP8 "u8.ToArray();
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%synthetic invoice\n"u8.ToArray();
    private static readonly byte[] HeicBytes = [0x00, 0x00, 0x00, 0x18, .. "ftypheic"u8, 0x00, 0x00, 0x00, 0x00, .. "mif1heic"u8];
    private static readonly byte[] TextBytes = "just some text, not an image"u8.ToArray();

    private readonly ICrmClient _crm = Substitute.For<ICrmClient>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IJobQueue _jobs = Substitute.For<IJobQueue>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<string> _events = [];
    private TenantContextScope _tenant = TenantContextScope.Begin(TenantId, "aurora", "claimant", "corr-1");
    private Claim? _added;
    private ClaimJob? _job;

    public SubmitClaimTests()
    {
        _crm.FindOrCreateCustomerAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            var d = ci.Arg<CustomerDetails>();
            return Customer.Create(Guid.CreateVersion7(), TenantId, d.FullName, d.Email, d.Country, d.Phone, d.AddressLine, d.City, d.PostalCode);
        });
        var product = Product.Create(ProductId, TenantId, ModelCode, "Aurora Tab 10", "tablet", 349m, "USD");
        _catalog.FindProductByModelAsync(ModelCode, Arg.Any<CancellationToken>()).Returns(product);
        _catalog.FindSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(TenantId, Serial, ProductId));
        _documents.UploadEvidenceAsync(default!, default!, default!, default).ReturnsForAnyArgs(ci =>
        {
            _events.Add("upload");
            var stream = ci.ArgAt<Stream>(1);
            var length = 0L;
            var buffer = new byte[4096];
            int n;
            while ((n = stream.Read(buffer)) > 0)
            {
                length += n;
            }

            return new StoredDocument(ci.ArgAt<string>(0), new string('a', 64), length);
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
        _claims.When(c => c.Add(Arg.Any<Claim>())).Do(ci => _added = ci.Arg<Claim>());
        _jobs.EnqueueAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            _job = ci.Arg<ClaimJob>();
            return Task.CompletedTask;
        });
        _trail.AppendAsync(default, default, default!, default!, default, default).ReturnsForAnyArgs(ci =>
        {
            _events.Add($"trail:{ci.ArgAt<TrailStep>(1)}");
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _tenant.Dispose();

    [Fact]
    public async Task A_valid_claimant_submission_stores_claim_evidence_job_and_trail_in_one_transaction()
    {
        var result = await Submit(Command(ValidData()));

        var accepted = result.ShouldBeOfType<SubmitClaimResult.Accepted>();
        accepted.Status.ShouldBe(ClaimStatus.Submitted);
        accepted.Round.ShouldBe(1);
        ClaimReference.IsValid(accepted.Reference).ShouldBeTrue();

        var claim = _added.ShouldNotBeNull();
        claim.Id.ShouldBe(accepted.ClaimId);
        claim.TenantId.ShouldBe(TenantId);
        claim.Channel.ShouldBe(ClaimChannel.ClaimantPortal);
        claim.SubmittedBy.ShouldBe(Claim.ClaimantSubmitter);
        claim.ContactEmail.ShouldBe("sample.customer@aurora.example.com");
        claim.ContactPhone.ShouldBe("+15550100001");
        claim.ProductId.ShouldBe(ProductId);
        claim.SerialNumber.ShouldBe(Serial);
        claim.PurchaseDate.ShouldBe(new DateOnly(2026, 6, 4));
        claim.ClaimDate.ShouldBe(new DateOnly(2026, 10, 4));
        claim.Region.ShouldBe(Region.NA);
        claim.Reference.ShouldBe(accepted.Reference);

        _claims.Received(2).AddEvidence(Arg.Any<ClaimEvidence>());
        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e =>
            e.Kind == EvidenceKind.Invoice && e.ContentType == "application/pdf" && e.Round == 1 && e.ClaimId == claim.Id
            && e.BlobPath.StartsWith($"claims/{claim.Id}/1/", StringComparison.Ordinal) && e.BlobPath.EndsWith(".pdf", StringComparison.Ordinal)));
        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.Kind == EvidenceKind.Photo && e.ContentType == "image/jpeg"));

        var job = _job.ShouldNotBeNull();
        job.ClaimId.ShouldBe(claim.Id);
        job.TenantId.ShouldBe(TenantId);
        job.Round.ShouldBe(1);
        job.CorrelationId.ShouldBe("corr-1");
        job.Status.ShouldBe(ClaimJobStatus.Queued);

        _events.ShouldBe(
        [
            "upload", "upload", "begin", "save",
            "trail:ClaimSubmitted", "trail:TenantResolved", "trail:EvidenceStored", "commit",
        ]);
        await _trail.Received(1).AppendAsync(claim.Id, TrailStep.ClaimSubmitted, "claimant", Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_claims_agent_submission_is_recorded_as_submitted_by_the_staff_subject()
    {
        _tenant.Dispose();
        _tenant = TenantContextScope.Begin(TenantId, "aurora", "staff-sub-1", "corr-2", [Principals.ClaimsAgentRole]);

        var result = await Submit(Command(ValidData(), ClaimChannel.AgentPortal));

        result.ShouldBeOfType<SubmitClaimResult.Accepted>();
        _added!.Channel.ShouldBe(ClaimChannel.AgentPortal);
        _added.SubmittedBy.ShouldBe("staff-sub-1");
        _added.WasSubmittedBy("staff-sub-1").ShouldBeTrue();
    }

    [Theory]
    [InlineData("2026-10-05")]
    [InlineData("2027-01-01")]
    public async Task A_purchase_date_in_the_future_or_after_the_claim_date_is_rejected_without_creating_anything(string date)
    {
        var data = ValidData() with { Purchase = ValidData().Purchase! with { Date = date } };

        var result = await Submit(Command(data));

        var invalid = result.ShouldBeOfType<SubmitClaimResult.Invalid>();
        invalid.Errors.Keys.ShouldBe(["purchase.date"]);
        await NothingWasCreatedAsync();
    }

    [Fact]
    public async Task A_purchase_on_the_claim_date_is_accepted()
    {
        var data = ValidData() with { Purchase = ValidData().Purchase! with { Date = "2026-10-04" } };

        (await Submit(Command(data))).ShouldBeOfType<SubmitClaimResult.Accepted>();
    }

    [Fact]
    public async Task Missing_and_malformed_fields_are_reported_by_their_dotted_paths()
    {
        var data = new ClaimSubmissionData(
            new ClaimSubmissionCustomer(" ", "not-an-email", "abc", null, null, null, "USA"),
            new ClaimSubmissionProduct(null, new string('S', 51)),
            new ClaimSubmissionPurchase("04/06/2026", "", -1m, "US", null),
            "too short");

        var invalid = (await Submit(Command(data))).ShouldBeOfType<SubmitClaimResult.Invalid>();

        invalid.Errors.Keys.ShouldBe(
            [
                "customer.fullName", "customer.email", "customer.phone", "customer.country", "product.modelCode", "product.serialNumber",
                "purchase.date", "purchase.place", "purchase.price", "purchase.currency", "purchase.country", "problemDescription",
            ],
            ignoreOrder: true);
        await NothingWasCreatedAsync();
    }

    [Fact]
    public async Task A_missing_claim_part_is_a_validation_problem()
    {
        var invalid = (await Submit(Command(null))).ShouldBeOfType<SubmitClaimResult.Invalid>();

        invalid.Errors.Keys.ShouldContain("claim");
        await NothingWasCreatedAsync();
    }

    [Fact]
    public async Task Lower_case_country_and_currency_codes_are_accepted()
    {
        var data = ValidData() with { Purchase = ValidData().Purchase! with { Currency = "usd", Country = "de" } };

        (await Submit(Command(data))).ShouldBeOfType<SubmitClaimResult.Accepted>();
        _added!.Region.ShouldBe(Region.EU);
    }

    [Fact]
    public async Task An_invoice_and_one_to_eight_photos_are_required()
    {
        var none = (await Submit(Command(ValidData(), invoices: [], photos: []))).ShouldBeOfType<SubmitClaimResult.Invalid>();
        none.Errors.Keys.ShouldBe(["invoice", "photos"], ignoreOrder: true);

        var tooMany = (await Submit(Command(ValidData(), photos: Enumerable.Range(1, 9).Select(i => File($"p{i}.jpg", JpegBytes)).ToList())))
            .ShouldBeOfType<SubmitClaimResult.Invalid>();
        tooMany.Errors.Keys.ShouldBe(["photos"]);

        var twoInvoices = (await Submit(Command(ValidData(), invoices: [File("a.pdf", PdfBytes), File("b.pdf", PdfBytes)])))
            .ShouldBeOfType<SubmitClaimResult.Invalid>();
        twoInvoices.Errors.Keys.ShouldBe(["invoice"]);
        await NothingWasCreatedAsync();
    }

    [Fact]
    public async Task Eight_photos_are_accepted()
    {
        var photos = Enumerable.Range(1, 8).Select(i => File($"p{i}.jpg", JpegBytes)).ToList();

        (await Submit(Command(ValidData(), photos: photos))).ShouldBeOfType<SubmitClaimResult.Accepted>();
        _claims.Received(9).AddEvidence(Arg.Any<ClaimEvidence>());
    }

    [Fact]
    public async Task File_types_are_judged_by_content_not_by_name()
    {
        // A PNG named .jpg is stored as PNG; a WebP named .pdf is a valid photo.
        var photos = new[] { File("photo.jpg", PngBytes), File("photo.pdf", WebPBytes) };

        (await Submit(Command(ValidData(), photos: photos))).ShouldBeOfType<SubmitClaimResult.Accepted>();

        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.ContentType == "image/png" && e.BlobPath.EndsWith(".png", StringComparison.Ordinal)));
        _claims.Received(1).AddEvidence(Arg.Is<ClaimEvidence>(e => e.ContentType == "image/webp"));
    }

    [Fact]
    public async Task Unsupported_empty_oversized_and_pdf_photos_are_field_errors()
    {
        var invalid = (await Submit(Command(
                ValidData(),
                invoices: [File("invoice.pdf", TextBytes)],
                photos: [File("empty.jpg", []), File("huge.jpg", JpegBytes, ClaimEvidence.MaxSizeBytes + 1), File("scan.pdf", PdfBytes)])))
            .ShouldBeOfType<SubmitClaimResult.Invalid>();

        invalid.Errors["invoice"].ShouldHaveSingleItem().ShouldContain("invoice.pdf");
        invalid.Errors["photos"].Length.ShouldBe(3);
        await NothingWasCreatedAsync();
    }

    [Fact]
    public async Task A_heic_photo_is_unsupported_media_asking_for_jpeg_or_png()
    {
        var result = await Submit(Command(ValidData(), photos: [File("IMG_0001.jpg", HeicBytes)]));

        var unsupported = result.ShouldBeOfType<SubmitClaimResult.UnsupportedMediaType>();
        unsupported.Detail.ShouldContain("JPEG or PNG");
        await NothingWasCreatedAsync();
    }

    [Fact]
    public async Task A_product_missing_from_the_catalog_leaves_the_product_empty()
    {
        var data = ValidData() with { Product = new ClaimSubmissionProduct("AUR-UNKNOWN", "ZZ-1") };

        (await Submit(Command(data))).ShouldBeOfType<SubmitClaimResult.Accepted>();

        _added!.ProductId.ShouldBeNull();
        _added.ProductModelCode.ShouldBe("AUR-UNKNOWN");
    }

    [Fact]
    public async Task A_reference_already_used_in_the_tenant_is_regenerated()
    {
        _claims.FindByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult<Claim?>(ExistingClaim()), _ => Task.FromResult<Claim?>(null));

        var accepted = (await Submit(Command(ValidData()))).ShouldBeOfType<SubmitClaimResult.Accepted>();

        await _claims.Received(2).FindByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        ClaimReference.IsValid(accepted.Reference).ShouldBeTrue();
    }

    [Theory]
    [InlineData("US", "DE", Region.NA)]
    [InlineData("FR", "US", Region.EU)]
    [InlineData("JP", "CA", Region.NA)]
    [InlineData(null, "SE", Region.EU)]
    [InlineData("JP", "AU", null)]
    public void The_region_is_the_purchase_country_else_the_customer_country(string? purchaseCountry, string customerCountry, Region? expected)
        => SubmitClaim.DeriveRegion(purchaseCountry, customerCountry).ShouldBe(expected);

    [Fact]
    public void Generated_references_are_ten_crockford_base32_characters()
    {
        var references = Enumerable.Range(0, 200).Select(_ => ClaimReference.Generate()).ToList();

        references.ShouldAllBe(r => r.Length == 10 && r.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)));
        references.Distinct().Count().ShouldBe(references.Count);
    }

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Magic_bytes_identify_the_file_type(byte[] header, EvidenceFileType expected)
        => EvidenceFileSignature.Detect(header).ShouldBe(expected);

    public static TheoryData<byte[], EvidenceFileType> Signatures() => new()
    {
        { JpegBytes, EvidenceFileType.Jpeg },
        { PngBytes, EvidenceFileType.Png },
        { WebPBytes, EvidenceFileType.WebP },
        { PdfBytes, EvidenceFileType.Pdf },
        { HeicBytes, EvidenceFileType.Heic },
        { [0x00, 0x00, 0x00, 0x18, .. "ftypmif1"u8, 0x00, 0x00, 0x00, 0x00], EvidenceFileType.Heic },
        { [0x00, 0x00, 0x00, 0x18, .. "ftypavif"u8, 0x00, 0x00, 0x00, 0x00], EvidenceFileType.Unknown },
        { "GIF89a"u8.ToArray(), EvidenceFileType.Unknown },
        { TextBytes, EvidenceFileType.Unknown },
        { "RIFF\x24\x00\x00\x00WAVE"u8.ToArray(), EvidenceFileType.Unknown },
        { [0xFF, 0xD8], EvidenceFileType.Unknown },
        { Array.Empty<byte>(), EvidenceFileType.Unknown },
    };

    private Task<SubmitClaimResult> Submit(SubmitClaimCommand command)
        => new SubmitClaim(_tenant, _crm, _catalog, _claims, _documents, _jobs, _trail, _unitOfWork, _time)
            .ExecuteAsync(command, TestContext.Current.CancellationToken);

    private async Task NothingWasCreatedAsync()
    {
        _claims.DidNotReceive().Add(Arg.Any<Claim>());
        _claims.DidNotReceive().AddEvidence(Arg.Any<ClaimEvidence>());
        await _crm.DidNotReceiveWithAnyArgs().FindOrCreateCustomerAsync(default!, default);
        await _documents.DidNotReceiveWithAnyArgs().UploadEvidenceAsync(default!, default!, default!, default);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default);
    }

    private static SubmitClaimCommand Command(
        ClaimSubmissionData? data,
        ClaimChannel channel = ClaimChannel.ClaimantPortal,
        IReadOnlyList<EvidenceUpload>? invoices = null,
        IReadOnlyList<EvidenceUpload>? photos = null)
        => new(channel, data, invoices ?? [File("invoice.pdf", PdfBytes)], photos ?? [File("photo-1.jpg", JpegBytes)]);

    private static EvidenceUpload File(string name, byte[] content, long? length = null)
        => new(name, length ?? content.Length, () => new MemoryStream(content, writable: false));

    private static ClaimSubmissionData ValidData() => new(
        new ClaimSubmissionCustomer("Sample Customer", "Sample.Customer@Aurora.example.com", "+1 555 010 0001", "1 Example Road", "Springfield", "00001", "US"),
        new ClaimSubmissionProduct(ModelCode, Serial),
        new ClaimSubmissionPurchase("2026-06-04", "Aurora Store", 450.00m, "USD", "US"),
        "The tablet stopped turning on and does not charge any more.");

    private static Claim ExistingClaim() => Claim.Submit(
        Guid.CreateVersion7(), TenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, Guid.CreateVersion7(),
        "x@example.com", null, ModelCode, null, Serial, new DateOnly(2026, 1, 1), "Store", 10m, Region.NA,
        "An existing claim with a long enough description.", Now);
}
