using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Warranty.Domain.Policies;

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
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitDerivedRemedyProperties } },
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// <c>policy_versions.remedy_rules</c> holds exactly the <c>remedies</c> block of
    /// contracts/remedy-rules.schema.json: the computed <see cref="RemedyChoice.AllowedRemedies"/> and
    /// <see cref="RemedyChoice.NeedsReviewer"/> are not stored.
    /// </summary>
    private static void OmitDerivedRemedyProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(RemedyChoice).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].Name is "allowedRemedies" or "needsReviewer")
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}
