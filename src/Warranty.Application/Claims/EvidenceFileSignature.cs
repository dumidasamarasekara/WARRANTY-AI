namespace Warranty.Application.Claims;

/// <summary>The type of an uploaded file as judged by its content (FR-009).</summary>
public enum EvidenceFileType
{
    Unknown,
    Jpeg,
    Png,
    WebP,
    Pdf,

    /// <summary>HEIC/HEIF photos: rejected in the PoC with a request for JPEG or PNG (research R11).</summary>
    Heic,
}

/// <summary>
/// Identifies an evidence file by its magic bytes; the file name and the declared content type are
/// never trusted (FR-009, research R11). Content sanitizing (metadata removal, PDF checks) is done by
/// <see cref="Abstractions.Storage.IUploadSanitizer"/>.
/// </summary>
public static class EvidenceFileSignature
{
    /// <summary>Bytes needed to recognise every supported signature.</summary>
    public const int HeaderLength = 16;

    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> Pdf => "%PDF-"u8;

    private static ReadOnlySpan<byte> Riff => "RIFF"u8;

    private static ReadOnlySpan<byte> WebP => "WEBP"u8;

    private static ReadOnlySpan<byte> Ftyp => "ftyp"u8;

    /// <summary>ISO-BMFF major brands of HEIC/HEIF images (incl. sequences and the generic HEIF brands).</summary>
    private static readonly string[] HeifBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1"];

    public static EvidenceFileType Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(Jpeg))
        {
            return EvidenceFileType.Jpeg;
        }

        if (header.StartsWith(Png))
        {
            return EvidenceFileType.Png;
        }

        if (header.StartsWith(Pdf))
        {
            return EvidenceFileType.Pdf;
        }

        if (header.Length >= 12 && header.StartsWith(Riff) && header[8..12].SequenceEqual(WebP))
        {
            return EvidenceFileType.WebP;
        }

        if (header.Length >= 12 && header[4..8].SequenceEqual(Ftyp))
        {
            var brand = System.Text.Encoding.ASCII.GetString(header[8..12]);
            if (HeifBrands.Contains(brand, StringComparer.Ordinal))
            {
                return EvidenceFileType.Heic;
            }
        }

        return EvidenceFileType.Unknown;
    }

    /// <summary>Reads the start of <paramref name="content"/> and identifies it.</summary>
    public static async Task<EvidenceFileType> DetectAsync(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var buffer = new byte[HeaderLength];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await content.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return Detect(buffer.AsSpan(0, read));
    }

    /// <summary>The stored content type of a supported type; null for unknown and HEIC files.</summary>
    public static string? ContentTypeOf(EvidenceFileType type) => type switch
    {
        EvidenceFileType.Jpeg => "image/jpeg",
        EvidenceFileType.Png => "image/png",
        EvidenceFileType.WebP => "image/webp",
        EvidenceFileType.Pdf => "application/pdf",
        _ => null,
    };
}
