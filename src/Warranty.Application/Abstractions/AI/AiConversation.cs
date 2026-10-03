using System.Text.Json;

namespace Warranty.Application.Abstractions.AI;

public enum AiRole
{
    User,
    Assistant,
}

/// <summary>One provider-neutral message.</summary>
public sealed record AiMessage(AiRole Role, IReadOnlyList<AiContentPart> Parts);

/// <summary>A provider-neutral content part.</summary>
public abstract record AiContentPart;

public sealed record TextPart(string Text) : AiContentPart;

/// <summary>Claimant-supplied text; the gateway wraps it in <c>&lt;untrusted_claim_content&gt;</c> (FR-019, research R14).</summary>
public sealed record UntrustedTextPart(string Label, string Text) : AiContentPart;

public sealed record ImagePart(string EvidenceRef, ReadOnlyMemory<byte> Data, string MediaType) : AiContentPart;

public sealed record DocumentPart(string EvidenceRef, ReadOnlyMemory<byte> PdfData) : AiContentPart;

public sealed record ToolCallPart(string CallId, string ToolName, JsonElement Arguments) : AiContentPart;

public sealed record ToolResultPart(string CallId, JsonElement Result, bool IsError) : AiContentPart;

/// <summary>A provider block (e.g. a signed thinking block) echoed back unchanged; never inspected or edited.</summary>
public sealed record ProviderOpaquePart(string Provider, JsonElement Payload) : AiContentPart;

/// <summary>
/// Append-only conversation: messages can be added but never edited or removed, so assistant turns
/// (including opaque provider blocks) are always echoed back exactly as returned.
/// </summary>
public sealed class AiConversation
{
    private readonly List<AiMessage> _messages = [];

    public IReadOnlyList<AiMessage> Messages => _messages;

    public void AddUser(params AiContentPart[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("A user message needs at least one part.", nameof(parts));
        }

        _messages.Add(new AiMessage(AiRole.User, parts.ToArray()));
    }

    /// <summary>Appends the assistant message exactly as the gateway returned it.</summary>
    public void AddAssistant(AiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != AiRole.Assistant)
        {
            throw new ArgumentException("Only assistant messages can be added here.", nameof(message));
        }

        _messages.Add(message);
    }

    /// <summary>Appends every tool result of one turn as a single user message.</summary>
    public void AddToolResults(IReadOnlyList<AiToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new ArgumentException("At least one tool result is required.", nameof(results));
        }

        _messages.Add(new AiMessage(
            AiRole.User,
            results.Select(r => (AiContentPart)new ToolResultPart(r.CallId, r.Result, r.IsError)).ToArray()));
    }
}
