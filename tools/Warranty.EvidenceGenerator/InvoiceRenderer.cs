using System.Globalization;
using SkiaSharp;

namespace Warranty.EvidenceGenerator;

/// <summary>
/// Draws a one-page sales invoice (US Letter, 612×792 pt): seller, invoice number and date, the claimed
/// product line with model code and serial, total, and an optional free-text note line. A legible PDF
/// is vector (text drawn as glyph outlines); the illegible variant is a blurred, low-contrast raster so
/// nothing can be read.
/// </summary>
internal static class InvoiceRenderer
{
    private const float PageWidth = 612;
    private const float PageHeight = 792;

    /// <summary>Raster scale for image output and illegible PDFs.</summary>
    private const float RasterScale = 1.25f;

    public static void Render(EvidenceItem item, string outputPath, DateOnly asOf)
    {
        var random = Rendering.Seeded(item);
        var date = item.InvoiceDate(asOf);
        var isPdf = string.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase);

        if (isPdf && !item.Illegible)
        {
            using var stream = File.Create(outputPath);
            using var document = SKDocument.CreatePdf(stream, Metadata(item, date));
            var canvas = document.BeginPage(PageWidth, PageHeight);
            DrawPage(canvas, item, date, random, lowContrast: false);
            document.EndPage();
            document.Close();
            return;
        }

        using var bitmap = Raster(item, date, random);
        if (!isPdf)
        {
            Rendering.Save(bitmap, outputPath, item.Quality);
            return;
        }

        using (var stream = File.Create(outputPath))
        using (var document = SKDocument.CreatePdf(stream, Metadata(item, date)))
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, new SKRect(0, 0, PageWidth, PageHeight), new SKSamplingOptions(SKFilterMode.Linear));
            document.EndPage();
            document.Close();
        }
    }

    private static SKDocumentPdfMetadata Metadata(EvidenceItem item, DateOnly date) => new()
    {
        Title = $"Invoice {item.InvoiceNumber}",
        Author = item.Seller,
        Creator = "Warranty.EvidenceGenerator (synthetic)",
        Producer = "Warranty.EvidenceGenerator",
        Creation = date.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc),
        Modified = date.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc),
        RasterDpi = 150,
        EncodingQuality = 85,
    };

    private static SKBitmap Raster(EvidenceItem item, DateOnly date, Random random)
    {
        var bitmap = new SKBitmap((int)(PageWidth * RasterScale), (int)(PageHeight * RasterScale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        if (!item.Illegible)
        {
            canvas.Scale(RasterScale);
            DrawPage(canvas, item, date, random, lowContrast: false);
            canvas.Flush();
            Rendering.AddNoise(bitmap, random);
            return bitmap;
        }

        // Illegible: draw the page low-contrast into a layer, then composite it heavily blurred and
        // slightly rotated, like an out-of-focus photo of a faded thermal receipt.
        canvas.Clear(new SKColor(0xC9, 0xC7, 0xC2));
        using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(5f * RasterScale, 4f * RasterScale) };
        canvas.SaveLayer(blur);
        canvas.Translate(bitmap.Width / 2f, bitmap.Height / 2f);
        canvas.RotateDegrees(random.Between(-2.5f, 2.5f));
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        canvas.Scale(RasterScale);
        DrawPage(canvas, item, date, random, lowContrast: true);
        canvas.Restore();
        canvas.Flush();
        Rendering.AddNoise(bitmap, random);
        return bitmap;
    }

    private static void DrawPage(SKCanvas canvas, EvidenceItem item, DateOnly date, Random random, bool lowContrast)
    {
        var paper = lowContrast ? new SKColor(0xD2, 0xD0, 0xCB) : SKColors.White;
        var ink = lowContrast ? new SKColor(0xB4, 0xB2, 0xAD) : new SKColor(0x1C, 0x1F, 0x24);
        var muted = lowContrast ? new SKColor(0xBC, 0xBA, 0xB5) : new SKColor(0x5B, 0x61, 0x6B);
        var accents = new[] { new SKColor(0x1F, 0x4E, 0x99), new SKColor(0x0F, 0x6E, 0x5A), new SKColor(0x7A, 0x2E, 0x8C), new SKColor(0x9A, 0x4A, 0x12) };
        var accent = lowContrast ? new SKColor(0xAE, 0xAC, 0xA8) : accents[random.Next(accents.Length)];

        canvas.DrawRect(0, 0, PageWidth, PageHeight, Rendering.Fill(paper));
        canvas.Save();
        canvas.Translate(random.Between(-6, 6), random.Between(-6, 6));

        using var inkPaint = Rendering.Fill(ink);
        using var mutedPaint = Rendering.Fill(muted);
        using var accentPaint = Rendering.Fill(accent);
        using var title = Rendering.Font(Rendering.SansBold, 22);
        using var heading = Rendering.Font(Rendering.SansBold, 11);
        using var body = Rendering.Font(Rendering.Sans, 10.5f);
        using var mono = Rendering.Font(Rendering.Mono, 10.5f);

        const float left = 48;
        const float right = PageWidth - 48;

        // Seller block.
        canvas.DrawRect(left, 40, right - left, 6, accentPaint);
        canvas.DrawGlyphs(item.Seller!, left, 82, SKTextAlign.Left, title, inkPaint);
        var y = 100f;
        foreach (var line in (item.SellerAddress ?? "Retail store").Split('\n'))
        {
            canvas.DrawGlyphs(line.Trim(), left, y, SKTextAlign.Left, body, mutedPaint);
            y += 14;
        }

        using var invoiceTitle = Rendering.Font(Rendering.SansBold, 26);
        canvas.DrawGlyphs("INVOICE", right, 82, SKTextAlign.Right, invoiceTitle, accentPaint);
        canvas.DrawGlyphs($"Invoice no.: {item.InvoiceNumber}", right, 104, SKTextAlign.Right, body, inkPaint);
        canvas.DrawGlyphs($"Invoice date: {date:yyyy-MM-dd}", right, 118, SKTextAlign.Right, body, inkPaint);
        canvas.DrawGlyphs("Payment: card — paid in full", right, 132, SKTextAlign.Right, body, mutedPaint);

        // Bill to.
        y = Math.Max(y, 140) + 24;
        canvas.DrawGlyphs("BILL TO", left, y, SKTextAlign.Left, heading, accentPaint);
        y += 16;
        foreach (var line in (item.BillTo ?? "Walk-in customer").Split('\n'))
        {
            canvas.DrawGlyphs(line.Trim(), left, y, SKTextAlign.Left, body, inkPaint);
            y += 14;
        }

        // Line items.
        y += 26;
        var columns = new[] { left + 6, left + 210, left + 310, left + 420, right - 6 };
        canvas.DrawRect(left, y - 14, right - left, 22, accentPaint);
        using var headerInk = Rendering.Fill(lowContrast ? paper : SKColors.White);
        canvas.DrawGlyphs("Description", columns[0], y + 2, SKTextAlign.Left, heading, headerInk);
        canvas.DrawGlyphs("Model", columns[1], y + 2, SKTextAlign.Left, heading, headerInk);
        canvas.DrawGlyphs("Serial no.", columns[2], y + 2, SKTextAlign.Left, heading, headerInk);
        canvas.DrawGlyphs("Qty", columns[3], y + 2, SKTextAlign.Left, heading, headerInk);
        canvas.DrawGlyphs("Amount", columns[4], y + 2, SKTextAlign.Right, heading, headerInk);

        y += 28;
        var amount = Money(item.Amount!.Value, item.Currency);
        canvas.DrawGlyphs(item.ProductName ?? item.ModelCode!, columns[0], y, SKTextAlign.Left, body, inkPaint);
        canvas.DrawGlyphs(item.ModelCode!, columns[1], y, SKTextAlign.Left, mono, inkPaint);
        canvas.DrawGlyphs(item.Serial!, columns[2], y, SKTextAlign.Left, mono, inkPaint);
        canvas.DrawGlyphs("1", columns[3], y, SKTextAlign.Left, body, inkPaint);
        canvas.DrawGlyphs(amount, columns[4], y, SKTextAlign.Right, body, inkPaint);
        y += 12;
        using var rule = Rendering.Stroke(muted, 0.8f);
        canvas.DrawLine(left, y, right, y, rule);

        // Totals.
        var totalsLabel = left + 330;
        y += 26;
        canvas.DrawGlyphs("Subtotal", totalsLabel, y, SKTextAlign.Left, body, mutedPaint);
        canvas.DrawGlyphs(amount, columns[4], y, SKTextAlign.Right, body, inkPaint);
        y += 16;
        canvas.DrawGlyphs("Tax (included)", totalsLabel, y, SKTextAlign.Left, body, mutedPaint);
        canvas.DrawGlyphs(Money(0m, item.Currency), columns[4], y, SKTextAlign.Right, body, inkPaint);
        y += 20;
        canvas.DrawGlyphs("TOTAL", totalsLabel, y, SKTextAlign.Left, heading, inkPaint);
        canvas.DrawGlyphs(amount, columns[4], y, SKTextAlign.Right, heading, inkPaint);

        // Optional free-text note printed on the invoice.
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            y += 46;
            canvas.DrawGlyphs("NOTES", left, y, SKTextAlign.Left, heading, accentPaint);
            y += 16;
            foreach (var line in Rendering.Wrap(item.Note, body, right - left))
            {
                canvas.DrawGlyphs(line, left, y, SKTextAlign.Left, body, inkPaint);
                y += 14;
            }
        }

        // Footer.
        canvas.DrawLine(left, PageHeight - 92, right, PageHeight - 92, rule);
        canvas.DrawGlyphs("Thank you for your purchase. Keep this invoice as proof of purchase for warranty service.", left, PageHeight - 74, SKTextAlign.Left, body, mutedPaint);
        canvas.DrawGlyphs("Returns accepted within 30 days with this invoice.", left, PageHeight - 58, SKTextAlign.Left, body, mutedPaint);
        canvas.Restore();
    }

    private static string Money(decimal amount, string currency)
        => string.Create(CultureInfo.InvariantCulture, $"{currency} {amount:#,##0.00}");
}
