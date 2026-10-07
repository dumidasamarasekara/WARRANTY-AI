using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace Warranty.AI.Gateway.Imaging;

/// <summary>
/// Renders PDF pages to JPEG images for models that accept images but not PDFs (the <c>ollama</c>
/// provider). Rendering, not text extraction: invoices may be scans or draw their text as outlines.
/// Pages are rendered at twice the PDF's 72 dpi, flattened onto white and passed through the
/// <see cref="ImageDownscaler"/>, so they arrive like any other evidence image.
/// </summary>
public sealed class PdfRasterizer(ImageDownscaler downscaler)
{
    private const double RenderScale = 2.0;

    // PDFium is not thread-safe; one document is rendered at a time.
    private static readonly Lock Pdfium = new();

    /// <summary>
    /// The first <paramref name="maxPages"/> pages as images, plus the document's page count.
    /// Throws <see cref="InvalidDataException"/> when the bytes are not a readable PDF.
    /// </summary>
    public (IReadOnlyList<DownscaledImage> Pages, int PageCount) Render(ReadOnlyMemory<byte> pdf, int maxPages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        var pages = new List<DownscaledImage>();
        int pageCount;
        lock (Pdfium)
        {
            try
            {
                using var document = DocLib.Instance.GetDocReader(pdf.ToArray(), new PageDimensions(RenderScale));
                pageCount = document.GetPageCount();
                for (var index = 0; index < Math.Min(pageCount, maxPages); index++)
                {
                    using var page = document.GetPageReader(index);
                    pages.Add(downscaler.Downscale(ToPng(page.GetImage(), page.GetPageWidth(), page.GetPageHeight())));
                }
            }
            catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
            {
                throw new InvalidDataException($"The PDF could not be rendered: {ex.Message}", ex);
            }
        }

        if (pages.Count == 0)
        {
            throw new InvalidDataException("The PDF has no pages.");
        }

        return (pages, pageCount);
    }

    /// <summary>PDFium's BGRA pixels (transparent where nothing is drawn) as a PNG on white.</summary>
    private static byte[] ToPng(byte[] bgra, int width, int height)
    {
        if (width < 1 || height < 1 || bgra.Length < width * height * 4)
        {
            throw new InvalidDataException("The PDF page rendered to an empty image.");
        }

        using var page = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, page.GetPixels(), width * height * 4);

        using var flattened = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(page);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), SKSamplingOptions.Default);
        }

        using var encoded = flattened.Encode(SKEncodedImageFormat.Png, 100)
                            ?? throw new InvalidDataException("The rendered PDF page could not be encoded.");
        return encoded.ToArray();
    }
}
