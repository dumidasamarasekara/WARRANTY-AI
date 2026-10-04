// Synthetic evidence generator (research R11, R19): product photos and invoices for the seed scenarios.
//
//   generate --spec <file.json> [--only S1,S2] [--as-of yyyy-MM-dd]
//       Renders every file of a spec (e.g. seed/evidence/evidence.json); paths are relative to the spec.
//   photo   --out <file.jpg> [--device tablet|phone|laptop|hub|oven] [--view front|back|label]
//           [--model-code AUR-TAB10] [--serial AT10-24-0001] [--brand Aurora] [--screen off|on]
//           [--damage crack,scorch,dent,liquid,corrosion,scratches] [--sticker "text"] [--variant n]
//           [--width 1024] [--height 768] [--quality 80]
//   invoice --out <file.pdf|.jpg|.png> --seller "Aurora Store" --invoice-number AS-1001
//           (--date yyyy-MM-dd | --purchase-date-offset-months -4 [--as-of yyyy-MM-dd])
//           --model-code AUR-TAB10 --serial AT10-24-0001 --amount 450 [--currency USD]
//           [--product-name "Aurora Tab 10"] [--seller-address "line 1\nline 2"] [--bill-to "[CUSTOMER]"]
//           [--note "free text printed on the invoice"] [--illegible] [--variant n]
//
// Every option of `photo` and `invoice` is also a property of a spec item (camelCase).
using System.Text.Json;
using Warranty.EvidenceGenerator;

try
{
    return Cli.Run(args);
}
catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or JsonException or FormatException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}
