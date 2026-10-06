using System.Globalization;
using System.Text;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Safety;

/// <summary>One injection phrase found in one piece of claimant-supplied text.</summary>
/// <param name="Source">The <see cref="UntrustedTextPart.Label"/> of the text it was found in (e.g. <c>invoice_text EV-2</c>).</param>
/// <param name="Phrase">The matched phrase as listed in the global injection phrase list.</param>
public sealed record InjectionMatch(string Source, string Phrase);

/// <summary>
/// Deterministic prompt-injection detector (FR-019, research R14): normalized phrase matching from the
/// global injection phrase list over the claimant's description, extracted invoice text and text read
/// from photos. Text and phrases are normalized alike (compatibility form, lower case, invisible format
/// characters dropped, every run of punctuation or whitespace collapsed to one space), and a phrase
/// matches only as a sequence of whole words, so case, spacing, line breaks and punctuation do not hide
/// it and "approve a repair" is not "approve".
/// </summary>
public sealed class InjectionDetector
{
    /// <summary>Manifest name of the embedded copy of <c>seed/global/injection-phrases.md</c>.</summary>
    private const string PhraseListResource = "Warranty.AI.Harness.Safety.injection-phrases.md";

    private static readonly Lazy<IReadOnlyList<string>> DefaultPhrases = new(LoadDefaultPhrases);

    private readonly (string Phrase, string Pattern)[] _phrases;

    /// <summary>Creates a detector for the phrases of <c>seed/global/injection-phrases.md</c>.</summary>
    public InjectionDetector(IEnumerable<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        _phrases = phrases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (Phrase: p.Trim(), Pattern: Normalize(p)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Pattern))
            .DistinctBy(p => p.Phrase, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The phrases of the global injection phrase list as the knowledge seed ships it (the file is
    /// embedded in this assembly, so detection needs no retrieval and is the same in every run).
    /// </summary>
    public static IReadOnlyList<string> GlobalPhrases => DefaultPhrases.Value;

    /// <summary>Every listed phrase found in <paramref name="parts"/>, with the label of the part it was found in.</summary>
    public IReadOnlyList<InjectionMatch> Detect(IEnumerable<UntrustedTextPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var matches = new List<InjectionMatch>();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Text))
            {
                continue;
            }

            var text = Normalize(part.Text);
            foreach (var (phrase, pattern) in _phrases)
            {
                var match = new InjectionMatch(part.Label, phrase);
                if (text.Contains(pattern, StringComparison.Ordinal) && !matches.Contains(match))
                {
                    matches.Add(match);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// The phrases of an injection phrase list document: one phrase per line after the YAML front matter.
    /// </summary>
    public static IReadOnlyList<string> ParsePhraseList(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries);
        var start = 0;
        if (lines.Length > 0 && lines[0] == "---")
        {
            var end = Array.IndexOf(lines, "---", 1);
            start = end < 0 ? lines.Length : end + 1;
        }

        return lines.Skip(start).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
    }

    /// <summary>
    /// Lower-case words separated by single spaces, with a leading and trailing space so that a
    /// normalized phrase is found in a normalized text only on word boundaries.
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append(' ');
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark)
                     && builder[^1] != ' ')
            {
                // Zero-width and combining characters are dropped, so they cannot split a word.
                builder.Append(' ');
            }
        }

        if (builder[^1] != ' ')
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> LoadDefaultPhrases()
    {
        using var stream = typeof(InjectionDetector).Assembly.GetManifestResourceStream(PhraseListResource)
                           ?? throw new InvalidOperationException($"Embedded resource '{PhraseListResource}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return ParsePhraseList(reader.ReadToEnd());
    }
}
