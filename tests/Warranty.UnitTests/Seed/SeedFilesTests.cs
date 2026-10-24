using Warranty.Domain.Claims;
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
        // Borealis: the approved accidental-damage claim that uses up BS11-24-0015's included incident (002 quickstart R14).
        var borealisHistory = seed.Tenants[1].HistoricalClaims.ShouldHaveSingleItem();
        (borealisHistory.SerialNumber, borealisHistory.Outcome).ShouldBe(("BS11-24-0015", "Approved"));
        borealisHistory.PhotoDamageTypes.ShouldBe(["CRACKED_SCREEN"]);
        aurora.HistoricalClaims.ShouldAllBe(h => h.PhotoDamageTypes == null);
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
    /// Feature 002 (T222): each tenant's <c>remedy-scenarios.json</c> holds the approved claims of R1/R2, its half
    /// of the R4 "same facts, different company" pair and the claims the remedy stories drive (Borealis R9, R10 for T246;
    /// R11 for T251); the remedy itself is not in the file.
    /// </summary>
    [Fact]
    public void Remedy_scenarios_load_per_tenant_with_the_R4_pair_sharing_the_same_facts()
    {
        var seed = SeedFiles.Load(SeedRoot());
        var aurora = seed.Tenants[0].RemedyScenarios;
        var borealis = seed.Tenants[1].RemedyScenarios;

        aurora.Select(s => (s.Scenario, s.Reference, s.ModelCode, s.Region)).ShouldBe(
            [("R1", "RMDYR1A001", "AUR-TAB10", Region.NA), ("R4-aurora", "RMDYR4A001", "AUR-TAB10", Region.EU),
             ("R11-aurora", "RMDYR11A01", "AUR-TAB10", Region.NA)]);
        borealis.Select(s => (s.Scenario, s.Reference, s.ModelCode, s.Region)).ShouldBe(
            [("R2", "RMDYR2B001", "BOR-SLATE11", Region.EU), ("R4-borealis", "RMDYR4B001", "BOR-SLATE11", Region.EU),
             ("R9-borealis", "RMDYR9B001", "BOR-SLATE11", Region.EU), ("R10-borealis", "RMDYR10B01", "BOR-SLATE11", Region.EU),
             ("R11-borealis", "RMDYR11B01", "BOR-SLATE11", Region.EU)]);
        aurora.Concat(borealis).ShouldAllBe(s => s.ApprovedBy == DecidedBy.System);
        aurora[0].Id.ShouldBe(Guid.Parse("11111111-0005-7000-8000-000000000001"));
        borealis[0].Id.ShouldBe(Guid.Parse("22222222-0005-7000-8000-000000000001"));

        var (a, b) = (aurora[1], borealis[1]);
        (a.Region, a.PurchasePlace, a.PurchasePrice, a.ProblemDescription, a.PurchasedDaysAgo, a.SubmittedDaysAgo, a.ApprovedDaysAgo)
            .ShouldBe((b.Region, b.PurchasePlace, b.PurchasePrice, b.ProblemDescription, b.PurchasedDaysAgo, b.SubmittedDaysAgo, b.ApprovedDaysAgo));
    }

    [Fact]
    public void Remedy_scenario_dates_are_offsets_from_the_seeding_time()
    {
        var r1 = SeedFiles.Load(SeedRoot()).Tenants[0].RemedyScenarios[0];
        var seedingTime = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        r1.PurchaseDate(seedingTime).ShouldBe(new DateOnly(2026, 8, 25));
        r1.SubmittedAt(seedingTime).ShouldBe(seedingTime.AddDays(-3));
        r1.ApprovedAt(seedingTime).ShouldBe(seedingTime.AddDays(-3));
    }

    [Fact]
    public void Remedy_scenarios_are_optional()
    {
        var root = CopyOfSeed();
        try
        {
            File.Delete(Path.Combine(root, "tenants", "borealis", "remedy-scenarios.json"));

            var seed = SeedFiles.Load(root);

            seed.Tenants[1].RemedyScenarios.ShouldBeEmpty();
            seed.Tenants[0].RemedyScenarios.Count.ShouldBe(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("serialNumber", "\"AT10-99-0001\"", "names unknown serial AT10-99-0001")]
    [InlineData("modelCode", "\"AUR-TV55\"", "names unknown model AUR-TV55")]
    [InlineData("customerEmail", "\"nobody@aurora.example.com\"", "names unknown customer")]
    [InlineData("reference", "\"TOO-SHORT\"", "invalid claim reference")]
    [InlineData("reference", "\"HSTRY00030\"", "HSTRY00030 is used by more than one seeded claim")]
    [InlineData("id", "\"11111111-0004-7000-8000-000000000001\"", "is used by more than one seeded claim")]
    [InlineData("scenario", "\"R4-aurora\"", "R4-aurora is listed more than once")]
    [InlineData("approvedDaysAgo", "-10", "submitted and approved in that order")]
    [InlineData("approvedDaysAgo", "1", "submitted and approved in that order")]
    [InlineData("explanation", "\" \"", "needs a problem description and an explanation")]
    public void An_invalid_remedy_scenario_is_rejected_before_anything_is_written(string field, string json, string message)
    {
        var root = CopyOfSeed();
        try
        {
            var file = Path.Combine(root, "tenants", "aurora", "remedy-scenarios.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            document["claims"]![0]![field] = System.Text.Json.Nodes.JsonNode.Parse(json);
            File.WriteAllText(file, document.ToJsonString());

            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message.ShouldContain(message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Feature 002 (T251): R11 needs claims whose remedy a reviewer already decided — Replacement at Aurora, Refund at
    /// Borealis, each after two failed repairs (row <c>repairFailed</c>). Only these claims carry a decided remedy.
    /// </summary>
    [Fact]
    public void The_R11_claims_carry_a_reviewer_decided_Replacement_and_Refund()
    {
        var seed = SeedFiles.Load(SeedRoot());
        var decided = seed.Tenants.SelectMany(t => t.RemedyScenarios).Where(s => s.DecidedRemedy is not null).ToList();

        decided.Select(s => (s.Scenario, s.DecidedRemedy!.Scenario, s.DecidedRemedy.Remedy, s.DecidedRemedy.FailedAttempts)).ShouldBe(
        [
            ("R11-aurora", RemedyScenario.RepairFailed, RemedyType.Replacement, 2),
            ("R11-borealis", RemedyScenario.RepairFailed, RemedyType.Refund, 2),
        ]);
        decided.ShouldAllBe(s => s.DecidedRemedy!.DecidedDaysAgo >= s.ApprovedDaysAgo && s.DecidedRemedy.DecidedDaysAgo < 0);
        var seedingTime = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        decided[0].DecidedRemedy!.DecidedAt(seedingTime).ShouldBe(seedingTime.AddDays(decided[0].DecidedRemedy!.DecidedDaysAgo));
    }

    [Theory]
    [InlineData("decidedDaysAgo", "-40", "must decide its remedy after the approval, in the past")]
    [InlineData("decidedDaysAgo", "1", "must decide its remedy after the approval, in the past")]
    [InlineData("reason", "\"too short\"", "needs a remedy decision reason")]
    [InlineData("failedAttempts", "-1", "negative failed-attempt count or estimate")]
    [InlineData("estimateAmount", "-5", "negative failed-attempt count or estimate")]
    public void An_invalid_decided_remedy_is_rejected_before_anything_is_written(string field, string json, string message)
    {
        var root = CopyOfSeed();
        try
        {
            var file = Path.Combine(root, "tenants", "aurora", "remedy-scenarios.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            var r11 = document["claims"]!.AsArray().Single(c => (string?)c!["scenario"] == "R11-aurora")!;
            r11["decidedRemedy"]![field] = System.Text.Json.Nodes.JsonNode.Parse(json);
            File.WriteAllText(file, document.ToJsonString());

            Should.Throw<InvalidDataException>(() => SeedFiles.Load(root)).Message.ShouldContain(message);
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
