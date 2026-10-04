using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Warranty.EvidenceGenerator;

internal static class Cli
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "illegible" };

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("usage: Warranty.EvidenceGenerator generate --spec <file.json> [--only S1,S2] [--as-of yyyy-MM-dd]");
            Console.WriteLine("       Warranty.EvidenceGenerator photo --out <file.jpg> [options]");
            Console.WriteLine("       Warranty.EvidenceGenerator invoice --out <file.pdf> [options]");
            Console.WriteLine("See the header of tools/Warranty.EvidenceGenerator/Program.cs for every option.");
            return args.Length == 0 ? 1 : 0;
        }

        var options = Parse(args.Skip(1).ToArray());
        var asOf = options.Remove("asOf", out var asOfText)
            ? DateOnly.ParseExact(asOfText!.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateOnly.FromDateTime(DateTime.UtcNow);

        switch (args[0])
        {
            case "generate":
            {
                var spec = options.Remove("spec", out var specPath) ? specPath!.ToString() : throw new ArgumentException("generate needs --spec <file.json>.");
                var only = options.Remove("only", out var onlyText)
                    ? onlyText!.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                RejectUnknown(options);
                var root = Path.GetDirectoryName(Path.GetFullPath(spec))!;
                var items = EvidenceSpec.Load(spec).Files
                    .Where(i => only is null || (i.Scenario is not null && only.Contains(i.Scenario)))
                    .ToList();
                foreach (var item in items)
                {
                    Generate(item, Path.Combine(root, item.Path), asOf);
                }

                Console.WriteLine($"Generated {items.Count} file(s) as of {asOf:yyyy-MM-dd}.");
                return 0;
            }

            case "photo":
            case "invoice":
            {
                options["kind"] = args[0];
                if (options.Remove("out", out var output))
                {
                    options["path"] = output!.ToString();
                }

                var item = options.Deserialize<EvidenceItem>(EvidenceSpec.JsonOptions)!;
                Generate(item, Path.GetFullPath(item.Path), asOf);
                return 0;
            }

            default:
                throw new ArgumentException($"Unknown command '{args[0]}' (expected generate, photo or invoice).");
        }
    }

    private static void Generate(EvidenceItem item, string outputPath, DateOnly asOf)
    {
        if (item.Validate() is { Count: > 0 } errors)
        {
            throw new InvalidDataException($"{item.Path}: {string.Join(" ", errors)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (item.Kind == "photo")
        {
            PhotoRenderer.Render(item, outputPath);
        }
        else
        {
            InvoiceRenderer.Render(item, outputPath, asOf);
        }

        Console.WriteLine($"{item.Path}  ({new FileInfo(outputPath).Length / 1024} KB)");
    }

    /// <summary>Turns <c>--kebab-case value</c> pairs into a JSON object with camelCase keys (the spec item shape).</summary>
    private static JsonObject Parse(string[] args)
    {
        var result = new JsonObject();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            var key = CamelCase(args[i][2..]);
            if (Flags.Contains(key))
            {
                result[key] = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{args[i]}' needs a value.");
            }

            var value = args[++i];
            result[key] = key switch
            {
                // Multi-line texts accept a literal \n as the line break.
                "sellerAddress" or "billTo" or "note" or "sticker" => value.Replace("\\n", "\n", StringComparison.Ordinal),
                "damage" => new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => (JsonNode)d).ToArray()),
                "variant" or "width" or "height" or "quality" or "purchaseDateOffsetMonths" => int.Parse(value, CultureInfo.InvariantCulture),
                "amount" => decimal.Parse(value, CultureInfo.InvariantCulture),
                _ => value,
            };
        }

        return result;
    }

    private static void RejectUnknown(JsonObject options)
    {
        if (options.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s) for generate: {string.Join(", ", options.Select(o => o.Key))}.");
        }
    }

    private static string CamelCase(string kebab)
    {
        var parts = kebab.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select((p, i) => i == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
