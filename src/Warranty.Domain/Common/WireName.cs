using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Warranty.Domain.Common;

/// <summary>
/// Maps enum members to the exact names used in data-model.md, the database and the API
/// (e.g. <c>AiDecision.Approve</c> ↔ <c>"APPROVE"</c>). The name comes from
/// <see cref="JsonStringEnumMemberNameAttribute"/> when present, otherwise the member name, so
/// System.Text.Json and persistence agree on one spelling.
/// </summary>
public static class WireName
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => Cache<TEnum>.ToWire.TryGetValue(value, out var wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Undefined {typeof(TEnum).Name} value.");

    public static TEnum Parse<TEnum>(string wire)
        where TEnum : struct, Enum
        => TryParse<TEnum>(wire, out var value)
            ? value
            : throw new FormatException($"'{wire}' is not a valid {typeof(TEnum).Name}.");

    public static bool TryParse<TEnum>(string? wire, out TEnum value)
        where TEnum : struct, Enum
    {
        if (wire is not null && Cache<TEnum>.FromWire.TryGetValue(wire, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static IReadOnlyCollection<string> All<TEnum>()
        where TEnum : struct, Enum
        => Cache<TEnum>.FromWire.Keys;

    private static class Cache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly FrozenDictionary<TEnum, string> ToWire = Build();

        public static readonly FrozenDictionary<string, TEnum> FromWire =
            ToWire.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

        private static FrozenDictionary<TEnum, string> Build()
            => typeof(TEnum)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .ToFrozenDictionary(
                    field => (TEnum)field.GetValue(null)!,
                    field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name);
    }
}
