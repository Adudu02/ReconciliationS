using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

internal static class SyntheticMatching
{
    public static readonly Guid InvoiceId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid InvoiceUuid = Guid.Parse("00000000-0000-4000-8000-000000000001");
    public static readonly Guid FirstMovementId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid SecondMovementId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid PaymentId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public static readonly Guid PaymentCfdiId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static MatchingInvoice Invoice(decimal total = 11600m) => new(InvoiceId, InvoiceUuid, new(2026, 10, 2), total, 0,
        "AAA010101AAA", "XEXX010101000");
    public static MatchingMovement FirstMovement(decimal amount = 5800m) => new(FirstMovementId, new(2026, 10, 10), amount, 0, "SPEI REC 0034 PAGO FACT 0001");
    public static MatchingMovement SecondMovement() => new(SecondMovementId, new(2026, 10, 20), 5800, 0, "SPEI REC 0071 XEXX COMERCIAL");
    public static MatchingPayment Payment(decimal paid = 5800m) => new(PaymentId, PaymentCfdiId, new(2026, 10, 10), paid,
        "ABC123", [new(InvoiceUuid, paid)]);
}
