using System.Globalization;
using System.Text;
using System.Text.Json;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Audit;

/// <summary>
/// Deterministic JSON for hashing: object keys in ordinal order, no whitespace, numbers in one
/// normal form. A payload survives a PostgreSQL <c>jsonb</c> round trip (which reorders keys and
/// rewrites number notation) with the same canonical text, so stored hash chains re-verify.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>Serializes with the persistence JSON format, then canonicalizes; null becomes <c>{}</c>.</summary>
    public static string Serialize(object? value)
        => value is null ? "{}" : Canonicalize(JsonSerializer.SerializeToElement(value, PersistenceJson.Options));

    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    public static string Canonicalize(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                writer.WriteRawValue(
                    decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? number.ToString(CultureInfo.InvariantCulture)
                        : raw);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
