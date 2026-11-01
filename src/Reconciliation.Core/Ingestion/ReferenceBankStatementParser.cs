using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Reconciliation.Core.Common;

namespace Reconciliation.Core.Ingestion;

public sealed partial class ReferenceBankStatementParser(IngestionOptions options) : IBankStatementParser
{
    public string Bank => "reference";

    public IReadOnlyList<BankStatementLine> Parse(byte[] csvBytes, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(csvBytes);
        using var stream = new MemoryStream(csvBytes, 0, length ?? csvBytes.Length, writable: false);
        using var csvTextReader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        using var csvReader = new TextFieldParser(csvTextReader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        csvReader.SetDelimiters(",");
        var statementLines = new List<BankStatementLine>();
        try
        {
            var headerFields = csvReader.ReadFields();
            if (headerFields is null || !headerFields.SequenceEqual(["fecha", "concepto", "abono", "cargo", "saldo"], StringComparer.Ordinal))
                throw new InvalidDataException("Line 1: expected header fecha,concepto,abono,cargo,saldo.");
            while (!csvReader.EndOfData)
            {
                if (statementLines.Count >= options.MaxStatementLines) throw new InvalidDataException("Statement exceeds MaxStatementLines.");
                var lineNumber = csvReader.LineNumber;
                var columnValues = csvReader.ReadFields()!;
                if (columnValues.Length != 5) throw new InvalidDataException($"Line {lineNumber}: expected five columns.");
                if (!DateTime.TryParseExact(columnValues[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var movementDate))
                    throw new InvalidDataException($"Line {lineNumber}: invalid date; expected yyyy-MM-dd.");
                var description = columnValues[1].Trim();
                if (description.Length > options.MaxDescriptionLength) throw new InvalidDataException($"Line {lineNumber}: description exceeds {options.MaxDescriptionLength} characters.");
                var creditAmount = CreditOrDebit(columnValues[2], lineNumber);
                var debitAmount = CreditOrDebit(columnValues[3], lineNumber);
                var runningBalance = Balance(columnValues[4], lineNumber);
                if (description.Length == 0 || !((creditAmount > 0 && debitAmount == 0) || (debitAmount > 0 && creditAmount == 0)))
                    throw new InvalidDataException($"Line {lineNumber}: description and exactly one positive credit/debit are required.");
                var referenceMatch = TrackingKey().Match(description);
                if (!referenceMatch.Success) referenceMatch = ReferenceNumber().Match(description);
                statementLines.Add(new BankStatementLine(movementDate, description, creditAmount, debitAmount, runningBalance, referenceMatch.Success ? referenceMatch.Groups[1].Value : null));
            }
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Line 1: expected UTF-8 CSV.", error); }
        catch (MalformedLineException error) { throw new InvalidDataException($"Line {csvReader.ErrorLineNumber}: malformed quoted CSV.", error); }
        if (statementLines.Count == 0) throw new InvalidDataException("Line 2: statement contains no movements.");
        return statementLines;
    }

    private static decimal CreditOrDebit(string declaredAmount, long lineNumber)
    {
        if (declaredAmount.Length == 0) return 0;
        if (declaredAmount.StartsWith('-')) throw InvalidAmount(lineNumber);
        return Balance(declaredAmount, lineNumber);
    }

    private static InvalidDataException InvalidAmount(long lineNumber) =>
        new($"Line {lineNumber}: invalid amount; use a decimal point and at most two decimals.");

    private static decimal Balance(string declaredAmount, long lineNumber)
    {
        if (!NumberFormat().IsMatch(declaredAmount) ||
            !decimal.TryParse(declaredAmount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount is < -Money.MaxAmount or > Money.MaxAmount)
            throw InvalidAmount(lineNumber);
        return amount;
    }

    [GeneratedRegex(@"\A-?[0-9]+(?:\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberFormat();
    [GeneratedRegex(@"(?:^|\s)RASTREO:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex TrackingKey();
    [GeneratedRegex(@"(?:^|\s)REF:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceNumber();
}
