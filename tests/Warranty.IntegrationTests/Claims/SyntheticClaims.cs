using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Warranty.IntegrationTests.Claims;

/// <summary>
/// Claim submissions made of synthetic data: a fresh serial and freshly generated evidence bytes behind
/// real file signatures (random bodies, so unique hashes), so no claim built here raises a
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

    public static EvidenceFile Jpeg() => new("photo-1.jpg", [0xFF, 0xD8, 0xFF, 0xE0, .. RandomNumberGenerator.GetBytes(256)], "image/jpeg");

    public static EvidenceFile Png() => new("photo-2.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. RandomNumberGenerator.GetBytes(256)], "image/png");
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
