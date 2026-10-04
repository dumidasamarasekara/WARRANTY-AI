using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warranty.EvidenceGenerator;

/// <summary>
/// A spec file (e.g. <c>seed/evidence/evidence.json</c>): the evidence files to generate. Item paths are
/// relative to the spec file's directory.
/// </summary>
public sealed class EvidenceSpec
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public List<EvidenceItem> Files { get; init; } = [];

    public static EvidenceSpec Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<EvidenceSpec>(stream, JsonOptions)
               ?? throw new InvalidDataException($"Spec '{path}' is empty.");
    }
}

/// <summary>
/// One evidence file. <see cref="Kind"/> selects the renderer; the other properties are the rendering
/// options of that kind (unused ones are ignored). The same shape is filled from command-line options.
/// </summary>
public sealed class EvidenceItem
{
    public static readonly IReadOnlySet<string> Devices = new HashSet<string>(StringComparer.Ordinal) { "tablet", "phone", "laptop", "hub", "oven" };

    public static readonly IReadOnlySet<string> Views = new HashSet<string>(StringComparer.Ordinal) { "front", "back", "label" };

    public static readonly IReadOnlySet<string> DamageKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "crack", "scorch", "dent", "liquid", "corrosion", "scratches",
    };

    /// <summary>Output path; the extension picks the format (<c>.jpg</c>, <c>.png</c>, <c>.webp</c>, or <c>.pdf</c> for invoices).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary><c>photo</c> or <c>invoice</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Scenario the file belongs to (for <c>--only</c>); informational otherwise.</summary>
    public string? Scenario { get; set; }

    /// <summary>Seeds the small rendering variations (tint, offset, rotation, noise) so otherwise identical files hash differently.</summary>
    public int Variant { get; set; }

    // ---- photo ----

    /// <summary><c>tablet</c>, <c>phone</c>, <c>laptop</c>, <c>hub</c> or <c>oven</c>.</summary>
    public string Device { get; set; } = "tablet";

    /// <summary><c>front</c> (screen/door side), <c>back</c> (housing with the serial label) or <c>label</c> (close-up of the label).</summary>
    public string View { get; set; } = "front";

    /// <summary>Brand text printed on the housing.</summary>
    public string? Brand { get; set; }

    public string? ModelCode { get; set; }

    /// <summary>Serial printed on the serial label (back and label views); no label when null.</summary>
    public string? Serial { get; set; }

    /// <summary>Front view: <c>off</c> (black screen) or <c>on</c> (lit home screen).</summary>
    public string Screen { get; set; } = "off";

    /// <summary>Damage overlays: <c>crack</c>, <c>scorch</c>, <c>dent</c>, <c>liquid</c>, <c>corrosion</c>, <c>scratches</c>.</summary>
    public List<string> Damage { get; set; } = [];

    /// <summary>Free text written on a sticky note visible in the photo (e.g. for manipulation scenarios).</summary>
    public string? Sticker { get; set; }

    public int Width { get; set; } = 1024;

    public int Height { get; set; } = 768;

    /// <summary>JPEG/WebP quality 1–100.</summary>
    public int Quality { get; set; } = 80;

    // ---- invoice ----

    public string? Seller { get; set; }

    public string? SellerAddress { get; set; }

    public string? InvoiceNumber { get; set; }

    /// <summary>Absolute invoice date (ISO). Alternative to <see cref="PurchaseDateOffsetMonths"/>.</summary>
    public string? Date { get; set; }

    /// <summary>Invoice date as a month offset from the generation day (<c>--as-of</c>), e.g. <c>-4</c>.</summary>
    public int? PurchaseDateOffsetMonths { get; set; }

    public string? ProductName { get; set; }

    public decimal? Amount { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>Optional "Bill to" text (use placeholders, never real people).</summary>
    public string? BillTo { get; set; }

    /// <summary>Free-text line printed on the invoice (e.g. for manipulation scenarios).</summary>
    public string? Note { get; set; }

    /// <summary>Renders the invoice blurred and low-contrast as a raster, so it cannot be read.</summary>
    public bool Illegible { get; set; }

    /// <summary>The invoice date for a generation day.</summary>
    public DateOnly InvoiceDate(DateOnly asOf)
    {
        if (Date is { } date)
        {
            return DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return PurchaseDateOffsetMonths is { } months
            ? asOf.AddMonths(months)
            : throw new InvalidDataException($"Invoice '{Path}' needs 'date' or 'purchaseDateOffsetMonths'.");
    }

    /// <summary>Every problem with the item; empty when it can be rendered.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Path))
        {
            errors.Add("'path' is required.");
        }

        var extension = System.IO.Path.GetExtension(Path).ToLowerInvariant();
        switch (Kind)
        {
            case "photo":
                if (!Devices.Contains(Device))
                {
                    errors.Add($"Unknown device '{Device}' (expected {string.Join(", ", Devices)}).");
                }

                if (!Views.Contains(View))
                {
                    errors.Add($"Unknown view '{View}' (expected {string.Join(", ", Views)}).");
                }

                errors.AddRange(Damage.Where(d => !DamageKinds.Contains(d)).Select(d => $"Unknown damage '{d}' (expected {string.Join(", ", DamageKinds)})."));
                if (View == "label" && Serial is null)
                {
                    errors.Add("The 'label' view needs a 'serial'.");
                }

                if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
                {
                    errors.Add($"A photo must be .jpg, .png or .webp, not '{extension}'.");
                }

                break;

            case "invoice":
                if (string.IsNullOrWhiteSpace(Seller))
                {
                    errors.Add("'seller' is required.");
                }

                if (string.IsNullOrWhiteSpace(InvoiceNumber))
                {
                    errors.Add("'invoiceNumber' is required.");
                }

                if (Date is null && PurchaseDateOffsetMonths is null)
                {
                    errors.Add("'date' or 'purchaseDateOffsetMonths' is required.");
                }

                if (Date is not null && !DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add($"'date' must be yyyy-MM-dd, not '{Date}'.");
                }

                if (string.IsNullOrWhiteSpace(ModelCode) || string.IsNullOrWhiteSpace(Serial) || Amount is null)
                {
                    errors.Add("'modelCode', 'serial' and 'amount' are required.");
                }

                if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp"))
                {
                    errors.Add($"An invoice must be .pdf, .jpg, .png or .webp, not '{extension}'.");
                }

                break;

            default:
                errors.Add($"Unknown kind '{Kind}' (expected photo or invoice).");
                break;
        }

        if (Width is < 200 or > 4000 || Height is < 200 or > 4000)
        {
            errors.Add("'width' and 'height' must be between 200 and 4000.");
        }

        if (Quality is < 1 or > 100)
        {
            errors.Add("'quality' must be between 1 and 100.");
        }

        return errors;
    }
}
