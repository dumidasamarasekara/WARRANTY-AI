namespace Warranty.IntegrationTests.Infrastructure;

/// <summary>A staff user of the Keycloak realm (infra/keycloak/warranty-realm.json) as the API sees it.</summary>
public sealed record TestStaffUser(string Username, string Subject, Guid TenantId, IReadOnlyList<string> Roles);

/// <summary>
/// The seven synthetic realm users. Keycloak generates their subjects, so the tests use fixed ones;
/// usernames, tenants and roles match the realm.
/// </summary>
public static class TestStaffUsers
{
    public static readonly Guid AuroraTenantId = Guid.Parse("11111111-1111-7111-8111-111111111111");

    public static readonly Guid BorealisTenantId = Guid.Parse("22222222-2222-7222-8222-222222222222");

    public const string ClaimsAgent = "claims-agent";

    public const string ClaimsReviewer = "claims-reviewer";

    public const string Auditor = "auditor";

    public static readonly TestStaffUser AgentAurora = new("agent.aurora", "0b9e7a52-1f2c-4c1a-9a01-a0000000a001", AuroraTenantId, [ClaimsAgent]);

    public static readonly TestStaffUser ReviewerAurora = new("reviewer.aurora", "0b9e7a52-1f2c-4c1a-9a01-a0000000a002", AuroraTenantId, [ClaimsReviewer]);

    public static readonly TestStaffUser AuditorAurora = new("auditor.aurora", "0b9e7a52-1f2c-4c1a-9a01-a0000000a003", AuroraTenantId, [Auditor]);

    /// <summary>Holds both agent and reviewer roles, for the separation-of-duties checks (research R29).</summary>
    public static readonly TestStaffUser AgentReviewerAurora =
        new("agent-reviewer.aurora", "0b9e7a52-1f2c-4c1a-9a01-a0000000a004", AuroraTenantId, [ClaimsAgent, ClaimsReviewer]);

    public static readonly TestStaffUser AgentBorealis = new("agent.borealis", "0b9e7a52-1f2c-4c1a-9a01-b0000000b001", BorealisTenantId, [ClaimsAgent]);

    public static readonly TestStaffUser ReviewerBorealis = new("reviewer.borealis", "0b9e7a52-1f2c-4c1a-9a01-b0000000b002", BorealisTenantId, [ClaimsReviewer]);

    public static readonly TestStaffUser AuditorBorealis = new("auditor.borealis", "0b9e7a52-1f2c-4c1a-9a01-b0000000b003", BorealisTenantId, [Auditor]);

    public static readonly IReadOnlyList<TestStaffUser> All =
        [AgentAurora, ReviewerAurora, AuditorAurora, AgentReviewerAurora, AgentBorealis, ReviewerBorealis, AuditorBorealis];

    public static TestStaffUser Find(string username)
        => All.SingleOrDefault(user => user.Username == username)
           ?? throw new ArgumentException($"'{username}' is not a seeded staff user.", nameof(username));
}
