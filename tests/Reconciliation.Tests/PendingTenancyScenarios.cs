namespace Reconciliation.Tests;

public sealed class PendingTenancyScenarios
{
    [Fact(DisplayName = "Viewer cannot decide", Skip = "pending group 8: confirm a real suggested link and verify nothing changes")]
    public void Viewer_cannot_decide_on_real_link() => Assert.Fail("Replace with real link-decision scenario.");

    [Fact(DisplayName = "Different roles", Skip = "pending group 8: successful link decision in A and read-only access in B")]
    public void Different_roles_on_real_links() => Assert.Fail("Replace with real link-decision scenario.");
    [Fact(DisplayName = "Cross-company attempt in the test suite", Skip = "pending group 3: request a real company B invoice with company A credentials")]
    public void Real_invoice_cross_company_read() => Assert.Fail("Replace with real invoice-read scenario.");

    [Fact(DisplayName = "Tolerance changed", Skip = "pending group 6: persisted settings apply to the next reconciliation run")]
    public void Settings_apply_to_next_reconciliation_run() => Assert.Fail("Replace with real reconciliation-run scenario.");

    [Fact(DisplayName = "Delete attempt", Skip = "pending group 3: imported CFDI remains unchanged after an unavailable DELETE")]
    public void Imported_CFDI_retained_on_delete_attempt() => Assert.Fail("Replace with real imported CFDI retention scenario.");
}
