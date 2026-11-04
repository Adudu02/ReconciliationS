using System.Text;
using System.Text.RegularExpressions;

namespace Reconciliation.Core.Matching;

internal sealed partial class MatchingText
{
    public string Compact { get; }
    public string Description { get; }
    public HashSet<string> RfcTerms { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);

    public MatchingText(MatchingMovement movement)
    {
        Description = Normalize(movement.Description);
        for (var length = 12; length <= 13; length++)
            for (var start = 0; start <= Description.Length - length; start++) RfcTerms.Add(Description.Substring(start, length));
        var tokens = Tokens().Matches((movement.Description + " " + movement.Reference).ToUpperInvariant()).Select(match => Normalize(match.Value)).ToArray();
        Compact = string.Concat(tokens);
        foreach (var token in tokens) Terms.Add(token);
        for (var i = 1; i < tokens.Length; i++) Terms.Add(tokens[i - 1] + tokens[i]);
        foreach (var hex in HexRuns().Matches(Compact).Select(match => match.Value))
            for (var i = 0; i <= hex.Length - 8; i++) Terms.Add(string.Concat("uuid:", hex.AsSpan(i, 8)));
    }

    public static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value.ToUpperInvariant()) if (char.IsLetterOrDigit(character)) text.Append(character);
        return text.ToString();
    }

    public static IEnumerable<string> References(MatchingInvoice invoice)
    {
        if (!string.IsNullOrEmpty(invoice.Serie) && !string.IsNullOrEmpty(invoice.Folio)) yield return Normalize(invoice.Serie + invoice.Folio);
        if (invoice.Folio is { Length: >= 4 } folio && folio.All(char.IsAsciiDigit)) yield return folio;
        var uuid = invoice.Uuid.ToString("N").ToUpperInvariant();
        for (var i = 0; i <= uuid.Length - 8; i++) yield return string.Concat("uuid:", uuid.AsSpan(i, 8));
    }

    [GeneratedRegex(@"[\p{L}\p{N}&]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    [GeneratedRegex(@"[0-9A-F]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex HexRuns();
}
