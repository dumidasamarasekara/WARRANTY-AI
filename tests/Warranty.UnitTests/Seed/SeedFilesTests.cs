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

    [Fact]
    public void Remedies_are_optional_so_a_policy_version_without_a_block_still_loads()
    {
        var root = CopyOfSeed();
        try
        {
            foreach (var policy in Directory.GetFiles(Path.Combine(root, "tenants"), "*.md", SearchOption.AllDirectories))
            {
                File.WriteAllText(policy, WithRemedies(File.ReadAllText(policy), block: null));
            }

            Should.NotThrow(() => SeedFiles.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Skip = "Pending T214")]
    [InlineData("  coveredDefect: { remedy: Repair, allowed: [Refund, Replacement] }", "remedy and allowed together")]
    [InlineData("  coveredDefect: { allowed: [Refund] }", "one-item allowed list")]
    [InlineData("  repairNotEconomical: { limitPercentOfClaimValue: 101, allowed: [Refund, Replacement] }", "limit 101")]
    [InlineData("  repairFailed: { afterFailedAttempts: 0, remedy: Refund }", "attempts 0")]
    [InlineData("  protectionPlan: { liftsAccidentalDamageExclusion: true }", "plan lifting the exclusion without a remedy")]
    [InlineData("  coveredDefect: Repair", "a row that is not an object")]
    public void An_invalid_remedies_block_is_rejected_before_anything_is_written(string row, string reason)
    {
        var root = CopyOfSeed();
        try
        {
            var policy = Path.Combine(root, "tenants", "aurora", "policies", "AUR-WP-v1.md");
            File.WriteAllText(policy, WithRemedies(File.ReadAllText(policy), "remedies:\n" + row));

            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root), reason).Message.ShouldContain("AUR-WP-v1", Case.Insensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = "Pending T214")]
    public void An_active_repair_centre_needs_an_address_and_opening_hours_but_an_inactive_one_does_not()
    {
        var root = CopyOfSeed();
        try
        {
            var file = Path.Combine(root, "tenants", "aurora", "service-centers.json");
            var original = File.ReadAllText(file);

            File.WriteAllText(file, WithFirstCentre(original, isActive: true, without: "address"));
            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message.ShouldContain("address", Case.Insensitive);

            File.WriteAllText(file, WithFirstCentre(original, isActive: true, without: "openingHours"));
            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message.ShouldContain("opening", Case.Insensitive);

            File.WriteAllText(file, WithFirstCentre(original, isActive: false, without: "address"));
            Should.NotThrow(() => SeedFiles.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The policy file with its front-matter <c>remedies</c> block removed and, unless <paramref name="block"/>
    /// is null, replaced by the given YAML (inserted just before the closing <c>---</c>).
    /// </summary>
    private static string WithRemedies(string policy, string? block)
    {
        var lines = policy.Split('\n').ToList();
        var end = lines.IndexOf("---", 1);
        end.ShouldBeGreaterThan(0);

        var start = lines.FindIndex(1, end - 1, l => l.StartsWith("remedies:", StringComparison.Ordinal));
        if (start > 0)
        {
            var next = start + 1;
            while (next < end && (lines[next].StartsWith(' ') || lines[next].Length == 0))
            {
                next++;
            }

            lines.RemoveRange(start, next - start);
            end -= next - start;
        }

        if (block is not null)
        {
            lines.InsertRange(end, block.Split('\n'));
        }

        return string.Join('\n', lines);
    }

    /// <summary>The service-centre file with the first centre's activity set and one field removed.</summary>
    private static string WithFirstCentre(string json, bool isActive, string without)
    {
        var centres = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray();
        var first = centres[0]!.AsObject();
        first["isActive"] = isActive;
        first.Remove(without);
        return centres.ToJsonString();
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
