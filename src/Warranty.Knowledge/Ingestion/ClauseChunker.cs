using System.Text;
using System.Text.RegularExpressions;

namespace Warranty.Knowledge.Ingestion;

/// <summary>One <c>##</c> section of a source; in a policy its heading starts with the clause key.</summary>
public sealed record KnowledgeSection(string? ClauseKey, string? Title, string Text);

/// <summary>One chunk to embed: a whole section or, for a long one, a run of its paragraphs.</summary>
public sealed record KnowledgeChunkText(int Index, string? ClauseKey, string? SectionTitle, string Text)
{
    /// <summary>The text that is embedded: heading and body, so a query can match either.</summary>
    public string EmbeddingInput
        => string.Join(' ', new[] { ClauseKey, SectionTitle }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } heading
            ? $"{heading}\n\n{Text}"
            : Text;
}

/// <summary>
/// Splits a source body at <c>##</c> headings (contracts/rag.md): one clause per chunk, and a clause
/// longer than <see cref="MaxChunkChars"/> is split at paragraph boundaries with its clause key and
/// title repeated on every part. In a policy the heading's first word is the clause key
/// (<c>## AUR-WP-3.2 Exclusions — accidental damage</c>); other documents have titled sections only.
/// Text before the first heading becomes a section without a heading, except a <c>#</c> title line.
/// </summary>
public static partial class ClauseChunker
{
    /// <summary>About 400 tokens, well inside the embedding model's context.</summary>
    public const int MaxChunkChars = 1_600;

    public static IReadOnlyList<KnowledgeSection> Sections(string body, bool keyedHeadings)
    {
        ArgumentNullException.ThrowIfNull(body);
        var sections = new List<KnowledgeSection>();
        string? heading = null;
        var text = new StringBuilder();

        void Flush()
        {
            var content = text.ToString().Trim();
            text.Clear();
            if (heading is null)
            {
                if (content.Length > 0)
                {
                    sections.Add(new KnowledgeSection(null, null, content));
                }

                return;
            }

            if (!keyedHeadings)
            {
                if (content.Length > 0)
                {
                    sections.Add(new KnowledgeSection(null, heading, content));
                }

                return;
            }

            var space = heading.IndexOfAny([' ', '\t']);
            var key = space < 0 ? heading : heading[..space];
            var title = space < 0 ? string.Empty : heading[(space + 1)..].Trim();
            sections.Add(new KnowledgeSection(key, title.Length > 0 ? title : key, content));
        }

        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (SectionHeading().Match(line) is { Success: true } match)
            {
                Flush();
                heading = match.Groups[1].Value.Trim();
            }
            else if (heading is null && TitleHeading().IsMatch(line))
            {
                // The document title; the front matter already carries it.
            }
            else
            {
                text.Append(line).Append('\n');
            }
        }

        Flush();
        return sections;
    }

    public static IReadOnlyList<KnowledgeChunkText> Chunk(IReadOnlyList<KnowledgeSection> sections, int maxChars = MaxChunkChars)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var chunks = new List<KnowledgeChunkText>();
        foreach (var section in sections)
        {
            var part = new StringBuilder();
            foreach (var paragraph in ParagraphBreak().Split(section.Text).Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                if (part.Length > 0 && part.Length + 2 + paragraph.Length > maxChars)
                {
                    chunks.Add(new KnowledgeChunkText(chunks.Count, section.ClauseKey, section.Title, part.ToString()));
                    part.Clear();
                }

                if (part.Length > 0)
                {
                    part.Append("\n\n");
                }

                part.Append(paragraph);
            }

            if (part.Length > 0)
            {
                chunks.Add(new KnowledgeChunkText(chunks.Count, section.ClauseKey, section.Title, part.ToString()));
            }
        }

        return chunks;
    }

    [GeneratedRegex(@"^##(?!#)\s*(\S.*)$")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^#(?!#)\s")]
    private static partial Regex TitleHeading();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();
}
