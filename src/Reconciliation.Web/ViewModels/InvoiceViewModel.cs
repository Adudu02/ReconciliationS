using System.Globalization;
using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class InvoiceViewModel(ApiClient api) : PageViewModel
{
    public string State { get; set; } = "";
    public string ComplementStatus { get; set; } = "";
    public InvoiceItem[] Invoices { get; private set; } = [];
    public InvoiceSummary? Summary { get; private set; }
    private InvoicePage? page;
    public bool HasMore => page?.NextAfterId is not null;
    public Task LoadAsync() => RunAsync(() => LoadCoreAsync(false));
    public Task NextAsync() => RunAsync(() => LoadCoreAsync(true));
    private async Task LoadCoreAsync(bool next)
    {
        var query = "cfdi?type=I&limit=50";
        if (State.Length > 0) query += "&state=" + Uri.EscapeDataString(State);
        if (ComplementStatus.Length > 0) query += "&complementStatus=" + Uri.EscapeDataString(ComplementStatus);
        if (next && page?.NextAfterId is not null) query += "&afterIssuedAt=" + Uri.EscapeDataString(page.NextAfterIssuedAt!.Value.ToString("O", CultureInfo.InvariantCulture)) + "&afterId=" + page.NextAfterId;
        page = await api.GetAsync<InvoicePage>(api.CompanyPath(query));
        Invoices = next ? [.. Invoices, .. page.Invoices] : page.Invoices;
    }
    public Task LoadSummaryAsync(Guid invoiceId) => RunAsync(async () =>
    {
        Summary = null;
        Summary = await api.GetAsync<InvoiceSummary>(api.CompanyPath($"cfdi/{invoiceId}/summary"));
    });
}
