namespace Reconciliation.Web.Localization;

public static partial class Catalog
{
    public static IReadOnlyDictionary<string, string> En { get; } = new Dictionary<string, string>
    {
        ["nav.overview"] = "Overview",
        ["nav.review"] = "Review queue",
        ["nav.invoices"] = "Invoices",
        ["nav.movements"] = "Movements",
        ["nav.imports"] = "Import files",

        ["topbar.switch-company"] = "Switch company",
        ["topbar.sign-out"] = "Sign out",
        ["topbar.demo"] = "Demo",

        ["login.title"] = "Sign in",
        ["login.eyebrow"] = "Access",
        ["login.intro"] = "Sign in with your email and password to review your company's reconciliation.",
        ["login.email"] = "Email",
        ["login.password"] = "Password",
        ["login.sign-in"] = "Sign in",
        ["login.signing-in"] = "Signing in…",
        ["login.service-unavailable"] = "The service is unavailable right now. Try again in a few minutes.",

        ["notice.disclaimer"] = "Demo: results have no fiscal or legal validity.",

        ["import.files-selected"] = "{0} file(s) selected",

        ["state.pending"] = "Pending",
        ["state.suggested"] = "Suggested",
        ["state.reconciled"] = "Reconciled",
        ["state.received"] = "Received",
        ["state.overdue"] = "Overdue",
        ["state.missing"] = "Missing",
        ["state.rejected"] = "Rejected",
        ["state.cancelled"] = "Cancelled",
        ["state.closed"] = "Closed",

        ["confidence.high"] = "High",
        ["confidence.medium"] = "Medium",
        ["confidence.low"] = "Low",

        ["role.administrator"] = "Administrator",
        ["role.accountant"] = "Accountant",
        ["role.viewer"] = "Viewer",

        ["direction.issued"] = "Issued",
        ["direction.received"] = "Received",

        ["jobstate.queued"] = "Queued",
        ["jobstate.running"] = "Running",
        ["jobstate.succeeded"] = "Completed",
        ["jobstate.failed"] = "Failed",

        ["complement.received"] = "Received",
        ["complement.overdue"] = "Overdue",
        ["complement.missing"] = "Missing",

        ["classification.loan"] = "Loan",
        ["classification.partner_contribution"] = "Partner contribution",
        ["classification.tax_payment"] = "Tax payment",
        ["classification.late_invoicing"] = "Late invoicing",
        ["classification.other"] = "Other",

        ["criterion.payment-complement"] = "issued payment complement references invoice UUID; exact movement amount and payment date within tolerance",
        ["criterion.identifier-without-amount"] = "identifier without amount match; allocation is limited by both remaining capacities",
        ["criterion.rfc"] = "issuer or receiver RFC found in the description",
        ["criterion.invoice-reference"] = "invoice serie/folio or UUID fragment found in text",
        ["criterion.operation-number"] = "reference equals operation number",
        ["criterion.amount-equals-total"] = "amount equals CFDI net total",
        ["criterion.amount-equals-total.credit-notes"] = "amount equals net payable after credit notes",
        ["criterion.gross-payment"] = "amount equals total before retentions",
        ["criterion.amount-equals-remaining"] = "amount equals remaining payable",
        ["criterion.amount-within-tolerance"] = "amount within tolerance of the expected amount",
        ["criterion.amount-within-tolerance.detail"] = "amount within tolerance of {0} target; difference {1} cents",
        ["criterion.target.total"] = "total",
        ["criterion.target.gross"] = "gross (amount equals total before retentions)",
        ["criterion.target.remaining"] = "remaining payable",
        ["criterion.date-proximity"] = "invoice date is within tolerance",
        ["criterion.date-proximity.bonus"] = "invoice date is within tolerance; proximity bonus {0}",
        ["criterion.replacement-of-cancelled-invoice"] = "replacement of cancelled invoice",

        ["reason.fully-credited"] = "Fully credited by credit notes",
        ["reason.global-invoice"] = "Global invoice",
        ["reason.overpaid"] = "Overpaid",
        ["reason.cancelled-credit-note"] = "Cancelled credit note",
        ["reason.credit-note-in-a-different-currency"] = "Credit note in a different currency",
        ["reason.cancelled-invoice"] = "Cancelled invoice",
        ["reason.cancelled-payment-complement"] = "Cancelled payment complement",
        ["reason.complement-overdue"] = "Payment complement overdue",
        ["reason.efos-warning"] = "SAT EFOS list warning",
        ["reason.inconsistent-balance"] = "Inconsistent balance in the payment complement",

        ["explanation.equal-matches"] = "{0} other invoices matched equally",
        ["explanation.capped-matches"] = "{0}+ others may have matched equally",
    };
}
