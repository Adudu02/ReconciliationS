using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Reconciliation.Core.Ingestion;

public sealed partial class ReferenceBankStatementParser(IngestionOptions options) : IBankStatementParser
{
    public string Bank => "reference";

    public IReadOnlyList<BankStatementLine> Parse(byte[] content, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream(content, 0, length ?? content.Length, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        using var csv = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        csv.SetDelimiters(",");
        var rows = new List<BankStatementLine>();
        try
        {
            var header = csv.ReadFields();
            if (header is null || !header.SequenceEqual(["fecha", "concepto", "abono", "cargo", "saldo"], StringComparer.Ordinal))
                throw new InvalidDataException("Line 1: expected header fecha,concepto,abono,cargo,saldo.");
            while (!csv.EndOfData)
            {
                if (rows.Count >= options.MaxStatementLines) throw new InvalidDataException("Statement exceeds MaxStatementLines.");
                var lineNumber = csv.LineNumber;
                var fields = csv.ReadFields()!;
                if (fields.Length != 5) throw new InvalidDataException($"Line {lineNumber}: expected five columns.");
                if (!DateTime.TryParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new InvalidDataException($"Line {lineNumber}: invalid date; expected yyyy-MM-dd.");
                var description = fields[1].Trim();
                if (description.Length > options.MaxDescriptionLength) throw new InvalidDataException($"Line {lineNumber}: description exceeds {options.MaxDescriptionLength} characters.");
                var credit = Amount(fields[2], lineNumber, signed: false, emptyZero: true);
                var debit = Amount(fields[3], lineNumber, signed: false, emptyZero: true);
                var balance = Amount(fields[4], lineNumber, signed: true, emptyZero: false);
                if (description.Length == 0 || !((credit > 0 && debit == 0) || (debit > 0 && credit == 0)))
                    throw new InvalidDataException($"Line {lineNumber}: description and exactly one positive credit/debit are required.");
                var reference = TrackingKey().Match(description);
                if (!reference.Success) reference = ReferenceNumber().Match(description);
                rows.Add(new BankStatementLine(date, description, credit, debit, balance, reference.Success ? reference.Groups[1].Value : null));
            }
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Line 1: expected UTF-8 CSV.", error); }
        catch (MalformedLineException error) { throw new InvalidDataException($"Line {csv.ErrorLineNumber}: malformed quoted CSV.", error); }
        if (rows.Count == 0) throw new InvalidDataException("Line 2: statement contains no movements.");
        return rows;
    }

    private static decimal Amount(string text, long line, bool signed, bool emptyZero)
    {
        if (emptyZero && text.Length == 0) return 0;
        if (!NumberFormat().IsMatch(text) || (!signed && text.StartsWith('-')) ||
            !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount is < -9999999999999999.99m or > 9999999999999999.99m)
            throw new InvalidDataException($"Line {line}: invalid amount; use a decimal point and at most two decimals.");
        return amount;
    }

    [GeneratedRegex(@"\A-?[0-9]+(?:\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberFormat();
    [GeneratedRegex(@"(?:^|\s)RASTREO:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex TrackingKey();
    [GeneratedRegex(@"(?:^|\s)REF:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceNumber();
}
