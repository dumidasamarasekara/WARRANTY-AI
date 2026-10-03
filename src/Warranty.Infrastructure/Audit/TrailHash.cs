using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Warranty.Domain.Common;

namespace Warranty.Infrastructure.Audit;

/// <summary>
/// <c>hash = SHA-256(prev_hash ‖ canonical_json(entry))</c> for decision trail entries (FR-039),
/// shared by the writer and the verifier so both hash exactly the same bytes.
/// </summary>
internal static class TrailHash
{
    /// <summary><c>prev_hash</c> of the first entry of every claim.</summary>
    public static readonly string Genesis = new('0', 64);

    /// <summary>PostgreSQL stores microseconds; hashing the stored precision keeps the chain verifiable.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    public static string Compute(
        string prevHash, Guid tenantId, Guid claimId, int seq, DateTimeOffset occurredAt, TrailStep step, string actor,
        string summary, string canonicalPayload, string correlationId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Keys in ordinal order (canonical form).
            writer.WriteStartObject();
            writer.WriteString("actor", actor);
            writer.WriteString("claimId", claimId);
            writer.WriteString("correlationId", correlationId);
            writer.WriteString("occurredAt", Truncate(occurredAt).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WritePropertyName("payload");
            writer.WriteRawValue(canonicalPayload);
            writer.WriteNumber("seq", seq);
            writer.WriteString("step", WireName.Of(step));
            writer.WriteString("summary", summary);
            writer.WriteString("tenantId", tenantId);
            writer.WriteEndObject();
        }

        var bytes = Encoding.UTF8.GetBytes(prevHash).Concat(buffer.ToArray()).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
