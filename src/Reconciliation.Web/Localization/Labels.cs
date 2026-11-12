using System.Text.RegularExpressions;
using Reconciliation.Contracts;

namespace Reconciliation.Web.Localization;

/// <summary>Display text for machine values the API emits. Unknown values pass through raw so a new server value never hides data.</summary>
public sealed partial class Labels(Localizer t)
{
    public string ImportStatus(string v) => Lookup("imports.status", v);

    public string State(string v) => Lookup("state", v);

    public string Confidence(string? v) => v is null ? "" : Lookup("confidence", v);

    public string Role(string v) => Lookup("role", v);

    public string Direction(string v) => Lookup("direction", v);

    public string JobState(string v) => Lookup("jobstate", v);

    public string ComplementStatus(string v) => Lookup("complement", v);

    public string Classification(string? v) => v is null ? "" : Lookup("classification", v);

    /// <summary>Label chosen by the stable criterion code; an unknown code keeps the server's own detail text.</summary>
    public string Criterion(ReviewCriterion c)
    {
        var known = Lookup("criterion", c.Code, c.Detail);
        switch (c.Code)
        {
            case "amount-equals-total" when c.Detail == "amount equals net payable after credit notes":
                return t["criterion.amount-equals-total.credit-notes"];
            case "amount-within-tolerance" when ToleranceDetail().Match(c.Detail) is { Success: true } tolerance
                && TargetKey(tolerance.Groups["target"].Value) is { } target:
                return t.Format("criterion.amount-within-tolerance.detail", t[target], tolerance.Groups["cents"].Value);
            case "date-proximity" when ProximityDetail().Match(c.Detail) is { Success: true } proximity:
                return t.Format("criterion.date-proximity.bonus", proximity.Groups["bonus"].Value);
            default:
                return known;
        }
    }

    public string Reason(string code) => Lookup("reason", code);

    /// <summary>
    /// Rebuilds the server explanation from the localized criteria plus the equal-match count. Any other shape
    /// (extra appended text, no criteria, a different criteria text) is returned exactly as the server sent it.
    /// </summary>
    public string Explanation(ReviewEntry e)
    {
        if (e.Criteria.Length == 0) return e.Explanation;

        var body = e.Explanation;
        string? suffix = null;
        if (EqualMatches().Match(e.Explanation) is { Success: true } equal)
        {
            body = equal.Groups["body"].Value;
            suffix = t.Format("explanation.equal-matches", equal.Groups["n"].Value);
        }
        else if (CappedMatches().Match(e.Explanation) is { Success: true } capped)
        {
            body = capped.Groups["body"].Value;
            suffix = t.Format("explanation.capped-matches", capped.Groups["n"].Value);
        }

        if (body != string.Join("; ", e.Criteria.Select(criterion => criterion.Detail))) return e.Explanation;

        var text = string.Join("; ", e.Criteria.Select(Criterion));
        return suffix is null ? text : text + "; " + suffix;
    }

    private static string? TargetKey(string name) => name switch
    {
        "total" => "criterion.target.total",
        "gross (amount equals total before retentions)" => "criterion.target.gross",
        "remaining payable" => "criterion.target.remaining",
        _ => null,
    };

    private string Lookup(string prefix, string value, string? fallback = null)
    {
        var key = prefix + "." + value.Replace(' ', '-');
        var text = t[key];
        return text == key ? fallback ?? value : text;
    }

    [GeneratedRegex(@"^amount within tolerance of (?<target>.+) target; difference (?<cents>[0-9.,]+) cents$", RegexOptions.Singleline)]
    private static partial Regex ToleranceDetail();

    [GeneratedRegex(@"^invoice date is within tolerance; proximity bonus (?<bonus>-?\d+)$", RegexOptions.Singleline)]
    private static partial Regex ProximityDetail();

    [GeneratedRegex(@"^(?<body>.*); (?<n>\d+) other invoices matched equally$", RegexOptions.Singleline)]
    private static partial Regex EqualMatches();

    [GeneratedRegex(@"^(?<body>.*); (?<n>\d+)\+ others may have matched equally$", RegexOptions.Singleline)]
    private static partial Regex CappedMatches();
}
