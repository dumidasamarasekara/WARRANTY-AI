using Warranty.AI.Harness.Context;
using Warranty.Application.Abstractions.Knowledge;

namespace Warranty.UnitTests.Harness;

/// <summary>
/// Harness-issued reference IDs (research R7, FR-022): models may cite only <c>EV-n</c> and <c>POL-n</c>
/// issued for the run; <c>GLB-n</c> is context only and never accepted as a policy reference.
/// </summary>
public sealed class ReferenceRegistryTests
{
    private const string AuroraNamespace = "tenant-aurora";
    private const string GlobalNamespace = "global";

    private static RetrievedChunk Chunk(string knowledgeNamespace, string clauseKey = "AUR-WP-3.2")
        => new(Guid.NewGuid(), knowledgeNamespace, Guid.NewGuid(), "Aurora Limited Warranty", 1, clauseKey, "Coverage", "text", null, null, 0.8);

    [Fact]
    public void Issued_evidence_reference_resolves_to_its_evidence_file()
    {
        var registry = new ReferenceRegistry();
        var invoice = Guid.NewGuid();
        var photo = Guid.NewGuid();

        registry.IssueEvidence(invoice).ShouldBe("EV-1");
        registry.IssueEvidence(photo).ShouldBe("EV-2");

        var entry = registry.Resolve("EV-2", ReferenceKind.Evidence);
        entry.Kind.ShouldBe(ReferenceKind.Evidence);
        entry.TargetId.ShouldBe(photo);
        entry.Chunk.ShouldBeNull();
        registry.Validate(["EV-1", "EV-2"], ReferenceKind.Evidence).ShouldBeEmpty();
    }

    [Fact]
    public void Issued_policy_reference_resolves_to_its_chunk()
    {
        var registry = new ReferenceRegistry();
        var chunk = Chunk(AuroraNamespace);

        registry.IssueChunk(chunk).ShouldBe("POL-1");

        var entry = registry.Resolve("POL-1", ReferenceKind.Policy);
        entry.Kind.ShouldBe(ReferenceKind.Policy);
        entry.TargetId.ShouldBe(chunk.ChunkId);
        entry.Chunk.ShouldBe(chunk);
        registry.Validate(["POL-1"], ReferenceKind.Policy).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("EV-3", ReferenceKind.Evidence)]
    [InlineData("POL-2", ReferenceKind.Policy)]
    [InlineData("GLB-1", ReferenceKind.Global)]
    public void Well_formed_reference_that_was_not_issued_is_rejected(string id, ReferenceKind kind)
    {
        var registry = new ReferenceRegistry();
        registry.IssueEvidence(Guid.NewGuid());
        registry.IssueEvidence(Guid.NewGuid());
        registry.IssueChunk(Chunk(AuroraNamespace));

        registry.Validate([id], kind).ShouldBe([$"{id} was not issued for this run."]);
        registry.TryResolve(id, out _).ShouldBeFalse();
        Should.Throw<UnknownReferenceException>(() => registry.Resolve(id, kind)).Reference.ShouldBe(id);
    }

    [Fact]
    public void Global_snippet_is_issued_as_glb_and_never_accepted_as_a_policy_reference()
    {
        var registry = new ReferenceRegistry();
        var snippet = Chunk(GlobalNamespace, clauseKey: "GLOSSARY-1");

        registry.IssueChunk(snippet).ShouldBe("GLB-1");

        registry.Validate(["GLB-1"], ReferenceKind.Policy).ShouldBe(["GLB-1 is not a POL-n reference."]);
        Should.Throw<UnknownReferenceException>(() => registry.Resolve("GLB-1", ReferenceKind.Policy)).Reference.ShouldBe("GLB-1");
        registry.Resolve("GLB-1", ReferenceKind.Global).TargetId.ShouldBe(snippet.ChunkId);
    }

    [Fact]
    public void Global_snippet_does_not_consume_a_policy_number()
    {
        var registry = new ReferenceRegistry();

        registry.IssueChunk(Chunk(GlobalNamespace)).ShouldBe("GLB-1");
        registry.IssueChunk(Chunk(AuroraNamespace)).ShouldBe("POL-1");
        registry.IssueChunk(Chunk(GlobalNamespace)).ShouldBe("GLB-2");

        registry.Validate(["POL-1"], ReferenceKind.Policy).ShouldBeEmpty();
        registry.Validate(["POL-2"], ReferenceKind.Policy).ShouldBe(["POL-2 was not issued for this run."]);
    }

    [Fact]
    public void Evidence_and_policy_references_are_not_interchangeable()
    {
        var registry = new ReferenceRegistry();
        registry.IssueEvidence(Guid.NewGuid());
        registry.IssueChunk(Chunk(AuroraNamespace));

        registry.Validate(["EV-1"], ReferenceKind.Policy).ShouldBe(["EV-1 is not a POL-n reference."]);
        registry.Validate(["POL-1"], ReferenceKind.Evidence).ShouldBe(["POL-1 is not a EV-n reference."]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("EV-0")]
    [InlineData("EV-01")]
    [InlineData("ev-1")]
    [InlineData("EV1")]
    [InlineData("EV-")]
    [InlineData(" EV-1")]
    [InlineData("EV-1 ")]
    [InlineData("EV-1234567")]
    [InlineData("DOC-1")]
    [InlineData("chunk:0199a000-0000-7000-8000-000000000001")]
    public void Malformed_reference_is_rejected_as_not_a_reference_id(string id)
    {
        var registry = new ReferenceRegistry();
        registry.IssueEvidence(Guid.NewGuid());

        registry.Validate([id], ReferenceKind.Evidence).ShouldBe([$"'{id}' is not a reference ID."]);
        Should.Throw<UnknownReferenceException>(() => registry.Resolve(id, ReferenceKind.Evidence));
    }

    [Fact]
    public void Database_id_of_an_issued_target_is_not_accepted_in_place_of_its_reference()
    {
        var registry = new ReferenceRegistry();
        var evidence = Guid.NewGuid();
        registry.IssueEvidence(evidence);

        registry.Validate([evidence.ToString()], ReferenceKind.Evidence).ShouldBe([$"'{evidence}' is not a reference ID."]);
        registry.TryResolve(evidence.ToString(), out _).ShouldBeFalse();
    }

    [Fact]
    public void Validate_reports_one_error_per_invalid_id_in_input_order()
    {
        var registry = new ReferenceRegistry();
        registry.IssueEvidence(Guid.NewGuid());
        registry.IssueChunk(Chunk(AuroraNamespace));
        registry.IssueChunk(Chunk(GlobalNamespace));

        registry.Validate(["POL-1", "GLB-1", "POL-9", "POL-1", "EV-1"], ReferenceKind.Policy).ShouldBe(
        [
            "GLB-1 is not a POL-n reference.",
            "POL-9 was not issued for this run.",
            "EV-1 is not a POL-n reference.",
        ]);
    }

    [Fact]
    public void References_issued_in_one_run_are_unknown_in_another()
    {
        var first = new ReferenceRegistry();
        first.IssueEvidence(Guid.NewGuid());
        first.IssueChunk(Chunk(AuroraNamespace));

        var second = new ReferenceRegistry();

        second.Validate(["EV-1"], ReferenceKind.Evidence).ShouldBe(["EV-1 was not issued for this run."]);
        second.Validate(["POL-1"], ReferenceKind.Policy).ShouldBe(["POL-1 was not issued for this run."]);
    }

    [Fact]
    public void Issuing_the_same_target_again_returns_the_existing_reference()
    {
        var registry = new ReferenceRegistry();
        var evidence = Guid.NewGuid();
        var chunk = Chunk(AuroraNamespace);

        registry.IssueEvidence(evidence).ShouldBe("EV-1");
        registry.IssueChunk(chunk).ShouldBe("POL-1");
        registry.IssueEvidence(evidence).ShouldBe("EV-1");
        registry.IssueChunk(chunk).ShouldBe("POL-1");

        registry.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public void Reference_needs_a_target()
        => Should.Throw<ArgumentException>(() => new ReferenceRegistry().IssueEvidence(Guid.Empty));

    [Fact]
    public void Entries_are_ordered_by_kind_and_numerically()
    {
        var registry = new ReferenceRegistry();
        registry.IssueChunk(Chunk(GlobalNamespace));
        for (var i = 0; i < 10; i++)
        {
            registry.IssueEvidence(Guid.NewGuid());
        }

        registry.IssueChunk(Chunk(AuroraNamespace));

        registry.Entries.Select(e => e.Id).ShouldBe(
            ["EV-1", "EV-2", "EV-3", "EV-4", "EV-5", "EV-6", "EV-7", "EV-8", "EV-9", "EV-10", "POL-1", "GLB-1"]);
    }

    [Fact]
    public void Restored_registry_accepts_the_same_references_and_rejects_others()
    {
        var registry = new ReferenceRegistry();
        var evidence = Guid.NewGuid();
        var policy = Chunk(AuroraNamespace);
        registry.IssueEvidence(evidence);
        registry.IssueChunk(policy);
        registry.IssueChunk(Chunk(GlobalNamespace));

        var restored = ReferenceRegistry.FromMap(registry.ToMap());

        restored.Resolve("EV-1", ReferenceKind.Evidence).TargetId.ShouldBe(evidence);
        restored.Resolve("POL-1", ReferenceKind.Policy).TargetId.ShouldBe(policy.ChunkId);
        restored.Validate(["GLB-1"], ReferenceKind.Policy).ShouldBe(["GLB-1 is not a POL-n reference."]);
        restored.Validate(["EV-2"], ReferenceKind.Evidence).ShouldBe(["EV-2 was not issued for this run."]);
    }

    [Fact]
    public void Restored_reference_regains_its_chunk_when_the_chunk_is_issued_again()
    {
        var registry = new ReferenceRegistry();
        var policy = Chunk(AuroraNamespace);
        registry.IssueChunk(policy);

        var restored = ReferenceRegistry.FromMap(registry.ToMap());
        restored.Resolve("POL-1", ReferenceKind.Policy).Chunk.ShouldBeNull();

        restored.IssueChunk(policy).ShouldBe("POL-1");
        restored.Resolve("POL-1", ReferenceKind.Policy).Chunk.ShouldBe(policy);
    }

    [Theory]
    [InlineData("EV-0")]
    [InlineData("chunk:1")]
    [InlineData("")]
    public void Restoring_a_map_with_a_malformed_key_fails(string id)
        => Should.Throw<ArgumentException>(() => ReferenceRegistry.FromMap(new Dictionary<string, Guid> { [id] = Guid.NewGuid() }));

    [Fact]
    public void Concurrent_issuing_hands_out_unique_consecutive_numbers()
    {
        var registry = new ReferenceRegistry();
        var targets = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();

        var ids = new string[targets.Length];
        Parallel.For(0, targets.Length, i => ids[i] = registry.IssueEvidence(targets[i]));

        ids.Distinct().Count().ShouldBe(targets.Length);
        ids.Select(id => int.Parse(id[3..], System.Globalization.CultureInfo.InvariantCulture)).Order()
            .ShouldBe(Enumerable.Range(1, targets.Length));
        for (var i = 0; i < targets.Length; i++)
        {
            registry.Resolve(ids[i], ReferenceKind.Evidence).TargetId.ShouldBe(targets[i]);
        }
    }
}
