namespace Reconciliation.Tests;

public sealed class PendingTenancyScenarios
{
    [Fact(DisplayName = "Viewer cannot decide", Skip = "pending group 8: confirm a real suggested link and verify nothing changes")]
    public void Viewer_cannot_decide_on_real_link() => Assert.Fail("Replace with real link-decision scenario.");

    [Fact(DisplayName = "Different roles", Skip = "pending group 8: successful link decision in A and read-only access in B")]
    public void Different_roles_on_real_links() => Assert.Fail("Replace with real link-decision scenario.");
    [Fact(DisplayName = "Cross-company attempt in the test suite", Skip = "pending group 3: request a real company B invoice with company A credentials")]
    public void Real_invoice_cross_company_read() => Assert.Fail("Replace with real invoice-read scenario.");
}

