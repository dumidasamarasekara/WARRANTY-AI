using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;
using Warranty.MigrationService.Seeding;
using Warranty.UnitTests.Domain;

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
    [InlineData("  replacement: { minimumCoverMonths: -1 }", "minimum replacement cover -1")]
    [InlineData("  replacement: { minimumCoverMonths: 37 }", "minimum replacement cover 37")]
    [InlineData("  replacement: { productRule: AnyModel }", "unknown product rule")]
    [InlineData("  replacement: { returnRule: Never }", "unknown return rule")]
    [InlineData("  replacementNotAvailable: { remedy: Refund, allowed: [Repair, Refund] }", "not-available row with remedy and allowed together")]
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
    [InlineData("remedies:\n  replacement: { productRule: AnyModel }", "/replacement/productRule")]
    [InlineData("remedies:\n  replacement: { minimumCoverMonths: 37 }", "/replacement/minimumCoverMonths")]
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

    /// <summary>
    /// Feature 003 (T302): the embedded schema accepts the optional <c>replacement</c> and
    /// <c>replacementNotAvailable</c> members (research R1); these cases check the schema step alone.
    /// </summary>
    [Theory]
    [InlineData("  replacement: { productRule: SameCategoryAtLeastClaimValue, returnRule: AtCollection, minimumCoverMonths: 0 }\n  replacementNotAvailable: { remedy: Refund }")]
    [InlineData("  replacement: { productRule: SameModel, returnRule: BeforeReservation, minimumCoverMonths: 36 }\n  replacementNotAvailable: { allowed: [Repair, Refund] }")]
    [InlineData("  replacement: {}")]
    [InlineData("  coveredDefect: { remedy: Repair }")]
    public void A_valid_replacement_block_passes_the_remedy_rules_schema(string rows)
        => RemedyRulesSchema.Validate(PolicyWith("remedies:\n" + rows)).ShouldBeEmpty();

    [Theory]
    [InlineData("  replacement: { productRule: AnyModel }", "remedies/replacement/productRule")]
    [InlineData("  replacement: { minimumCoverMonths: 37 }", "remedies/replacement/minimumCoverMonths")]
    [InlineData("  replacement: { minimumCoverMonths: -1 }", "remedies/replacement/minimumCoverMonths")]
    [InlineData("  replacement: { returnRule: Never }", "remedies/replacement/returnRule")]
    [InlineData("  replacement: { productRule: SameModel, upgrade: true }", "remedies/replacement")]
    [InlineData("  replacementNotAvailable: { remedy: Refund, allowed: [Repair, Refund] }", "remedies/replacementNotAvailable")]
    [InlineData("  replacementNotAvailable: {}", "remedies/replacementNotAvailable")]
    [InlineData("  replacementNotAvailable: { remedy: Swap }", "remedies/replacementNotAvailable/remedy")]
    public void An_invalid_replacement_block_fails_the_remedy_rules_schema(string rows, string location)
        => RemedyRulesSchema.Validate(PolicyWith("remedies:\n" + rows)).ShouldContain(p => p.StartsWith(location, StringComparison.Ordinal));

    [Fact]
    public void The_embedded_remedy_rules_schema_is_a_copy_of_the_002_contract()
    {
        var repo = Path.GetFullPath(Path.Combine(SeedRoot(), ".."));
        File.ReadAllText(Path.Combine(repo, "src", "Warranty.MigrationService", "Seeding", "Schemas", "remedy-rules.schema.json"))
            .ShouldBe(File.ReadAllText(Path.Combine(repo, "specs", "002-remedy-repair-handover", "contracts", "remedy-rules.schema.json")));
    }

    private static string PolicyWith(string block)
        => $"---\ndocumentType: WarrantyPolicy\n{block}\n---\n# Policy\n";

    /// <summary>Feature 003 (T313): each seeded policy version carries its tenant's replacement rules of tasks.md "Fixed identifiers".</summary>
    [Fact]
    public void The_repository_seed_loads_the_replacement_rules_of_each_policy_version()
    {
        var seed = SeedFiles.Load(SeedRoot());

        foreach (var (tenant, expected) in new[] { (seed.Tenants[0], RemedyRulesContract.AuroraWithReplacement), (seed.Tenants[1], RemedyRulesContract.BorealisWithReplacement) })
        {
            tenant.PolicyRemedies.ShouldNotBeEmpty(tenant.Tenant.Slug);
            foreach (var version in tenant.PolicyRemedies)
            {
                RemedyRulesContract.ShouldBeSameRules(version.RemedyRules.ShouldNotBeNull(version.BlobPath), expected);
            }
        }
    }

    [Fact]
    public void An_empty_replacement_member_loads_as_the_defaults_and_a_missing_one_as_none()
    {
        var root = CopyOfSeed();
        try
        {
            var v1 = Path.Combine(root, "tenants", "aurora", "policies", "AUR-WP-v1.md");
            File.WriteAllText(v1, WithRemedies(File.ReadAllText(v1), "remedies:\n  coveredDefect: { remedy: Repair }\n  replacement: {}"));
            var v2 = Path.Combine(root, "tenants", "aurora", "policies", "AUR-WP-v2.md");
            File.WriteAllText(v2, WithRemedies(File.ReadAllText(v2), "remedies:\n  coveredDefect: { remedy: Repair }"));

            var remedies = SeedFiles.Load(root).Tenants[0].PolicyRemedies;

            remedies[0].RemedyRules!.Replacement.ShouldBe(ReplacementRules.Default);
            remedies[0].RemedyRules!.ReplacementNotAvailable.ShouldBeNull();
            (remedies[1].RemedyRules!.Replacement, remedies[1].RemedyRules!.ReplacementNotAvailable).ShouldBe((null, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Feature 003 (research R14): Aurora sells AUR-TAB11, the equivalent tablet a reviewer can approve.</summary>
    [Fact]
    public void The_repository_seed_loads_AUR_TAB11_with_its_catalog_serials()
    {
        var aurora = SeedFiles.Load(SeedRoot()).Tenants[0];

        aurora.Products.ShouldContain(new ProductSeed(Guid.Parse("11111111-0001-7000-8000-000000000004"), "AUR-TAB11", "Aurora Tab 11", "tablet", 520m));
        aurora.Serials.Count(s => s.ModelCode == "AUR-TAB11").ShouldBeGreaterThanOrEqualTo(15);
    }

    /// <summary>
    /// Feature 003 (T312): <c>issuesReplacements</c> is read from <c>service-centers.json</c> and defaults to false
    /// (FR-007); every active centre of the repository seed issues replacements.
    /// </summary>
    [Fact]
    public void Service_centers_read_whether_they_issue_replacements_defaulting_to_no()
    {
        var root = CopyOfSeed();
        try
        {
            File.WriteAllText(Path.Combine(root, "tenants", "borealis", "service-centers.json"), """
                [
                  { "id": "22222222-0003-7000-8000-000000000001", "region": "NA", "name": "Issuer", "capabilities": ["tablet"],
                    "address": "1 Test Lane", "openingHours": "Mon–Fri 09:00–17:00", "issuesReplacements": true },
                  { "id": "22222222-0003-7000-8000-000000000002", "region": "NA", "name": "Repairs only", "capabilities": ["tablet"],
                    "address": "2 Test Lane", "openingHours": "Mon–Fri 09:00–17:00" }
                ]
                """);

            SeedFiles.Load(root).Tenants[1].ServiceCenters.Select(c => c.IssuesReplacements).ShouldBe([true, false]);
            SeedFiles.Load(SeedRoot()).Tenants
                .SelectMany(t => t.ServiceCenters)
                .Where(c => c.IsActive)
                .ShouldAllBe(c => c.IssuesReplacements);
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
            (centers[1].IsActive, centers[1].Priority, centers[1].Address, centers[1].IssuesReplacements)
                .ShouldBe((false, ServiceCenter.DefaultPriority, null, false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Feature 002 (T222): each tenant's <c>remedy-scenarios.json</c> holds the approved claims of R1/R2, its half
    /// of the R4 "same facts, different company" pair and the claims the remedy stories drive (Borealis R9, R10 for T246;
    /// R11 for T251; Aurora R12 for T255); the remedy itself is not in the file.
    /// </summary>
    [Fact]
    public void Remedy_scenarios_load_per_tenant_with_the_R4_pair_sharing_the_same_facts()
    {
        var seed = SeedFiles.Load(SeedRoot());
        var aurora = seed.Tenants[0].RemedyScenarios;
        var borealis = seed.Tenants[1].RemedyScenarios;

        aurora.Select(s => (s.Scenario, s.Reference, s.ModelCode, s.Region)).ShouldBe(
            [("R1", "RMDYR1A001", "AUR-TAB10", Region.NA), ("R4-aurora", "RMDYR4A001", "AUR-TAB10", Region.EU),
             ("R11-aurora", "RMDYR11A01", "AUR-TAB10", Region.NA), ("R12-aurora", "RMDYR12A01", "AUR-TAB10", Region.NA)]);
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
            seed.Tenants[0].RemedyScenarios.Count.ShouldBe(4);
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
    /// Feature 003 (T321): each tenant's <c>replacement-scenarios.json</c> holds approved claims whose remedy a reviewer
    /// decided as Replacement — Aurora after two failed repairs (row <c>repairFailed</c>), Borealis after an uneconomical
    /// estimate (row <c>repairNotEconomical</c>) — with X1-aurora and X6-borealis as the "same facts, different company" pair.
    /// </summary>
    [Fact]
    public void Replacement_scenarios_load_per_tenant_with_the_X1_X6_pair_sharing_the_same_facts()
    {
        var seed = SeedFiles.Load(SeedRoot());
        var aurora = seed.Tenants[0].ReplacementScenarios;
        var borealis = seed.Tenants[1].ReplacementScenarios;

        aurora.Select(s => (s.Scenario, s.Reference, s.ModelCode, s.SerialNumber, s.Region)).ShouldBe(
        [
            ("X1-aurora", "RPMTX1A001", "AUR-TAB10", "AT10-24-0301", Region.EU), ("X5", "RPMTX5A001", "AUR-TAB10", "AT10-24-0305", Region.NA),
            ("X9-aurora", "RPMTX9A001", "AUR-TAB10", "AT10-24-0309", Region.NA), ("X10-aurora", "RPMTX10A01", "AUR-TAB10", "AT10-24-0310", Region.EU),
            ("X11-aurora", "RPMTX11A01", "AUR-TAB10", "AT10-24-0311", Region.NA), ("X12-aurora", "RPMTX12A01", "AUR-TAB10", "AT10-24-0312", Region.EU),
        ]);
        borealis.Select(s => (s.Scenario, s.Reference, s.ModelCode, s.SerialNumber, s.Region)).ShouldBe(
        [
            ("X6-borealis", "RPMTX6B001", "BOR-SLATE11", "BS11-24-0306", Region.EU), ("X7-borealis", "RPMTX7B001", "BOR-SLATE11", "BS11-24-0307", Region.EU),
            ("X8-borealis", "RPMTX8B001", "BOR-SLATE11", "BS11-24-0308", Region.EU), ("X12-borealis", "RPMTX12B01", "BOR-SLATE11", "BS11-24-0312", Region.EU),
        ]);
        var decidedAtAurora = aurora.Where(s => s.Scenario != "X9-aurora").ToList();
        decidedAtAurora.ShouldAllBe(s => s.DecidedRemedy!.Scenario == RemedyScenario.RepairFailed && s.DecidedRemedy.FailedAttempts == 2);
        borealis.ShouldAllBe(s => s.DecidedRemedy!.Scenario == RemedyScenario.RepairNotEconomical && s.DecidedRemedy.EstimateAmount == 350m);
        decidedAtAurora.Concat(borealis).ShouldAllBe(s => s.DecidedRemedy!.Remedy == RemedyType.Replacement);
        aurora.Concat(borealis).ShouldAllBe(s => s.ApprovedBy == DecidedBy.System);
        aurora[0].Id.ShouldBe(Guid.Parse("11111111-0006-7000-8000-000000000001"));
        borealis[0].Id.ShouldBe(Guid.Parse("22222222-0006-7000-8000-000000000006"));

        var (a, b) = (aurora[0], borealis[0]);
        (a.Region, a.PurchasePlace, a.PurchasePrice, a.ProblemDescription, a.PurchasedDaysAgo, a.SubmittedDaysAgo, a.ApprovedDaysAgo, a.DecidedRemedy!.DecidedDaysAgo)
            .ShouldBe((b.Region, b.PurchasePlace, b.PurchasePrice, b.ProblemDescription, b.PurchasedDaysAgo, b.SubmittedDaysAgo, b.ApprovedDaysAgo, b.DecidedRemedy!.DecidedDaysAgo));
    }

    /// <summary>
    /// Feature 003 (T334, quickstart X9, research R9): X9-aurora carries no decided remedy — it is seeded with the Repair of its
    /// policy version, and the replacement comes later from a reviewer's decision after its second failed repair. X7 and X8
    /// are X6-like Borealis claims with their own serials, so that the return scenarios do not change the seeded X6.
    /// </summary>
    [Fact]
    public void The_X9_claim_carries_no_decided_remedy_and_X7_X8_are_claims_of_their_own()
    {
        var seed = SeedFiles.Load(SeedRoot());
        var x9 = seed.Tenants[0].ReplacementScenarios.Single(s => s.Scenario == "X9-aurora");
        var borealis = seed.Tenants[1].ReplacementScenarios;

        (x9.Id, x9.DecidedRemedy).ShouldBe((Guid.Parse("11111111-0006-7000-8000-000000000009"), (DecidedRemedySeed?)null));
        borealis.Select(s => s.Id).ShouldBe(
        [
            Guid.Parse("22222222-0006-7000-8000-000000000006"), Guid.Parse("22222222-0006-7000-8000-000000000007"),
            Guid.Parse("22222222-0006-7000-8000-000000000008"), Guid.Parse("22222222-0006-7000-8000-000000000012"),
        ]);
        borealis.Select(s => s.SerialNumber).Distinct().Count().ShouldBe(4);
    }

    /// <summary>
    /// Feature 003 (T340, quickstart X10 – X12): the US3 claims are X1-like Aurora claims and an X6-like Borealis claim of
    /// their own (own IDs and serials, Replacement decided by a reviewer as their company's other replacement scenarios), so
    /// that proposing, approving and refusing models and recording "not available" change no other scenario's claim. The
    /// catalog serial of the equivalent model that X10 reserves is set aside for it.
    /// </summary>
    [Fact]
    public void The_X10_to_X12_claims_are_claims_of_their_own_with_a_decided_Replacement()
    {
        var seed = SeedFiles.Load(SeedRoot());
        var all = seed.Tenants[0].ReplacementScenarios.Concat(seed.Tenants[1].ReplacementScenarios).ToList();
        var us3 = all.Where(s => s.Scenario is "X10-aurora" or "X11-aurora" or "X12-aurora" or "X12-borealis").ToList();

        us3.Select(s => s.Id).ShouldBe(
        [
            Guid.Parse("11111111-0006-7000-8000-000000000010"), Guid.Parse("11111111-0006-7000-8000-000000000011"),
            Guid.Parse("11111111-0006-7000-8000-000000000012"), Guid.Parse("22222222-0006-7000-8000-000000000012"),
        ]);
        us3.ShouldAllBe(s => s.DecidedRemedy!.Remedy == RemedyType.Replacement);
        all.Select(s => s.SerialNumber).Distinct().Count().ShouldBe(all.Count);
        var unit = seed.Tenants[0].Serials.Single(s => s.SerialNumber == "AT11-25-0001");
        unit.ModelCode.ShouldBe("AUR-TAB11");
        unit.ReservedFor.ShouldNotBeNull().ShouldStartWith("X10-aurora");
    }

    [Fact]
    public void Replacement_scenarios_are_optional()
    {
        var root = CopyOfSeed();
        try
        {
            File.Delete(Path.Combine(root, "tenants", "borealis", "replacement-scenarios.json"));

            var seed = SeedFiles.Load(root);

            seed.Tenants[1].ReplacementScenarios.ShouldBeEmpty();
            seed.Tenants[0].ReplacementScenarios.Count.ShouldBe(6);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("serialNumber", "\"AT10-99-0001\"", "replacement scenario X1-aurora names unknown serial AT10-99-0001")]
    [InlineData("modelCode", "\"AUR-TV55\"", "replacement scenario X1-aurora names unknown model AUR-TV55")]
    [InlineData("customerEmail", "\"nobody@aurora.example.com\"", "replacement scenario X1-aurora names unknown customer")]
    [InlineData("reference", "\"RMDYR1A001\"", "RMDYR1A001 is used by more than one seeded claim")]
    [InlineData("id", "\"11111111-0005-7000-8000-000000000001\"", "is used by more than one seeded claim")]
    [InlineData("scenario", "\"X5\"", "replacement scenario X5 is listed more than once")]
    [InlineData("approvedDaysAgo", "1", "submitted and approved in that order")]
    [InlineData("decidedRemedy", "{\"scenario\":\"RepairFailed\",\"failedAttempts\":2,\"remedy\":\"Repair\",\"reason\":\"Two repair attempts did not fix it.\",\"decidedDaysAgo\":-1}", "replacement scenario X1-aurora needs a decided remedy Replacement")]
    [InlineData("decidedRemedy", "{\"scenario\":\"RepairFailed\",\"failedAttempts\":2,\"remedy\":\"Refund\",\"reason\":\"Two repair attempts did not fix it.\",\"decidedDaysAgo\":-1}", "needs a decided remedy Replacement")]
    [InlineData("decidedRemedy", "{\"scenario\":\"RepairFailed\",\"remedy\":\"Replacement\",\"reason\":\"short\",\"decidedDaysAgo\":-1}", "needs a remedy decision reason")]
    public void An_invalid_replacement_scenario_is_rejected_before_anything_is_written(string field, string json, string message)
    {
        var root = CopyOfSeed();
        try
        {
            var file = Path.Combine(root, "tenants", "aurora", "replacement-scenarios.json");
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
