using System.Text;
using SkiaSharp;

namespace Warranty.EvidenceGenerator;

/// <summary>Fonts, seeded variation, noise and encoding shared by the renderers.</summary>
internal static class Rendering
{
    public static readonly SKTypeface Sans = Typeface(SKFontStyle.Normal, "Arial", "Helvetica", "DejaVu Sans", "Liberation Sans");

    public static readonly SKTypeface SansBold = Typeface(SKFontStyle.Bold, "Arial", "Helvetica", "DejaVu Sans", "Liberation Sans");

    public static readonly SKTypeface Mono = Typeface(SKFontStyle.Normal, "Consolas", "Courier New", "DejaVu Sans Mono", "Liberation Mono");

    public static readonly SKTypeface Hand = Typeface(SKFontStyle.Normal, "Segoe Print", "Comic Sans MS", "Bradley Hand", "DejaVu Sans");

    /// <summary>
    /// A random source seeded from the output file name and the variant, so each file differs from every
    /// other (no accidental evidence-reuse matches) while a re-run with the same spec draws the same picture.
    /// </summary>
    public static Random Seeded(EvidenceItem item)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes($"{System.IO.Path.GetFileName(item.Path)}|{item.Scenario}|{item.Variant}"))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return new Random((int)(hash & 0x7FFFFFFF));
    }

    public static SKFont Font(SKTypeface typeface, float size) => new(typeface, size) { Edging = SKFontEdging.Antialias };

    public static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    public static SKPaint Stroke(SKColor color, float width) => new()
    {
        Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };

    public static float Between(this Random random, float min, float max) => min + ((float)random.NextDouble() * (max - min));

    /// <summary>
    /// Draws text as glyph outlines. Used for PDFs: this Skia build embeds whole font files (≈0.5 MB each)
    /// instead of subsets, while outlines keep an invoice at a few kilobytes and look identical.
    /// </summary>
    public static void DrawGlyphs(this SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        var width = font.MeasureText(text);
        var left = align switch
        {
            SKTextAlign.Right => x - width,
            SKTextAlign.Center => x - (width / 2),
            _ => x,
        };
        using var path = font.GetTextPath(text, new SKPoint(left, y));
        canvas.DrawPath(path, paint);
    }

    public static void DrawPath(this SKCanvas canvas, SKPathBuilder builder, SKPaint paint)
    {
        using var path = builder.Snapshot();
        canvas.DrawPath(path, paint);
    }

    /// <summary>Nudges a few thousand pixels by a small amount: invisible, but makes the file hash unique to the seed.</summary>
    public static void AddNoise(SKBitmap bitmap, Random random)
    {
        var count = bitmap.Width * bitmap.Height / 150;
        for (var i = 0; i < count; i++)
        {
            var x = random.Next(bitmap.Width);
            var y = random.Next(bitmap.Height);
            var c = bitmap.GetPixel(x, y);
            var d = random.Next(-6, 7);
            bitmap.SetPixel(x, y, new SKColor(Clamp(c.Red + d), Clamp(c.Green + d), Clamp(c.Blue + d), c.Alpha));
        }
    }

    /// <summary>Encodes by extension: JPEG/WebP at <paramref name="quality"/>, PNG lossless.</summary>
    public static void Save(SKBitmap bitmap, string path, int quality)
    {
        var format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };

        using var data = bitmap.Encode(format, quality);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>Word-wraps <paramref name="text"/> to lines no wider than <paramref name="width"/>.</summary>
    public static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && font.MeasureText(candidate) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static SKTypeface Typeface(SKFontStyle style, params string[] families)
    {
        foreach (var family in families)
        {
            var typeface = SKTypeface.FromFamilyName(family, style);
            if (typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return typeface;
            }
        }

        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }
}
