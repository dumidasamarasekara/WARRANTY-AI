using SkiaSharp;

namespace Warranty.AI.Gateway.Imaging;

/// <summary>A JPEG ready to send to a model.</summary>
public sealed record DownscaledImage(byte[] Data, int Width, int Height)
{
    public const string MediaType = "image/jpeg";
}

/// <summary>
/// Shrinks evidence photos before they reach a model (contracts/ai-gateway.md): the long edge is
/// capped at <see cref="MaxLongEdge"/> pixels, the camera orientation is applied (re-encoding drops
/// EXIF, so a rotated phone photo would otherwise arrive sideways), transparency is flattened onto
/// white and the result is always re-encoded as JPEG. The stored evidence file is never changed.
/// </summary>
public sealed class ImageDownscaler
{
    public const int MaxLongEdge = 1_500;

    public const int JpegQuality = 85;

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>Throws <see cref="InvalidDataException"/> when the bytes are not a decodable image.</summary>
    public DownscaledImage Downscale(ReadOnlySpan<byte> image)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The image format is not recognized.");
        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("The image could not be decoded.");

        var origin = codec.EncodedOrigin;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (uprightWidth, uprightHeight) = swapsAxes ? (decoded.Height, decoded.Width) : (decoded.Width, decoded.Height);

        var scale = Math.Min(1d, (double)MaxLongEdge / Math.Max(uprightWidth, uprightHeight));
        var width = Math.Max(1, (int)Math.Round(uprightWidth * scale));
        var height = Math.Max(1, (int)Math.Round(uprightHeight * scale));

        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.White);
            canvas.SetMatrix(OrientationMatrix(origin, width, height));
            var (drawWidth, drawHeight) = swapsAxes ? (height, width) : (width, height);
            using var source = SKImage.FromBitmap(decoded);
            canvas.DrawImage(source, new SKRect(0, 0, drawWidth, drawHeight), Sampling);
        }

        using var encoded = target.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                            ?? throw new InvalidDataException("The image could not be re-encoded as JPEG.");
        return new DownscaledImage(encoded.ToArray(), width, height);
    }

    /// <summary>
    /// Maps the stored pixel grid (drawn into a <c>drawWidth × drawHeight</c> rectangle at the origin)
    /// onto the upright <paramref name="width"/> × <paramref name="height"/> canvas.
    /// </summary>
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
}
