using System.Globalization;
using System.Text.RegularExpressions;
using Warranty.Application.Abstractions.Knowledge;

namespace Warranty.AI.Harness.Context;

/// <summary>What a harness-issued reference points to.</summary>
public enum ReferenceKind
{
    /// <summary><c>EV-n</c>: an evidence file of the claim.</summary>
    Evidence,

    /// <summary><c>POL-n</c>: a tenant policy chunk; citable as policy.</summary>
    Policy,

    /// <summary><c>GLB-n</c>: a global knowledge snippet; context only, never citable as policy.</summary>
    Global,
}

/// <summary>An issued reference and the row it stands for; <see cref="Chunk"/> is set for chunks issued in this process.</summary>
public sealed record ReferenceEntry(string Id, ReferenceKind Kind, Guid TargetId, RetrievedChunk? Chunk = null);

/// <summary>A reference ID that was not issued for this run, or not of the expected kind.</summary>
public sealed class UnknownReferenceException(string reference, string message) : Exception(message)
{
    public string Reference { get; } = reference;
}

/// <summary>
/// Issues and resolves the run's reference IDs (research R7, FR-022). Models only ever see
/// <c>EV-n</c>, <c>POL-n</c> and <c>GLB-n</c>, never database IDs, so they cannot point at anything the
/// run did not show them. Issuing the same target again returns its existing ID. Any ID that was not
/// issued — or is of the wrong kind, such as a <c>GLB-n</c> cited as policy — is rejected. The map is
/// stored in <c>adjudication_runs.reference_map</c> and restored when a run resumes. Thread-safe,
/// because the Evidence and Policy steps run in parallel.
/// </summary>
public sealed partial class ReferenceRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReferenceEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(ReferenceKind, Guid), string> _byTarget = [];
    private readonly Dictionary<ReferenceKind, int> _counters = new() { [ReferenceKind.Evidence] = 0, [ReferenceKind.Policy] = 0, [ReferenceKind.Global] = 0 };

    /// <summary>Every issued reference, in kind and number order.</summary>
    public IReadOnlyList<ReferenceEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _byId.Values.OrderBy(e => e.Kind).ThenBy(e => Number(e.Id)).ToList();
            }
        }
    }

    public static string PrefixOf(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Evidence => "EV",
        ReferenceKind.Policy => "POL",
        ReferenceKind.Global => "GLB",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown reference kind."),
    };

    /// <summary>Restores a registry from a stored <c>reference_map</c>; numbering continues after the highest ID per kind.</summary>
    public static ReferenceRegistry FromMap(IReadOnlyDictionary<string, Guid> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var registry = new ReferenceRegistry();
        foreach (var (id, target) in map)
        {
            var kind = Parse(id) ?? throw new ArgumentException($"'{id}' is not a reference ID.", nameof(map));
            registry.Add(new ReferenceEntry(id, kind, target));
            registry._counters[kind] = Math.Max(registry._counters[kind], Number(id));
        }

        return registry;
    }

    /// <summary>Issues (or returns) the <c>EV-n</c> of an evidence file.</summary>
    public string IssueEvidence(Guid evidenceId) => Issue(ReferenceKind.Evidence, evidenceId, null);

    /// <summary>Issues (or returns) <c>GLB-n</c> for a <c>global</c> chunk and <c>POL-n</c> for any other chunk.</summary>
    public string IssueChunk(RetrievedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var kind = chunk.Namespace == "global" ? ReferenceKind.Global : ReferenceKind.Policy;
        return Issue(kind, chunk.ChunkId, chunk);
    }

    public bool TryResolve(string id, out ReferenceEntry entry)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id ?? string.Empty, out entry!);
        }
    }

    /// <summary>Resolves an issued ID of the expected kind; throws <see cref="UnknownReferenceException"/> otherwise.</summary>
    public ReferenceEntry Resolve(string id, ReferenceKind expected)
    {
        if (Check(id, expected) is { } error)
        {
            throw new UnknownReferenceException(id, error);
        }

        TryResolve(id, out var entry);
        return entry;
    }

    /// <summary>One error per ID that was not issued or is not of the expected kind; empty when all are valid.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<string> ids, ReferenceKind expected)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Select(id => Check(id, expected)).OfType<string>().ToList();
    }

    /// <summary>The map for <c>adjudication_runs.reference_map</c>.</summary>
    public IReadOnlyDictionary<string, Guid> ToMap() => Entries.ToDictionary(e => e.Id, e => e.TargetId, StringComparer.Ordinal);

    private string Issue(ReferenceKind kind, Guid targetId, RetrievedChunk? chunk)
    {
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("A reference needs a target.", nameof(targetId));
        }

        lock (_gate)
        {
            if (_byTarget.TryGetValue((kind, targetId), out var existing))
            {
                if (chunk is not null && _byId[existing].Chunk is null)
                {
                    _byId[existing] = _byId[existing] with { Chunk = chunk };
                }

                return existing;
            }

            var id = string.Create(CultureInfo.InvariantCulture, $"{PrefixOf(kind)}-{++_counters[kind]}");
            Add(new ReferenceEntry(id, kind, targetId, chunk));
            return id;
        }
    }

    private void Add(ReferenceEntry entry)
    {
        _byId.Add(entry.Id, entry);
        _byTarget[(entry.Kind, entry.TargetId)] = entry.Id;
    }

    private string? Check(string? id, ReferenceKind expected)
    {
        if (string.IsNullOrWhiteSpace(id) || Parse(id) is not { } kind)
        {
            return $"'{id}' is not a reference ID.";
        }

        lock (_gate)
        {
            if (!_byId.ContainsKey(id))
            {
                return $"{id} was not issued for this run.";
            }
        }

        return kind == expected ? null : $"{id} is not a {PrefixOf(expected)}-n reference.";
    }

    private static ReferenceKind? Parse(string id)
    {
        var match = ReferencePattern().Match(id);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["prefix"].Value switch
        {
            "EV" => ReferenceKind.Evidence,
            "POL" => ReferenceKind.Policy,
            _ => ReferenceKind.Global,
        };
    }

    private static int Number(string id) => int.Parse(id[(id.IndexOf('-', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture);

    [GeneratedRegex("^(?<prefix>EV|POL|GLB)-[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
