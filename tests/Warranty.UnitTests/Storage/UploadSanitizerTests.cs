using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Warranty.Application.Abstractions.Storage;
using Warranty.Application.Claims;
using Warranty.Infrastructure.Storage;

namespace Warranty.UnitTests.Storage;

/// <summary>
/// Upload hardening (T110, FR-006a, FR-009, research R11): types by magic bytes, photos re-encoded
/// upright without metadata, deterministic hashes of the sanitized bytes, and dangerous PDFs refused.
/// </summary>
public sealed class UploadSanitizerTests
{
    private static readonly SKColor Red = new(220, 20, 20);
    private static readonly SKColor Blue = new(20, 20, 220);

    private readonly UploadSanitizer _sanitizer = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_jpeg_with_gps_and_camera_exif_comes_out_upright_without_any_metadata()
    {
        // Stored 64×32 with a red top-left quadrant; EXIF orientation 6 means "rotate 90° clockwise to display".
        var photo = WithSegments(
            Jpeg(QuadrantImage(64, 32)),
            App1Exif(make: "TestCam", model: "TestCam Pro 9", orientation: 6, latitudeDegrees: 59),
            App1Xmp("<x:xmpmeta xmlns:x='adobe:ns:meta/'><dc:creator>Sample Person</dc:creator></x:xmpmeta>"),
            App13Iptc("Sample Person"),
            Comment("taken at 1 Example Road"));

        var clean = await SanitizedAsync(photo);

        clean.Type.ShouldBe(EvidenceFileType.Jpeg);
        clean.ContentType.ShouldBe("image/jpeg");
        var bytes = clean.Content.ToArray();
        JpegMarkers(bytes).Where(m => m is >= 0xE1 and <= 0xEF or 0xFE).ShouldBeEmpty("only the JFIF APP0 header may remain");
        foreach (var text in new[] { "Exif", "TestCam", "xmpmeta", "Photoshop", "Sample Person", "Example Road", "ICC_PROFILE" })
        {
            Contains(bytes, text).ShouldBeFalse(text);
        }

        using var codec = SKCodec.Create(SKData.CreateCopy(bytes));
        codec.EncodedOrigin.ShouldBe(SKEncodedOrigin.TopLeft);
        using var upright = SKBitmap.Decode(codec);
        (upright.Width, upright.Height).ShouldBe((32, 64));
        ShouldBeClose(upright.GetPixel(24, 8), Red); // the stored top-left is now top-right
        ShouldBeClose(upright.GetPixel(8, 8), Blue);
        ShouldBeClose(upright.GetPixel(24, 56), Blue);
    }

    [Fact]
    public async Task The_same_photo_uploaded_with_different_metadata_yields_the_same_bytes_and_hash()
    {
        var picture = Jpeg(QuadrantImage(48, 48));
        var first = WithSegments(picture, App1Exif("TestCam", "Model A", orientation: 1, latitudeDegrees: 40));
        var second = WithSegments(picture, App1Exif("OtherCam", "Model B", orientation: 1, latitudeDegrees: 12), Comment("edited"));
        SHA256.HashData(first).ShouldNotBe(SHA256.HashData(second));

        var a = (await SanitizedAsync(first)).Content.ToArray();
        var b = (await SanitizedAsync(second)).Content.ToArray();
        var plain = (await SanitizedAsync(picture)).Content.ToArray();

        Convert.ToHexStringLower(SHA256.HashData(a)).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(b)));
        a.ShouldBe(plain);
    }

    [Fact]
    public async Task A_png_named_jpg_is_classified_by_content_and_loses_its_text_chunks()
    {
        // The port takes no name or declared type at all; the PNG signature decides.
        var png = WithPngText(Png(QuadrantImage(20, 10)), "Author", "Sample Person, 1 Example Road");

        var clean = await SanitizedAsync(png);

        clean.Type.ShouldBe(EvidenceFileType.Png);
        clean.ContentType.ShouldBe("image/png");
        var bytes = clean.Content.ToArray();
        EvidenceFileSignature.Detect(bytes).ShouldBe(EvidenceFileType.Png);
        PngChunks(bytes).ShouldBeSubsetOf(["IHDR", "PLTE", "tRNS", "sBIT", "IDAT", "IEND"], "pixel data and its format only: no text, EXIF, time or ICC chunks");
        Contains(bytes, "Sample Person").ShouldBeFalse();
        using var decoded = SKBitmap.Decode(bytes);
        (decoded.Width, decoded.Height).ShouldBe((20, 10));
        ShouldBeClose(decoded.GetPixel(2, 2), Red);
    }

    [Fact]
    public async Task A_webp_photo_is_re_encoded_as_webp()
    {
        using var image = QuadrantImage(16, 16);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90);

        var clean = await SanitizedAsync(encoded.ToArray());

        clean.Type.ShouldBe(EvidenceFileType.WebP);
        clean.ContentType.ShouldBe("image/webp");
        EvidenceFileSignature.Detect(clean.Content.Span).ShouldBe(EvidenceFileType.WebP);
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public async Task Unknown_unreadable_and_empty_files_are_rejected(byte[] content, EvidenceFileType type)
    {
        var rejected = (await _sanitizer.SanitizeAsync(new MemoryStream(content), Ct)).ShouldBeOfType<UploadSanitizerResult.Rejected>();

        rejected.Type.ShouldBe(type);
        rejected.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    public static TheoryData<byte[], EvidenceFileType> Unusable() => new()
    {
        { "just some text"u8.ToArray(), EvidenceFileType.Unknown },
        { "GIF89a\x01\x00\x01\x00"u8.ToArray(), EvidenceFileType.Unknown },
        { [0x00, 0x00, 0x00, 0x18, .. "ftypheic"u8, 0x00, 0x00, 0x00, 0x00, .. "mif1heic"u8], EvidenceFileType.Heic },
        { [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Repeat((byte)0x5A, 300)], EvidenceFileType.Jpeg },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Repeat((byte)0x5A, 300)], EvidenceFileType.Png },
        { [], EvidenceFileType.Unknown },
    };

    [Fact]
    public async Task A_plain_pdf_is_stored_byte_for_byte()
    {
        var pdf = Pdf(catalogExtra: "/Lang (en-US)", extraObjects: "3 0 obj\n<< /Producer (Invoice /JavaScript generator) >>\nendobj\n");

        var clean = await SanitizedAsync(pdf);

        clean.Type.ShouldBe(EvidenceFileType.Pdf);
        clean.ContentType.ShouldBe("application/pdf");
        clean.Content.ToArray().ShouldBe(pdf, "a name inside a string is text, not a name");
    }

    [Fact]
    public async Task Binary_stream_data_that_happens_to_look_like_a_name_is_not_a_false_alarm()
    {
        var pdf = Pdf(extraObjects: "4 0 obj\n<< /Length 18 /Filter /DCTDecode >>\nstream\n\xff\xd8 /JS /Launch \xff\xd9\nendstream\nendobj\n");

        (await _sanitizer.SanitizeAsync(new MemoryStream(pdf), Ct)).ShouldBeOfType<UploadSanitizerResult.Sanitized>();
    }

    [Theory]
    [InlineData("/OpenAction << /S /JavaScript /JS (app.alert\\(1\\)) >>", PdfInspector.JavaScript)]
    [InlineData("/OpenAction << /S /J#61vaScript /J#53 (app.alert\\(1\\)) >>", PdfInspector.JavaScript)]
    [InlineData("/Names << /JavaScript 7 0 R >>", PdfInspector.JavaScript)]
    [InlineData("/OpenAction << /S /Launch /F (calc.exe) >>", PdfInspector.LaunchAction)]
    [InlineData("/Names << /EmbeddedFiles 6 0 R >>", PdfInspector.EmbeddedFiles)]
    public async Task A_pdf_with_javascript_a_launch_action_or_embedded_files_is_rejected(string catalogExtra, string reason)
    {
        var rejected = (await _sanitizer.SanitizeAsync(new MemoryStream(Pdf(catalogExtra)), Ct)).ShouldBeOfType<UploadSanitizerResult.Rejected>();

        rejected.Type.ShouldBe(EvidenceFileType.Pdf);
        rejected.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task An_encrypted_pdf_is_rejected()
    {
        var pdf = Pdf(trailerExtra: "/Encrypt 5 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]",
            extraObjects: "5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1028 /O <00> /U <00> >>\nendobj\n");

        var rejected = (await _sanitizer.SanitizeAsync(new MemoryStream(pdf), Ct)).ShouldBeOfType<UploadSanitizerResult.Rejected>();

        rejected.Reason.ShouldBe(PdfInspector.Encrypted);
    }

    [Fact]
    public async Task Javascript_hidden_in_a_compressed_object_stream_is_found()
    {
        var objects = "7 0 << /S /JavaScript /JS (app.alert\\(1\\)) >>"u8.ToArray();
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(objects);
        }

        var body = compressed.ToArray();
        var pdf = Concat(
            Pdf(catalogExtra: "/OpenAction 7 0 R")[..^"%%EOF\n".Length],
            Latin1($"8 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode /Length {body.Length} >>\nstream\n"),
            body,
            Latin1("\nendstream\nendobj\n%%EOF\n"));

        var rejected = (await _sanitizer.SanitizeAsync(new MemoryStream(pdf), Ct)).ShouldBeOfType<UploadSanitizerResult.Rejected>();

        rejected.Reason.ShouldBe(PdfInspector.JavaScript);
    }

    [Fact]
    public async Task An_object_stream_that_cannot_be_decoded_makes_the_pdf_unverifiable()
    {
        var pdf = Pdf(extraObjects: "8 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /LZWDecode /Length 4 >>\nstream\nabcd\nendstream\nendobj\n");

        var rejected = (await _sanitizer.SanitizeAsync(new MemoryStream(pdf), Ct)).ShouldBeOfType<UploadSanitizerResult.Rejected>();

        rejected.Reason.ShouldBe(PdfInspector.Unverifiable);
    }

    [Fact]
    public async Task Every_seeded_evidence_file_passes_and_seeded_pdfs_are_unchanged()
    {
        var files = Directory.GetFiles(Path.Combine(RepositoryRoot(), "seed", "evidence"), "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp")
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, Ct);
            var result = await _sanitizer.SanitizeAsync(new MemoryStream(bytes), Ct);
            var clean = result.ShouldBeOfType<UploadSanitizerResult.Sanitized>(file);
            if (clean.Type == EvidenceFileType.Pdf)
            {
                clean.Content.ToArray().ShouldBe(bytes, file);
            }
        }
    }

    private async Task<UploadSanitizerResult.Sanitized> SanitizedAsync(byte[] content)
        => (await _sanitizer.SanitizeAsync(new MemoryStream(content, writable: false), Ct)).ShouldBeOfType<UploadSanitizerResult.Sanitized>();

    private static SKBitmap QuadrantImage(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Blue);
        using var paint = new SKPaint { Color = Red };
        canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        return bitmap;
    }

    private static byte[] Jpeg(SKBitmap bitmap)
    {
        using (bitmap)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            return data.ToArray();
        }
    }

    private static byte[] Png(SKBitmap bitmap)
    {
        using (bitmap)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    /// <summary>Inserts JPEG segments right after the SOI marker.</summary>
    private static byte[] WithSegments(byte[] jpeg, params byte[][] segments) => Concat([jpeg[..2], .. segments, jpeg[2..]]);

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    /// <summary>An APP1 EXIF segment (big-endian TIFF) with Make, Model, Orientation and a GPS IFD.</summary>
    private static byte[] App1Exif(string make, string model, ushort orientation, uint latitudeDegrees)
    {
        var makeBytes = Latin1(make + "\0");
        var modelBytes = Latin1(model + "\0");
        const int ifd0 = 8;
        const int ifd0Size = 2 + (4 * 12) + 4;
        var makeOffset = ifd0 + ifd0Size;
        var modelOffset = makeOffset + makeBytes.Length;
        var gpsOffset = modelOffset + modelBytes.Length;
        const int gpsSize = 2 + (2 * 12) + 4;
        var latitudeOffset = gpsOffset + gpsSize;

        var tiff = new byte[latitudeOffset + 24];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), ifd0);

        var at = ifd0;
        void Entry(ushort tag, ushort type, uint count, uint value, bool shortValue = false)
        {
            var span = tiff.AsSpan();
            BinaryPrimitives.WriteUInt16BigEndian(span[at..], tag);
            BinaryPrimitives.WriteUInt16BigEndian(span[(at + 2)..], type);
            BinaryPrimitives.WriteUInt32BigEndian(span[(at + 4)..], count);
            if (shortValue)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span[(at + 8)..], (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(span[(at + 8)..], value);
            }

            at += 12;
        }

        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), 4);
        at += 2;
        Entry(0x010F, 2, (uint)makeBytes.Length, (uint)makeOffset);
        Entry(0x0110, 2, (uint)modelBytes.Length, (uint)modelOffset);
        Entry(0x0112, 3, 1, orientation, shortValue: true);
        Entry(0x8825, 4, 1, (uint)gpsOffset);
        at += 4; // no next IFD
        makeBytes.CopyTo(tiff, makeOffset);
        modelBytes.CopyTo(tiff, modelOffset);

        at = gpsOffset;
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), 2);
        at += 2;
        Entry(0x0001, 2, 2, 0x4E000000); // GPSLatitudeRef "N"
        Entry(0x0002, 5, 3, (uint)latitudeOffset); // GPSLatitude
        uint[] rationals = [latitudeDegrees, 1, 30, 1, 15, 1];
        for (var i = 0; i < rationals.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(latitudeOffset + (i * 4)), rationals[i]);
        }

        return Segment(0xE1, Concat("Exif\0\0"u8.ToArray(), tiff));
    }

    private static byte[] App1Xmp(string xml) => Segment(0xE1, Concat(Latin1("http://ns.adobe.com/xap/1.0/\0"), Encoding.UTF8.GetBytes(xml)));

    private static byte[] App13Iptc(string byline)
    {
        var text = Latin1(byline);
        byte[] dataset = [0x1C, 0x02, 0x50, 0x00, (byte)text.Length, .. text]; // IPTC 2:80 By-line
        byte[] resource = [.. "8BIM"u8, 0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)dataset.Length, .. dataset];
        if (resource.Length % 2 == 1)
        {
            resource = [.. resource, 0x00];
        }

        return Segment(0xED, Concat(Latin1("Photoshop 3.0\0"), resource));
    }

    private static byte[] Comment(string text) => Segment(0xFE, Latin1(text));

    /// <summary>The marker bytes of every segment before the scan data.</summary>
    private static List<byte> JpegMarkers(byte[] jpeg)
    {
        var markers = new List<byte>();
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            markers.Add(marker);
            if (marker == 0xDA)
            {
                break;
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(i + 2));
        }

        return markers;
    }

    /// <summary>Adds a tEXt chunk after IHDR (signature 8 bytes + IHDR 25 bytes).</summary>
    private static byte[] WithPngText(byte[] png, string keyword, string text)
    {
        var data = Concat(Latin1(keyword + "\0"), Latin1(text));
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return Concat(png[..33], chunk, png[33..]);
    }

    private static List<string> PngChunks(byte[] png)
    {
        var chunks = new List<string>();
        for (var i = 8; i + 8 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(i));
            chunks.Add(Encoding.ASCII.GetString(png, i + 4, 4));
            i += 12 + length;
        }

        return chunks;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>A minimal PDF: catalog, empty page tree, optional extra objects and trailer entries.</summary>
    private static byte[] Pdf(string catalogExtra = "", string trailerExtra = "", string extraObjects = "") => Latin1(
        "%PDF-1.7\n%\xe2\xe3\xcf\xd3\n" +
        $"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
        extraObjects +
        $"trailer\n<< /Size 9 /Root 1 0 R {trailerExtra} >>\n%%EOF\n");

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;

    private static void ShouldBeClose(SKColor actual, SKColor expected)
    {
        Math.Abs(actual.Red - expected.Red).ShouldBeLessThan(40, $"{actual} vs {expected}");
        Math.Abs(actual.Green - expected.Green).ShouldBeLessThan(40, $"{actual} vs {expected}");
        Math.Abs(actual.Blue - expected.Blue).ShouldBeLessThan(40, $"{actual} vs {expected}");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
