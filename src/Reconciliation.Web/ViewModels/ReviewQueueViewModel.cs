using System.Globalization;
using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class ReviewQueueViewModel(ApiClient api, SessionState session) : PageViewModel
{
    public ReviewEntry[] Entries { get; private set; } = [];
    public InvoiceItem[] Invoices { get; private set; } = [];
    private ReviewPage? page;
    private InvoicePage? invoicePage;
    public bool HasMore => page?.NextAfterId is not null;
    public bool HasMoreInvoices => invoicePage?.NextAfterId is not null;
    public Task LoadAsync() => RunAsync(() => LoadCoreAsync(false));
    public Task NextAsync() => RunAsync(() => LoadCoreAsync(true));
    private async Task LoadCoreAsync(bool next)
    {
        var cursor = next && page?.NextAfterId is not null ? $"&afterRank={page.NextAfterRank}&afterDate={page.NextAfterDate:yyyy-MM-dd}&afterId={page.NextAfterId}" : "";
        page = await api.GetAsync<ReviewPage>(api.CompanyPath("review-queue?limit=50" + cursor));
        Entries = next ? [.. Entries, .. page.Entries] : page.Entries;
    }
    public Task LoadInvoicesAsync(bool next = false) => RunAsync(async () =>
    {
        var cursor = next && invoicePage?.NextAfterId is not null ? "&afterIssuedAt=" + Uri.EscapeDataString(invoicePage.NextAfterIssuedAt!.Value.ToString("O", CultureInfo.InvariantCulture)) + "&afterId=" + invoicePage.NextAfterId : "";
        invoicePage = await api.GetAsync<InvoicePage>(api.CompanyPath("cfdi?type=I&direction=issued&limit=200" + cursor));
        Invoices = next ? [.. Invoices, .. invoicePage.Invoices] : invoicePage.Invoices;
    });
    public Task DecideAsync(Guid linkId, DecisionRequest request) => RunAsync(async () =>
    {
        if (!session.CanWrite) { Error = new("error.read-only"); return; }
        if (request.Type == "change" && (request.NewCfdiId is null || request.AmountApplied is not > 0 || decimal.Round(request.AmountApplied.Value, 2) != request.AmountApplied))
        { Error = new("error.choose-amount"); return; }
        try { await api.DecideAsync(linkId, request); }
        catch (ApiProblemException problem) when (problem.Status == 409) { await LoadCoreAsync(false); throw; }
        await LoadCoreAsync(false);
    });
}
