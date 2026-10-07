using System.Net;
using System.Text.RegularExpressions;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers;

/// <summary>
/// How every chat provider presents claimant text (FR-019, research R14): inside a labelled delimiter,
/// with any delimiter-like tag in the text neutralised so the content cannot close the block and pose
/// as instructions.
/// </summary>
internal static partial class UntrustedDelimiter
{
    public const string Tag = "untrusted_claim_content";

    public static string Wrap(UntrustedTextPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var label = WebUtility.HtmlEncode(part.Label);
        var body = TagPattern().Replace(part.Text, match => WebUtility.HtmlEncode(match.Value));
        return $"<{Tag} label=\"{label}\">\n{body}\n</{Tag}>";
    }

    [GeneratedRegex($"</?\\s*{Tag}", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();
}
