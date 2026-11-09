using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.ViewModels;

namespace Reconciliation.Tests;

public sealed class WorkspaceComponentTests
{
    [Fact]
    public async Task Company_picker_shows_membership_role_and_archived_workspace_stays_read_only()
    {
        await using var context = new BunitContext();
        var archived = new CompanyItem(Guid.NewGuid(), "AAA010101AAA", "Synthetic archive", DateTime.UtcNow, "accountant");
        var active = new CompanyItem(Guid.NewGuid(), "AAA010101AAA", "Synthetic active", null, "accountant");
        TestLanguage.Register(context, "en");
        var session = Register(context, [archived, active]);
        var picker = context.Render<CompanyPicker>();
        picker.WaitForAssertion(() => Assert.Equal(2, picker.FindAll("button[data-company]").Count));
        Assert.Contains("Accountant", picker.Markup, StringComparison.Ordinal);
        Assert.Contains("Archived · read-only", picker.Markup, StringComparison.Ordinal);
        picker.Find($"button[data-company='{archived.Id}']").Click();
        Assert.Equal(archived, session.Company);
        Assert.False(session.CanWrite);
        picker.Find($"button[data-company='{active.Id}']").Click();
        Assert.Equal(active, session.Company);
        Assert.True(session.CanWrite);
    }

    [Fact]
    public async Task Company_picker_is_spanish_by_default_and_follows_a_language_switch()
    {
        await using var context = new BunitContext();
        var archived = new CompanyItem(Guid.NewGuid(), "AAA010101AAA", "Synthetic archive", DateTime.UtcNow, "accountant");
        TestLanguage.Register(context, "es");
        Register(context, [archived]);
        var picker = context.Render<CompanyPicker>();
        picker.WaitForAssertion(() => Assert.Single(picker.FindAll("button[data-company]")));
        Assert.Contains("Elija una empresa", picker.Markup, StringComparison.Ordinal);
        Assert.Contains("Contador", picker.Markup, StringComparison.Ordinal);
        Assert.Contains("Archivada · solo lectura", picker.Markup, StringComparison.Ordinal);

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");

        picker.WaitForAssertion(() => Assert.Contains("Choose a company", picker.Markup, StringComparison.Ordinal));
        Assert.Contains("Accountant", picker.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Company_picker_explains_an_empty_membership_list_in_both_languages()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        Register(context, []);
        var picker = context.Render<CompanyPicker>();
        picker.WaitForAssertion(() => Assert.Contains("No hay membresías de empresa disponibles", picker.Markup, StringComparison.Ordinal));
        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        picker.WaitForAssertion(() => Assert.Contains("No company memberships are available", picker.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overview_shows_additive_totals_alert_counts_and_demo_disclaimer()
    {
        await using var context = new BunitContext();
        var company = new CompanyItem(Guid.NewGuid(), "AAA010101AAA", "Synthetic company", null, "viewer");
        Register(context, [company]).SelectCompany(company);
        var page = context.Render<Overview>();
        page.WaitForAssertion(() => Assert.Contains("11,600.00", page.Markup, StringComparison.Ordinal));
        Assert.Contains("results have no fiscal or legal validity", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Complement overdue", page.Markup, StringComparison.Ordinal);
        Assert.Contains("EFOS warning", page.Markup, StringComparison.Ordinal);
        Assert.Equal("2", page.Find("[data-alert='complement']").TextContent);
        Assert.Equal("1", page.Find("[data-alert='efos']").TextContent);
        Assert.Equal(2, page.FindAll("table").Count);
    }

    private static SessionState Register(BunitContext context, CompanyItem[] companies)
    {
        var state = new SessionState();
        var totals = new ResourceTotals(2, 11600, [new("reconciled", 1, 5800), new("suggested", 1, 5800)]);
        var http = new HttpClient(new WebTestHandler(request => request.RequestUri!.AbsolutePath == "/companies"
            ? WebTestHandler.Json(companies)
            : WebTestHandler.Json(new OverviewResponse(totals, totals, new(2, 0, 1, 0), "Demo: results have no fiscal or legal validity", "Classified movements remain in totals."))))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        context.Services.AddSingleton(state);
        context.Services.AddSingleton(http);
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<WorkspaceViewModel>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddTransient<JobProgressViewModel>();
        return state;
    }
}
