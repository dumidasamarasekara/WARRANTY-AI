using Warranty.Domain.Audit;
using Warranty.Domain.Common;
using Warranty.Infrastructure.Audit;
using Warranty.Infrastructure.Persistence.Sql;

namespace Warranty.UnitTests.Infrastructure;

public sealed class AuditTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Canonical_json_sorts_keys_removes_whitespace_and_normalizes_numbers()
        => CanonicalJson.Canonicalize("""{ "b": 1E+2, "a": { "z": 1.50, "y": [3, 2] } }""")
            .ShouldBe("""{"a":{"y":[3,2],"z":1.50},"b":100}""");

    [Fact]
    public void A_jsonb_style_rewrite_of_the_same_payload_has_the_same_canonical_form()
        => CanonicalJson.Canonicalize("""{"x": 1E+3, "aa": "été", "n": null}""")
            .ShouldBe(CanonicalJson.Canonicalize("""{"n": null, "x": 1000, "aa": "été"}"""));

    [Fact]
    public void Serialize_uses_the_persistence_format_and_maps_null_to_an_empty_object()
    {
        CanonicalJson.Serialize(null).ShouldBe("{}");
        CanonicalJson.Serialize(new { Zeta = 1, Alpha = SecurityEventKind.UnknownChannel }).ShouldBe("""{"alpha":"UNKNOWN_CHANNEL","zeta":1}""");
    }

    [Fact]
    public void Timestamps_are_truncated_to_postgres_microseconds()
        => TrailHash.Truncate(Start.AddTicks(12_345_678)).Ticks.ShouldBe(Start.AddTicks(12_345_670).Ticks);

    [Fact]
    public void An_untouched_chain_verifies()
        => HashChainVerifier.Verify(Chain(3)).ShouldBe(HashChainVerification.Valid(3));

    [Fact]
    public void A_changed_entry_breaks_the_chain_at_that_entry()
    {
        var chain = Chain(3);
        var tampered = chain[1];
        chain[1] = DecisionTrailEntry.Create(
            tampered.TenantId, tampered.ClaimId, tampered.Seq, tampered.OccurredAt, tampered.Step, tampered.Actor,
            "Approved without review", tampered.PayloadJson, tampered.CorrelationId, tampered.PrevHash, tampered.Hash);

        var result = HashChainVerifier.Verify(chain);

        result.IsValid.ShouldBeFalse();
        result.FirstBrokenSeq.ShouldBe(2);
    }

    [Fact]
    public void A_missing_entry_or_a_relinked_entry_is_reported()
    {
        var chain = Chain(3);
        HashChainVerifier.Verify([chain[0], chain[2]]).FirstBrokenSeq.ShouldBe(2);

        var rehashedWithoutLink = Entry(2, TrailHash.Genesis);
        HashChainVerifier.Verify([chain[0], rehashedWithoutLink]).FirstBrokenSeq.ShouldBe(2);
    }

    [Fact]
    public void The_operator_event_function_accepts_only_operator_kinds_and_is_execute_only()
    {
        var sql = SqlScripts.Load(SqlDatabase.Warranty).Where(s => s.Name == "005_operator_events.sql").ShouldHaveSingleItem().Sql;

        sql.ShouldContain("SECURITY DEFINER");
        sql.ShouldContain("kind NOT IN ('UNKNOWN_CHANNEL', 'CROSS_TENANT_ACCESS_DENIED')");
        sql.ShouldContain("GRANT EXECUTE ON FUNCTION audit.record_operator_event(text, text, text, jsonb, text) TO warranty_app");
    }

    [Fact]
    public void The_cross_tenant_functions_are_security_definer_and_execute_only_and_write_through_the_operator_function()
    {
        var sql = SqlScripts.Load(SqlDatabase.Warranty).Where(s => s.Name == "003_security_functions.sql").ShouldHaveSingleItem().Sql;

        sql.ShouldContain("SECURITY DEFINER");
        sql.ShouldContain("REVOKE ALL ON FUNCTION audit.exists_in_other_tenant(text, uuid, uuid) FROM PUBLIC");
        sql.ShouldContain("GRANT EXECUTE ON FUNCTION audit.exists_in_other_tenant(text, uuid, uuid) TO warranty_app");
        sql.ShouldContain("GRANT EXECUTE ON FUNCTION audit.record_cross_tenant_denial(text, uuid, uuid, text, text, text) TO warranty_app");
        sql.ShouldContain("PERFORM audit.record_operator_event(");
    }

    private static List<DecisionTrailEntry> Chain(int length)
    {
        var entries = new List<DecisionTrailEntry>();
        var prev = TrailHash.Genesis;
        for (var seq = 1; seq <= length; seq++)
        {
            var entry = Entry(seq, prev);
            entries.Add(entry);
            prev = entry.Hash;
        }

        return entries;
    }

    private static DecisionTrailEntry Entry(int seq, string prevHash)
    {
        var occurredAt = Start.AddSeconds(seq);
        var payload = CanonicalJson.Serialize(new { Seq = seq, Detail = "step" });
        var hash = TrailHash.Compute(prevHash, Tenant, ClaimId, seq, occurredAt, TrailStep.ClaimSubmitted, "system", $"Step {seq}", payload, "corr");
        return DecisionTrailEntry.Create(Tenant, ClaimId, seq, occurredAt, TrailStep.ClaimSubmitted, "system", $"Step {seq}", payload, "corr", prevHash, hash);
    }
}
