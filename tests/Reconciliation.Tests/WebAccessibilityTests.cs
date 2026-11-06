using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.ViewModels;

namespace Reconciliation.Tests;

public sealed class WebAccessibilityTests
{
    [Fact]
    public async Task Wide_invoice_tables_can_be_focused_and_scrolled_with_a_keyboard()
    {
        await using var context = new BunitContext();
        var session = OperationsComponentTests.State("viewer");
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(new InvoicePage([], null, null)))) { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<InvoiceViewModel>();
        var page = context.Render<Invoices>();
        Assert.Equal("0", page.Find(".table-wrap").GetAttribute("tabindex"));
        Assert.Equal("region", page.Find(".table-wrap").GetAttribute("role"));
        Assert.NotEmpty(page.Find(".table-wrap").GetAttribute("aria-label")!);
    }
}
