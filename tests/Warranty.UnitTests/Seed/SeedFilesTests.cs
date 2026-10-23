using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;
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

    [Theory]
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

    [Fact]
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

    [Fact]
    public void Each_policy_version_carries_its_remedies_block_or_none()
    {
        var root = CopyOfSeed();
        try
        {
            var v2 = Path.Combine(root, "tenants", "aurora", "policies", "AUR-WP-v2.md");
            File.WriteAllText(v2, WithRemedies(File.ReadAllText(v2), "remedies:\n  coveredDefect: { allowed: [Repair, Refund] }\n  repairFailed: { afterFailedAttempts: 3, remedy: Replacement }"));
            var v1 = Path.Combine(root, "tenants", "aurora", "policies", "AUR-WP-v1.md");
            File.WriteAllText(v1, WithRemedies(File.ReadAllText(v1), block: null));

            var remedies = SeedFiles.Load(root).Tenants[0].PolicyRemedies;

            remedies.Select(r => (r.BlobPath, r.PolicyCode, r.Version)).ShouldBe(
                [("tenant-aurora/policies/AUR-WP-v1.md", "AUR-WP", 1), ("tenant-aurora/policies/AUR-WP-v2.md", "AUR-WP", 2)]);
            remedies[0].RemedyRules.ShouldBeNull();
            var rules = remedies[1].RemedyRules.ShouldNotBeNull();
            rules.CoveredDefect!.Allowed.ShouldBe([RemedyType.Repair, RemedyType.Refund]);
            rules.RepairFailed.ShouldBe(new RepairFailedRule(3, RemedyType.Replacement, null));
            (rules.RepairNotEconomical, rules.ProtectionPlan).ShouldBe((null, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("remedies:\n  repairNotEconomical: { limitPercentOfClaimValue: 0, remedy: Refund }", "/repairNotEconomical/limitPercentOfClaimValue")]
    [InlineData("remedies:\n  repairFailed: { afterFailedAttempts: \"2\", remedy: Refund }", "/repairFailed/afterFailedAttempts")]
    [InlineData("remedies:\n  coveredDefect: { remedy: Swap }", "/coveredDefect/remedy")]
    [InlineData("remedies:\n  goodwill: { remedy: Refund }", "")]
    public void An_invalid_remedies_block_names_the_file_and_the_schema_location(string block, string location)
    {
        var root = CopyOfSeed();
        try
        {
            var v1 = Path.Combine(root, "tenants", "borealis", "policies", "BOR-WP-v1.md");
            File.WriteAllText(v1, WithRemedies(File.ReadAllText(v1), block));

            var message = Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message;

            message.ShouldContain("tenant-borealis/policies/BOR-WP-v1.md has an invalid remedies block");
            message.ShouldContain($"remedies{location}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Service_centers_read_address_hours_contact_activity_and_priority_with_defaults()
    {
        var root = CopyOfSeed();
        try
        {
            File.WriteAllText(Path.Combine(root, "tenants", "borealis", "service-centers.json"), """
                [
                  { "id": "22222222-0003-7000-8000-000000000001", "region": "NA", "name": "Open", "capabilities": ["tablet"],
                    "address": "1 Test Lane", "openingHours": "Mon–Fri 09:00–17:00", "contact": "desk@example.com", "priority": 5 },
                  { "id": "22222222-0003-7000-8000-000000000002", "region": "EU", "name": "Closed", "capabilities": ["tablet"], "isActive": false }
                ]
                """);

            var centers = SeedFiles.Load(root).Tenants[1].ServiceCenters;

            centers[0].ShouldBe(new ServiceCenterSeed(
                Guid.Parse("22222222-0003-7000-8000-000000000001"), Region.NA, "Open", centers[0].Capabilities,
                "1 Test Lane", "Mon–Fri 09:00–17:00", "desk@example.com", IsActive: true, Priority: 5));
            (centers[1].IsActive, centers[1].Priority, centers[1].Address).ShouldBe((false, ServiceCenter.DefaultPriority, null));
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
