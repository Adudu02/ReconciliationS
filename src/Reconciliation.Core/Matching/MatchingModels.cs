namespace Reconciliation.Core.Matching;

public sealed record MatchingMovement(Guid Id, DateOnly Date, decimal Credit, decimal Debit, string Description,
    string? Reference = null, string? Classification = null);
public sealed record MatchingInvoice(Guid Id, Guid Uuid, DateOnly Date, decimal Total, decimal Retained,
    string IssuerRfc, string ReceiverRfc, string Type = "I", string Direction = "issued", string Currency = "MXN",
    string? Serie = null, string? Folio = null);
public sealed record MatchingPaymentDocument(Guid InvoiceUuid, decimal Paid, string Currency = "MXN", bool Inconsistent = false);
public sealed record MatchingPayment(Guid Id, Guid CfdiId, DateOnly Date, decimal Amount, string? OperationNumber,
    IReadOnlyList<MatchingPaymentDocument> Documents, string Direction = "issued", string Currency = "MXN", string Version = "2.0", bool Inconsistent = false);
public sealed record MatchingSettings(int DateToleranceDays, decimal AmountTolerance);
public sealed record MatchingCriterion(string Code, int Points, string Detail);
public sealed record MatchingLink(Guid MovementId, Guid InvoiceId, Guid? PaymentCfdiId, Guid? PaymentRef, decimal Amount,
    string State, string Origin, IReadOnlyList<MatchingCriterion> Criteria, string? Confidence, string Explanation, bool Reopened = false);
public sealed record MatchingBalance(Guid InvoiceId, decimal ReconciledAmount, decimal SuggestedAmount, decimal PendingAmount, decimal UncoveredAmount);
public sealed record MatchingResult(IReadOnlyList<MatchingLink> Changes, long CandidateComparisons, IReadOnlyList<MatchingBalance> Invoices);
