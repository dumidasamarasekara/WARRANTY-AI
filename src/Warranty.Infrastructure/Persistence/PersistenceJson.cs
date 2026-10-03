using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warranty.Infrastructure.Persistence;

/// <summary>
/// The single JSON format of every jsonb column: camelCase names and enum values as their
/// data-model wire names (e.g. <c>"DUPLICATE_SERIAL_CLAIM"</c>), matching the API and contracts.
/// </summary>
internal static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
