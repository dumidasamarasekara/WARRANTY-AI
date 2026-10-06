using SkiaSharp;
using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;

namespace Warranty.Infrastructure.Storage;

/// <summary>
/// Upload hardening before storage (FR-006a, FR-009, research R11, checklist CHK010):
/// <list type="bullet">
/// <item>The type comes from the magic bytes (<see cref="EvidenceFileSignature"/>) and must agree with
/// what the decoder finds; file names and declared content types never reach this class.</item>
/// <item>Photos (JPEG, PNG, WebP) are decoded, turned upright by their EXIF orientation, converted to
/// sRGB and re-encoded in their own format. The new file is built from pixels only, so every
/// embedded record — EXIF (GPS location, camera and device data), XMP, IPTC, ICC profiles, comments,
/// text chunks — is gone. Re-encoding is deterministic, so the same picture uploaded with different
/// metadata yields the same bytes and the same SHA-256 (reused-evidence detection, FR-017/018).</item>
/// <item>PDFs are kept byte for byte but rejected when encrypted or when they carry JavaScript,
/// launch actions or embedded files (<see cref="PdfInspector"/>).</item>
/// </list>
/// Malware scanning is a production extension (plan, PoC simplifications).
/// </summary>
internal sealed class UploadSanitizer : IUploadSanitizer
{
    /// <summary>Decoded-size cap (a decompression-bomb guard): 64 megapixels is ~256 MB of RGBA.</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>JPEG and WebP re-encoding quality: visually lossless for evidence photos.</summary>
    public const int Quality = 92;

    private const string UnsupportedType = "send a PDF, JPG, PNG or WebP file.";

    private const string Unreadable = "the image could not be read. Please send it again as a JPG or PNG photo.";

    public async Task<UploadSanitizerResult> SanitizeAsync(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = await ReadAllAsync(content, ClaimEvidence.MaxSizeBytes, ct);
        if (bytes is null)
        {
            return new UploadSanitizerResult.Rejected(EvidenceFileSignature.Detect([]), "it is larger than 15 MB.");
        }

        var type = EvidenceFileSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, EvidenceFileSignature.HeaderLength)));
        if (bytes.Length == 0)
        {
            return new UploadSanitizerResult.Rejected(type, "it is empty.");
        }

        return type switch
        {
            EvidenceFileType.Pdf => SanitizePdf(bytes),
            EvidenceFileType.Jpeg or EvidenceFileType.Png or EvidenceFileType.WebP => SanitizeImage(bytes, type),
            _ => new UploadSanitizerResult.Rejected(type, UnsupportedType),
        };
    }

    private static UploadSanitizerResult SanitizePdf(byte[] bytes)
    {
        var threat = PdfInspector.FindThreat(bytes);
        return threat is null
            ? new UploadSanitizerResult.Sanitized(EvidenceFileType.Pdf, EvidenceFileSignature.ContentTypeOf(EvidenceFileType.Pdf)!, bytes)
            : new UploadSanitizerResult.Rejected(EvidenceFileType.Pdf, threat);
    }

    private static UploadSanitizerResult SanitizeImage(byte[] bytes, EvidenceFileType type)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || TypeOf(codec.EncodedFormat) != type)
        {
            return new UploadSanitizerResult.Rejected(type, Unreadable);
        }

        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            return new UploadSanitizerResult.Rejected(type, "the image is larger than 64 megapixels. Please send a smaller photo.");
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return new UploadSanitizerResult.Rejected(type, Unreadable);
        }

        var origin = codec.EncodedOrigin;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (width, height) = swapsAxes ? (decoded.Height, decoded.Width) : (decoded.Width, decoded.Height);
        var opaque = type == EvidenceFileType.Jpeg || codec.Info.AlphaType == SKAlphaType.Opaque;

        // Drawing into an sRGB target converts colors from any embedded ICC profile; the profile itself is not kept.
        using var target = new SKBitmap(new SKImageInfo(
            width, height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
            canvas.SetMatrix(OrientationMatrix(origin, width, height));
            using var source = SKImage.FromBitmap(decoded);
            canvas.DrawImage(source, 0, 0, SKSamplingOptions.Default);
        }

        // Encoding a pixmap without a color space writes no ICC profile either: the file holds pixels only.
        using var pixels = target.PeekPixels();
        using var untagged = pixels.WithColorSpace(null!); // SkiaSharp's annotation omits that null means "untagged"
        using var encoded = untagged.Encode(FormatOf(type), Quality);
        if (encoded is null)
        {
            return new UploadSanitizerResult.Rejected(type, Unreadable);
        }

        if (encoded.Size > ClaimEvidence.MaxSizeBytes)
        {
            return new UploadSanitizerResult.Rejected(type, "the image is larger than 15 MB once cleaned. Please send a smaller photo.");
        }

        return new UploadSanitizerResult.Sanitized(type, EvidenceFileSignature.ContentTypeOf(type)!, encoded.ToArray());
    }

    /// <summary>Maps the stored pixel grid (drawn at the origin) onto the upright <paramref name="width"/> × <paramref name="height"/> canvas.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static EvidenceFileType TypeOf(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => EvidenceFileType.Jpeg,
        SKEncodedImageFormat.Png => EvidenceFileType.Png,
        SKEncodedImageFormat.Webp => EvidenceFileType.WebP,
        _ => EvidenceFileType.Unknown,
    };

    private static SKEncodedImageFormat FormatOf(EvidenceFileType type) => type switch
    {
        EvidenceFileType.Jpeg => SKEncodedImageFormat.Jpeg,
        EvidenceFileType.Png => SKEncodedImageFormat.Png,
        EvidenceFileType.WebP => SKEncodedImageFormat.Webp,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an image type."),
    };

    /// <summary>The whole content, or null when it is longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadAllAsync(Stream content, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int n;
        while ((n = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }
}
