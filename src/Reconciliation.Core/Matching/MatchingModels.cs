namespace Reconciliation.Core.Matching;

public sealed record MatchingMovement(Guid Id, DateOnly Date, decimal Credit, decimal Debit, string Description,
    string? Reference = null, string? Classification = null);
public sealed record MatchingInvoice(Guid Id, Guid Uuid, DateOnly Date, decimal Total, decimal Retained,
    string IssuerRfc, string ReceiverRfc, string Type = "I", string Direction = "issued", string Currency = "MXN",
    string? Serie = null, string? Folio = null, decimal CreditNotes = 0)
{
    public decimal Payable => Math.Max(0, Total - CreditNotes);
}
public sealed record MatchingPaymentDocument(Guid InvoiceUuid, decimal Paid, string Currency = "MXN", bool Inconsistent = false);
public sealed record MatchingPayment(Guid Id, Guid CfdiId, DateOnly Date, decimal Amount, string? OperationNumber,
    IReadOnlyList<MatchingPaymentDocument> Documents, string Direction = "issued", string Currency = "MXN", string Version = "2.0", bool Inconsistent = false);
public sealed record MatchingSettings(int DateToleranceDays, decimal AmountTolerance);
public sealed record MatchingCriterion(string Code, int Points, string Detail);
public sealed record MatchingLink(Guid MovementId, Guid InvoiceId, Guid? PaymentCfdiId, Guid? PaymentRef, decimal Amount,
    string State, string Origin, IReadOnlyList<MatchingCriterion> Criteria, string? Confidence, string Explanation, bool Reopened = false, Guid? Id = null, DateTimeOffset? CreatedAt = null);
public sealed record MatchingBalance(Guid InvoiceId, decimal ReconciledAmount, decimal SuggestedAmount, decimal PendingAmount, decimal UncoveredAmount);
public sealed record MatchingLinkUpdate(MatchingLink Before, MatchingLink After, string Action);
public sealed record MatchingResult(IReadOnlyList<MatchingLink> Changes, long CandidateComparisons, IReadOnlyList<MatchingBalance> Invoices, IReadOnlyList<MatchingLinkUpdate> Updates);

public sealed record InvoiceMatchingState(string State, decimal CreditNotes, decimal Payable, decimal ReconciledAmount,
    decimal SuggestedAmount, decimal PendingAmount, decimal UncoveredAmount, decimal PaidPercentage,
    string[] Reasons, string[] InformationalReasons, bool RequiresReview)
{
    // Npgsql array metadata is System.Array; use property mapping rather than exact constructor matching.
    public InvoiceMatchingState() : this("pending", 0, 0, 0, 0, 0, 0, 0, [], [], false) { }
}
