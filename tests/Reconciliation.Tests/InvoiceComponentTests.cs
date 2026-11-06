using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.ViewModels;

namespace Reconciliation.Tests;

public sealed class InvoiceComponentTests
{
    [Fact]
    public async Task Summary_shows_worked_amounts_both_movements_payment_CFDI_and_cash_warning()
    {
        await using var context = new BunitContext();
        var summary = Summary();
        Register(context, _ => WebTestHandler.Json(summary));
        var page = context.Render<InvoiceDetail>(p => p.Add(x => x.InvoiceId, summary.Id));
        page.WaitForAssertion(() => Assert.Contains("50%", page.Markup, StringComparison.Ordinal));
        Assert.Contains("5,800.00", page.Find("[data-summary=pending]").TextContent, StringComparison.Ordinal);
        Assert.Contains("11,600.00", page.Markup, StringComparison.Ordinal);
        Assert.Equal(2, page.FindAll("tbody tr").Count);
        Assert.Contains(summary.Links[0].PaymentCfdiId!.Value.ToString(), page.Markup, StringComparison.Ordinal);
        Assert.Contains("received", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Cash payment above 2,000.00", page.Markup, StringComparison.Ordinal);
        Assert.Contains("global invoice", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_overdue_complement_due_date_is_visible_in_summary()
    {
        await using var context = new BunitContext();
        var summary = Summary();
        summary = summary with { Links = [summary.Links[0] with { ComplementStatus = "overdue", DueDate = new(2026, 11, 5), PaymentCfdiId = null, PaymentRef = null }] };
        Register(context, _ => WebTestHandler.Json(summary));
        var page = context.Render<InvoiceDetail>(p => p.Add(x => x.InvoiceId, summary.Id));
        page.WaitForAssertion(() => Assert.Contains("2026-11-05", page.Markup, StringComparison.Ordinal));
        Assert.Contains("overdue", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoice_filters_are_sent_to_API_and_keyset_paging_keeps_them()
    {
        await using var context = new BunitContext();
        var calls = new List<string>();
        var first = new InvoiceItem(Guid.NewGuid(), Guid.NewGuid(), "I", "issued", new(2026, 10, 2), 11600, "pending", 5800, new(2026, 11, 5), true);
        Register(context, request =>
        {
            calls.Add(request.RequestUri!.Query);
            return WebTestHandler.Json(calls.Count == 1 ? new InvoicePage([first], first.IssuedAt, first.Id) : new InvoicePage([first with { Id = Guid.NewGuid(), Uuid = Guid.NewGuid() }], null, null));
        });
        var model = context.Services.GetRequiredService<InvoiceViewModel>();
        model.State = "pending";
        model.ComplementStatus = "overdue";
        await model.LoadAsync();
        await model.NextAsync();
        Assert.Equal(2, model.Invoices.Length);
        Assert.All(calls, query => { Assert.Contains("state=pending", query, StringComparison.Ordinal); Assert.Contains("complementStatus=overdue", query, StringComparison.Ordinal); });
        Assert.Contains("afterIssuedAt=", calls[1], StringComparison.Ordinal);
        Assert.Contains("afterId=", calls[1], StringComparison.Ordinal);
    }

    private static void Register(BunitContext context, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var session = new SessionState();
        session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic", null, "viewer"));
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(respond)) { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<InvoiceViewModel>();
    }

    private static InvoiceSummary Summary()
    {
        var link = new SummaryLink(Guid.NewGuid(), Guid.NewGuid(), new(2026, 10, 10), "Synthetic first payment", 5800, 5800, "reconciled", "high", Guid.NewGuid(), Guid.NewGuid(), "received", null, new(2026, 10, 10), "01", "Cash payment above 2,000.00: results have no fiscal validity and deductibility must be checked");
        return new(Guid.NewGuid(), Guid.NewGuid(), 11600, 0, 11600, 5800, 50, 5800, 0, "suggested", "issued", "PPD", "current", null, false,
            [new("global invoice", false)], false, [link, link with { Id = Guid.NewGuid(), MovementId = Guid.NewGuid(), Description = "Synthetic second payment", State = "suggested", ComplementStatus = "missing", PaymentCfdiId = null, PaymentRef = null, CashPaymentWarning = null }]);
    }
}
