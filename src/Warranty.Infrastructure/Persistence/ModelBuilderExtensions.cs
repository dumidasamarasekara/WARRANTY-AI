using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warranty.Domain.Common;

namespace Warranty.Infrastructure.Persistence;

internal static class ModelBuilderExtensions
{
    /// <summary>Stores an enum as text using its data-model wire name (see <see cref="WireName"/>).</summary>
    public static PropertyBuilder<TEnum> HasWireName<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum
        => property.HasConversion(v => WireName.Of(v), s => WireName.Parse<TEnum>(s)).HasColumnType("text");

    /// <summary>Stores a nullable enum as text using its data-model wire name.</summary>
    public static PropertyBuilder<TEnum?> HasWireName<TEnum>(this PropertyBuilder<TEnum?> property)
        where TEnum : struct, Enum
        => property.HasConversion(
                v => v.HasValue ? WireName.Of(v.Value) : null,
                s => s == null ? null : WireName.Parse<TEnum>(s))
            .HasColumnType("text");

    /// <summary>Stores a value object (usually a read-only list) as jsonb in the shared persistence JSON format.</summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property)
        where T : class
    {
        var comparer = new ValueComparer<T>(
            (a, b) => Serialize(a) == Serialize(b),
            v => Serialize(v).GetHashCode(StringComparison.Ordinal),
            v => Deserialize<T>(Serialize(v)));

        return property
            .HasConversion(v => Serialize(v), s => Deserialize<T>(s), comparer)
            .HasColumnType("jsonb");
    }

    /// <summary>
    /// Snake-cases every column, key, foreign key and index name, and maps <c>*Json</c> string
    /// properties to jsonb columns without the suffix (e.g. <c>PayloadJson</c> → <c>payload</c>).
    /// </summary>
    public static void ApplySnakeCaseNames(this ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == "xmin")
                {
                    continue;
                }

                var name = property.Name;
                if (property.ClrType == typeof(string) && name.EndsWith("Json", StringComparison.Ordinal) && name.Length > 4)
                {
                    name = name[..^4];
                    property.SetColumnType("jsonb");
                }

                property.SetColumnName(ToSnakeCase(name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName() ?? string.Empty));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName() ?? string.Empty));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName() ?? string.Empty));
            }
        }
    }

    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                var previousIsUpper = i > 0 && char.IsUpper(name[i - 1]);
                if (i > 0 && name[i - 1] != '_' && (previousIsLowerOrDigit || (previousIsUpper && nextIsLower)))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, PersistenceJson.Options);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, PersistenceJson.Options)!;
}
