using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;
using Warranty.Application.Abstractions.Storage;
using Warranty.Infrastructure.Storage;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// Claim submissions made of synthetic data: a fresh serial and freshly generated evidence (a PDF with a
/// random body, photos of random pixels, so unique hashes), so no claim built here raises a
/// duplicate-serial or evidence-reuse signal in another test's adjudication.
/// </summary>
internal static class SyntheticClaims
{
    public const string ContactEmail = "synthetic.submitter@example.test";

    public const string ContactPhone = "+1 555 010 9999";

    public static string NewSerial() => $"IT-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    /// <summary>A valid Aurora submission for <paramref name="serial"/>; one fresh PDF invoice and one fresh JPEG photo unless given.</summary>
    public static MultipartFormDataContent Submission(
        string serial, Action<JsonObject>? adjust = null, IReadOnlyList<EvidenceFile>? invoices = null, IReadOnlyList<EvidenceFile>? photos = null)
    {
        invoices ??= [Pdf()];
        photos ??= [Jpeg()];
        var purchaseDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var claim = new JsonObject
        {
            ["customer"] = new JsonObject
            {
                ["fullName"] = "Synthetic Submitter",
                ["email"] = ContactEmail,
                ["phone"] = ContactPhone,
                ["country"] = "US",
            },
            ["product"] = new JsonObject { ["modelCode"] = "AUR-TAB10", ["serialNumber"] = serial },
            ["purchase"] = new JsonObject
            {
                ["date"] = purchaseDate,
                ["place"] = "Aurora Store",
                ["price"] = 450.00m,
                ["currency"] = "USD",
                ["country"] = "US",
            },
            ["problemDescription"] = "The tablet stopped turning on and shows no charging light.",
        };
        adjust?.Invoke(claim);

        var form = new MultipartFormDataContent();
        var json = new StringContent(claim.ToJsonString(), Encoding.UTF8);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(json, "claim");
        foreach (var file in invoices)
        {
            form.Add(file.Content(), "invoice", file.Name);
        }

        foreach (var file in photos)
        {
            form.Add(file.Content(), "photos", file.Name);
        }

        return form;
    }

    public static EvidenceFile Pdf() => new("invoice.pdf", [.. "%PDF-1.4\n"u8, .. RandomNumberGenerator.GetBytes(256)], "application/pdf");

    public static EvidenceFile Jpeg() => new("photo-1.jpg", NoiseImage(SKEncodedImageFormat.Jpeg), "image/jpeg");

    public static EvidenceFile Png() => new("photo-2.png", NoiseImage(SKEncodedImageFormat.Png), "image/png");

    /// <summary>The bytes the API stores for <paramref name="file"/>: the output of upload sanitizing (T110).</summary>
    public static async Task<byte[]> StoredBytesAsync(EvidenceFile file, CancellationToken ct)
    {
        var result = await new UploadSanitizer().SanitizeAsync(new MemoryStream(file.Bytes, writable: false), ct);
        return result is UploadSanitizerResult.Sanitized clean
            ? clean.Content.ToArray()
            : throw new InvalidOperationException($"{file.Name} would be rejected: {((UploadSanitizerResult.Rejected)result).Reason}");
    }

    /// <summary>A small decodable image of random pixels: it passes upload sanitizing (T110) and its sanitized bytes are unique.</summary>
    private static byte[] NoiseImage(SKEncodedImageFormat format)
    {
        const int size = 16;
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var noise = RandomNumberGenerator.GetBytes(size * size * 3);
        for (var i = 0; i < size * size; i++)
        {
            bitmap.SetPixel(i % size, i / size, new SKColor(noise[i * 3], noise[(i * 3) + 1], noise[(i * 3) + 2]));
        }

        using var data = bitmap.Encode(format, 95);
        return data.ToArray();
    }
}

/// <summary>An evidence file part with its declared content type.</summary>
internal sealed record EvidenceFile(string Name, byte[] Bytes, string ContentType)
{
    public ByteArrayContent Content()
    {
        var content = new ByteArrayContent(Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        return content;
    }
}
