using System.Security.Cryptography;
using Warranty.Domain.Claims;
using Warranty.Infrastructure.Storage;

namespace Warranty.UnitTests.Infrastructure;

public sealed class BlobDocumentStoreTests
{
    [Fact]
    public void Evidence_paths_built_by_the_domain_are_accepted()
    {
        var path = ClaimEvidence.BlobPathFor(Guid.NewGuid(), 2, Guid.NewGuid(), "image/jpeg");

        Should.NotThrow(() => BlobDocumentStore.ValidatePath(path, "claims/"));
        path.ShouldEndWith(".jpg");
    }

    [Theory]
    [InlineData("claims/../tenant-borealis/claims/x.jpg")]
    [InlineData("claims/./x.jpg")]
    [InlineData("claims//x.jpg")]
    [InlineData("/claims/x.jpg")]
    [InlineData("claims\\x.jpg")]
    [InlineData("claims/%2e%2e/x.jpg")]
    [InlineData("tenant-borealis/claims/x.jpg")]
    [InlineData("https://evil/claims/x.jpg")]
    [InlineData("claims/x y.jpg")]
    public void Paths_that_could_leave_the_tenant_container_are_refused(string path)
        => Should.Throw<UnauthorizedAccessException>(() => BlobDocumentStore.ValidatePath(path, "claims/"));

    [Fact]
    public async Task The_hashing_stream_reports_the_sha256_and_size_of_what_was_read()
    {
        var data = RandomNumberGenerator.GetBytes(100_000);
        await using var hashing = new HashingReadStream(new MemoryStream(data), maxBytes: data.Length);

        await hashing.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);

        hashing.BytesRead.ShouldBe(data.Length);
        hashing.GetSha256().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    [Fact]
    public async Task The_hashing_stream_aborts_past_the_size_limit()
    {
        await using var hashing = new HashingReadStream(new MemoryStream(new byte[11]), maxBytes: 10);

        await Should.ThrowAsync<InvalidDataException>(() => hashing.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
    }
}
