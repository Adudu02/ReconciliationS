namespace Reconciliation.Core.Ingestion;

public sealed record BankStatementLine(DateTime MovementDate, string Description, decimal Credit, decimal Debit, decimal Balance, string? Reference);

public interface IBankStatementParser
{
    string Bank { get; }
    IReadOnlyList<BankStatementLine> Parse(byte[] csvBytes, int? length = null);
}
