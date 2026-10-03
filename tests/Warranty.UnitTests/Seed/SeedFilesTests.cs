using Warranty.Domain.Common;
using Warranty.MigrationService.Seeding;

namespace Warranty.UnitTests.Seed;

public sealed class SeedFilesTests
{
    [Fact]
    public void The_repository_seed_loads_both_tenants_and_all_knowledge_with_blob_paths()
    {
        var seed = SeedFiles.Load(SeedRoot());

        seed.Tenants.Select(t => t.Tenant.Slug).ShouldBe(["aurora", "borealis"]);
        var aurora = seed.Tenants[0];
        aurora.Tenant.KnowledgeNamespace.ShouldBe("tenant-aurora");
        aurora.Knowledge.Select(k => k.BlobPath).ShouldBe(["tenant-aurora/policies/AUR-WP-v1.md", "tenant-aurora/policies/AUR-WP-v2.md"]);
        aurora.HistoricalClaims.Count.ShouldBe(2);
        aurora.ServiceCenters.ShouldContain(c => c.Region == Region.EU);
        seed.Tenants[1].HistoricalClaims.ShouldBeEmpty();
        seed.GlobalKnowledge.Select(k => k.BlobPath).ShouldBe(
            ["global/fraud-patterns.md", "global/injection-phrases.md", "global/procedures.md", "global/terminology.md"]);
        seed.GlobalKnowledge.Concat(aurora.Knowledge).ShouldAllBe(k => !k.Content.Contains('\r'));
    }

    [Fact]
    public void History_dates_are_offsets_from_the_seeding_time()
    {
        var history = SeedFiles.Load(SeedRoot()).Tenants[0].HistoricalClaims[0];
        var seedingTime = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

        history.FinalizedAt(seedingTime).ShouldBe(seedingTime.AddDays(-30));
        history.SubmittedAt(seedingTime).ShouldBe(seedingTime.AddDays(-34));
        history.PurchaseDate(seedingTime).ShouldBe(new DateOnly(2026, 4, 6));
    }

    [Fact]
    public void Unknown_references_and_misnamed_folders_are_rejected_before_anything_is_written()
    {
        var root = CopyOfSeed();
        try
        {
            var serials = Path.Combine(root, "tenants", "borealis", "serials.json");
            File.WriteAllText(serials, File.ReadAllText(serials).Replace("\"BOR-HUB2\"", "\"BOR-HUB9\"", StringComparison.Ordinal));
            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message.ShouldContain("names unknown model BOR-HUB9");

            Directory.Move(Path.Combine(root, "tenants", "borealis"), Path.Combine(root, "tenants", "north"));
            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message.ShouldContain("must be named after the slug");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CopyOfSeed()
    {
        var target = Directory.CreateTempSubdirectory("warranty-seed-").FullName;
        var source = SeedRoot();
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private static string SeedRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
            {
                return Path.Combine(dir.FullName, "seed");
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
