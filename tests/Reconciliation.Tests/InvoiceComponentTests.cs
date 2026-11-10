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
        TestLanguage.Register(context);
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
        Assert.Contains("Global invoice", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_overdue_complement_due_date_is_visible_in_summary()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context);
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
        TestLanguage.Register(context);
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

    [Fact]
    public async Task Filter_options_keep_machine_values_in_spanish()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        Register(context, _ => WebTestHandler.Json(new InvoicePage([], null, null)));
        var page = context.Render<Invoices>();
        page.WaitForAssertion(() => Assert.Contains("No hay facturas que coincidan con estos filtros.", page.Markup, StringComparison.Ordinal));
        var states = page.Find("#state").QuerySelectorAll("option");
        Assert.Equal(["", "pending", "suggested", "reconciled"], states.Select(o => o.GetAttribute("value")).ToArray());
        Assert.Equal(["Todos los estados", "Pendiente", "Sugerido", "Conciliado"], states.Select(o => o.TextContent).ToArray());
        var complements = page.Find("#complement").QuerySelectorAll("option");
        Assert.Equal(["", "missing", "overdue"], complements.Select(o => o.GetAttribute("value")).ToArray());
        Assert.Equal(["Todos los complementos", "Faltante (incluye vencido)", "Vencido"], complements.Select(o => o.TextContent).ToArray());
        Assert.Equal("CFDI DE INGRESO", page.Find(".eyebrow").TextContent);
        Assert.Equal("Facturas", page.Find("h1").TextContent);
        Assert.Equal("Aplicar filtros", page.Find("form button[type=submit]").TextContent);
        Assert.Equal("Lista de facturas", page.Find(".table-wrap").GetAttribute("aria-label"));
        Assert.Equal("0", page.Find(".table-wrap").GetAttribute("tabindex"));
        Assert.Equal("region", page.Find(".table-wrap").GetAttribute("role"));
        Assert.Equal(["Factura", "Emisión", "Dirección", "Estado", "Total", "Pendiente", "Vencimiento del complemento"], page.FindAll("thead th").Select(h => h.TextContent).ToArray());
        Assert.Equal("Estado", page.Find("label[for=state]").TextContent);
        Assert.Equal("Estado del complemento", page.Find("label[for=complement]").TextContent);

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        page.WaitForAssertion(() => Assert.Equal("Invoices", page.Find("h1").TextContent));
        Assert.Equal(["", "pending", "suggested", "reconciled"], page.Find("#state").QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToArray());
        Assert.Equal(["All states", "Pending", "Suggested", "Reconciled"], page.Find("#state").QuerySelectorAll("option").Select(o => o.TextContent).ToArray());
    }

    [Fact]
    public async Task Invoice_rows_localize_labels_but_keep_money_dates_and_machine_classes()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var item = new InvoiceItem(Guid.NewGuid(), Guid.NewGuid(), "I", "issued", new(2026, 10, 2), 11600, "pending", 5800, new(2026, 11, 5), true);
        Register(context, _ => WebTestHandler.Json(new InvoicePage([item], null, null)));
        var page = context.Render<Invoices>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        var cells = page.Find("tbody tr").QuerySelectorAll("td");
        Assert.Equal("Emitida", cells[1].TextContent);
        Assert.Equal("Pendiente", cells[2].TextContent);
        Assert.Equal("badge pending", cells[2].QuerySelector(".badge")!.ClassName);
        Assert.Equal("11,600.00", cells[3].TextContent);
        Assert.Equal("5,800.00", cells[4].TextContent);
        Assert.Contains("2026-11-05", cells[5].TextContent, StringComparison.Ordinal);
        Assert.Equal("Vencido", cells[5].QuerySelector(".badge.overdue")!.TextContent);
        Assert.Equal("2026-10-02", cells[0].TextContent);
    }

    [Fact]
    public async Task Unknown_state_in_a_table_row_renders_raw()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var item = new InvoiceItem(Guid.NewGuid(), Guid.NewGuid(), "I", "sideways", new(2026, 10, 2), 11600, "brand_new_state", null, null, null);
        Register(context, _ => WebTestHandler.Json(new InvoicePage([item], null, null)));
        var page = context.Render<Invoices>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        var badge = page.Find("tbody .badge");
        Assert.Equal("brand_new_state", badge.TextContent);
        Assert.Equal("badge brand_new_state", badge.ClassName);
        Assert.Equal("sideways", page.Find("tbody tr").QuerySelectorAll("td")[1].TextContent);
    }

    [Fact]
    public async Task Cash_payment_warning_from_server_is_shown_verbatim()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var summary = Summary();
        Register(context, _ => WebTestHandler.Json(summary));
        var page = context.Render<InvoiceDetail>(p => p.Add(x => x.InvoiceId, summary.Id));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[role=note]")));
        var warning = page.Find("[lang=en]");
        Assert.Equal(summary.Links[0].CashPaymentWarning, warning.TextContent);
        Assert.Single(page.FindAll("[lang=en]"));
    }

    [Fact]
    public async Task Money_and_dates_are_not_localized()
    {
        static async Task<(string[] Amounts, string[] Dates)> Render(string language)
        {
            await using var context = new BunitContext();
            TestLanguage.Register(context, language);
            var summary = Summary();
            Register(context, _ => WebTestHandler.Json(summary));
            var page = context.Render<InvoiceDetail>(p => p.Add(x => x.InvoiceId, summary.Id));
            page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
            var amounts = page.FindAll(".summary-values dd").Select(d => d.TextContent).Concat(page.FindAll("td.money").Select(d => d.TextContent)).ToArray();
            var dates = page.FindAll("tbody th[scope=row]").Select(h => h.TextContent[..10]).ToArray();
            return (amounts, dates);
        }

        var es = await Render("es");
        var en = await Render("en");
        Assert.Equal(en.Amounts, es.Amounts);
        Assert.Equal(en.Dates, es.Dates);
        Assert.Contains("11,600.00", es.Amounts);
        Assert.Contains("5,800.00", es.Amounts);
        Assert.Contains("50%", es.Amounts);
        Assert.All(es.Dates, date => Assert.Equal("2026-10-10", date));
    }

    [Fact]
    public async Task Invoice_detail_is_spanish_and_keeps_smoke_hooks()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var summary = Summary() with { Reasons = [new("global invoice", false), new("overpaid", true)], CancellationStatus = null };
        Register(context, _ => WebTestHandler.Json(summary));
        var page = context.Render<InvoiceDetail>(p => p.Add(x => x.InvoiceId, summary.Id));
        page.WaitForAssertion(() => Assert.Contains("50%", page.Markup, StringComparison.Ordinal));
        Assert.Equal("← Facturas", page.Find("a[href='/ui/invoices']").TextContent);
        Assert.Equal("Resumen de la factura", page.Find("h1").TextContent);
        Assert.Equal(["Total de la factura", "Notas de crédito aplicadas", "Neto por cobrar", "Cobrado confirmado", "Porcentaje cobrado", "Pendiente", "No cubierto por vínculos"], page.FindAll(".summary-values dt").Select(d => d.TextContent).ToArray());
        Assert.Equal("5,800.00", page.Find("[data-summary=pending]").TextContent);
        Assert.Contains(page.FindAll("dd"), dd => dd.TextContent == "50%");
        Assert.Equal(2, page.FindAll("tbody tr").Count);
        Assert.NotEmpty(page.FindAll(".badge.suggested"));
        Assert.Contains("PPD", page.Markup, StringComparison.Ordinal);
        var status = page.FindAll(".resource-grid > section")[1];
        Assert.Contains("SAT: current", status.TextContent, StringComparison.Ordinal);
        Assert.Contains("Cancelación: Sin información de cancelación", status.TextContent, StringComparison.Ordinal);
        Assert.Contains("Alerta EFOS: Sin alerta", status.TextContent, StringComparison.Ordinal);
        Assert.Contains("Factura global · informativo", status.TextContent, StringComparison.Ordinal);
        Assert.Contains("Pagada en exceso · requiere revisión", status.TextContent, StringComparison.Ordinal);
        Assert.Equal("Los vínculos sugeridos no cuentan para los montos cobrados confirmados.", page.Find("caption").TextContent);
        Assert.Equal("Movimientos vinculados", page.Find(".table-wrap").GetAttribute("aria-label"));
        Assert.Equal("region", page.Find(".table-wrap").GetAttribute("role"));
        Assert.Equal("0", page.Find(".table-wrap").GetAttribute("tabindex"));
        Assert.Equal(["Movimiento", "Estado / confianza", "Aplicado", "Complemento de pago"], page.FindAll("thead th").Select(h => h.TextContent).ToArray());
        Assert.Contains("Forma de pago 01 · 2026-10-10", page.Markup, StringComparison.Ordinal);
        Assert.Contains("CFDI de pago ", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Recibido", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Conciliado", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Alta", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Movimiento " + summary.Links[0].MovementId + " · 5,800.00", page.Markup, StringComparison.Ordinal);

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        page.WaitForAssertion(() => Assert.Equal("Invoice summary", page.Find("h1").TextContent));
        Assert.Contains("Confirmed paid", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Cancellation: No cancellation information", page.Markup, StringComparison.Ordinal);
        Assert.Equal("5,800.00", page.Find("[data-summary=pending]").TextContent);
    }

    [Fact]
    public async Task Invoice_detail_without_payment_method_says_so_in_spanish()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var summary = Summary() with { PaymentMethod = null };
        Register(context, _ => WebTestHandler.Json(summary));
        var page = context.Render<InvoiceDetail>(p => p.Add(x => x.InvoiceId, summary.Id));
        page.WaitForAssertion(() => Assert.Contains("Método de pago no disponible", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Emitida", page.Markup, StringComparison.Ordinal);
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
