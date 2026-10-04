namespace Warranty.AI.Harness.Agents;

/// <summary>
/// Agent names (contracts/agents-and-tools.md). They are the <see cref="Execution.AgentDescriptor.Name"/>
/// of each agent and the caller names that tool allow-lists are checked against.
/// </summary>
public static class AgentNames
{
    public const string Intake = "intake";

    public const string Evidence = "evidence";

    public const string Policy = "policy";

    public const string Decision = "decision";

    /// <summary>The risk capability; not an LLM agent in the PoC, it calls <c>claim_history_lookup</c> directly.</summary>
    public const string Risk = "risk";
}
