using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Core.Jobs;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.ViewModels;
using TestContext = Xunit.TestContext;

namespace Reconciliation.Tests;

public sealed class WebWorkedExampleTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Worked_example_runs_through_the_same_API_client_and_models_as_the_pages()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "accountant");
        var viewer = await fixture.SeedUserAsync();
        await using (var owner = await fixture.Database.OpenOwnerAsync())
            await owner.ExecuteAsync(new CommandDefinition("INSERT INTO membership(company_id,user_id,role) VALUES(@company,@Id,'viewer')", new { company, viewer.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var host = fixture.App.WithWebHostBuilder(builder => builder.UseSetting("Session:BrowserRefreshCookie", "true").UseSetting("Session:AllowedOrigins:0", "https://localhost"));
        using var documentClient = host.CreateClient();
        using var description = JsonDocument.Parse(await documentClient.GetStringAsync("/openapi.json", TestContext.Current.CancellationToken));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var http = host.CreateDefaultClient(new CookieContainerHandler(), new ContractHandler(description.RootElement.GetProperty("paths"), seen));
        http.BaseAddress = new Uri("https://localhost/");
        http.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var session = new SessionState();
        using var api = new ApiClient(http, session);
        await api.LoginAsync(user.Email, ApiFixture.Password, TestContext.Current.CancellationToken);
        var workspace = new WorkspaceViewModel(api);
        await workspace.LoadCompaniesAsync();
        session.SelectCompany(Assert.Single(workspace.Companies));
        Assert.Equal(company, session.Company!.Id);
        Assert.True(session.CanWrite);
        using var jobs = new JobProgressViewModel(api, fixture.Clock);
        var imports = new ImportViewModel(api, jobs, session);
        await imports.LoadBanksAsync();
        Assert.Contains("reference", imports.Banks);
        var uuid = Guid.NewGuid();
        var invoice = SyntheticCfdi.Invoice(root =>
        {
            root.SetAttributeValue("SubTotal", "10000.00"); root.SetAttributeValue("Descuento", "0.00"); root.SetAttributeValue("Total", "11600.00");
            root.SetAttributeValue("Fecha", "2026-10-02T12:00:00"); root.SetAttributeValue("Serie", "FC"); root.SetAttributeValue("Folio", "0001");
            root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "XEXX010101000");
            var taxes = root.Element(SyntheticCfdi.Cfdi + "Impuestos")!;
            taxes.SetAttributeValue("TotalImpuestosTrasladados", "1600.00"); taxes.SetAttributeValue("TotalImpuestosRetenidos", "0.00");
            root.Descendants(SyntheticCfdi.Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", uuid);
        });
        await CompleteAsync(imports.UploadAsync([new("synthetic-invoice.xml", invoice)], TestContext.Current.CancellationToken), host.Services);
        Assert.Null(imports.Error);
        var invoiceId = Assert.Single(imports.Report!.Files).CfdiId!.Value;
        imports.Kind = "bank-statement";
        imports.Bank = "reference";
        imports.AccountReference = "Synthetic-Web";
        await CompleteAsync(imports.UploadAsync([new("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SPEI REC 0034 PAGO FACT 0001,5800.00,0,5800.00\n2026-10-20,SPEI REC 0071 XEXX COMERCIAL,5800.00,0,11600.00\n"))], TestContext.Current.CancellationToken), host.Services);
        Assert.Null(imports.Error);
        Assert.Equal(2, Assert.Single(imports.Report!.Files).ImportedMovements);
        await CompleteAsync(jobs.StartRunAsync(TestContext.Current.CancellationToken), host.Services);
        Assert.Equal("succeeded", jobs.Current!.State);
        var review = new ReviewQueueViewModel(api, session);
        await review.LoadAsync();
        Assert.Null(review.Error);
        Assert.Equal(2, review.Entries.Count(entry => entry.Kind == "link"));
        await review.DecideAsync(review.Entries.First(entry => entry.Kind == "link").LinkId!.Value, new("confirm"));
        Assert.Null(review.Error);
        var invoices = new InvoiceViewModel(api);
        await invoices.LoadAsync();
        Assert.Equal(invoiceId, Assert.Single(invoices.Invoices).Id);
        await invoices.LoadSummaryAsync(invoiceId);
        Assert.Null(invoices.Error);
        Assert.Equal(50, invoices.Summary!.PaidPercentage);
        Assert.Equal(5800, invoices.Summary.Pending);
        Assert.Equal(2, invoices.Summary.Links.Length);
        await workspace.LoadOverviewAsync();
        AssertTotals(workspace.Overview!);
        var movements = new MovementsViewModel(api, session);
        await movements.LoadAsync();
        await movements.ClassifyAsync(movements.Movements[0].Id, "other", "Synthetic UI contract check");
        Assert.Null(movements.Error);
        var reloaded = new SessionState();
        using var restored = new ApiClient(http, reloaded);
        Assert.True(await restored.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.True(reloaded.IsAuthenticated);
        await api.LogoutAsync(TestContext.Current.CancellationToken);
        Assert.False(session.IsAuthenticated);

        using var viewerHttp = host.CreateDefaultClient(new CookieContainerHandler(), new ContractHandler(description.RootElement.GetProperty("paths"), seen));
        viewerHttp.BaseAddress = new Uri("https://localhost/");
        viewerHttp.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var viewerState = new SessionState();
        using var viewerApi = new ApiClient(viewerHttp, viewerState);
        await viewerApi.LoginAsync(viewer.Email, ApiFixture.Password, TestContext.Current.CancellationToken);
        var viewerWorkspace = new WorkspaceViewModel(viewerApi);
        await viewerWorkspace.LoadCompaniesAsync();
        viewerState.SelectCompany(Assert.Single(viewerWorkspace.Companies));
        Assert.False(viewerState.CanWrite);
        await viewerWorkspace.LoadOverviewAsync();
        AssertTotals(viewerWorkspace.Overview!);
        var viewerInvoice = new InvoiceViewModel(viewerApi);
        await viewerInvoice.LoadSummaryAsync(invoiceId);
        Assert.Equal(50, viewerInvoice.Summary!.PaidPercentage);
        await using var context = new BunitContext();
        context.Services.AddSingleton(viewerState);
        context.Services.AddSingleton(viewerApi);
        context.Services.AddTransient<ReviewQueueViewModel>();
        var page = context.Render<Review>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("article")));
        Assert.Empty(page.FindAll("button[data-decision]"));
        Assert.Equal(17, seen.Count);
    }

    private async Task CompleteAsync(Task operation, IServiceProvider services)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var dispatcher = services.GetRequiredService<JobDispatcher>();
        while (!operation.IsCompleted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await dispatcher.RunOneAsync("synthetic-ui-worker", timeout.Token);
            if (!fixture.Clock.AdvanceNextTimer()) await Task.Yield();
        }
        await operation;
    }
    private static void AssertTotals(OverviewResponse value)
    {
        Assert.Equal(1, value.Invoices.Count);
        Assert.Equal(2, value.Movements.Count);
        Assert.Equal(11600, value.Invoices.Amount);
        Assert.Equal(11600, value.Movements.Amount);
        Assert.Equal(value.Invoices.Count, value.Invoices.States.Sum(state => state.Count));
        Assert.Equal(value.Invoices.Amount, value.Invoices.States.Sum(state => state.Amount));
        Assert.Equal(value.Movements.Count, value.Movements.States.Sum(state => state.Count));
        Assert.Equal(value.Movements.Amount, value.Movements.States.Sum(state => state.Amount));
    }
    private sealed class ContractHandler(JsonElement paths, HashSet<string> seen) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string Normalize(string path) => string.Join('/', path.Split('/').Select(part => Guid.TryParse(part, out _) || part.StartsWith('{') ? "{id}" : part));
            var match = Assert.Single(paths.EnumerateObject(), path => Normalize(path.Name) == Normalize(request.RequestUri!.AbsolutePath));
            Assert.True(match.Value.TryGetProperty(request.Method.Method.ToLowerInvariant(), out _), $"Undocumented UI call {request.Method} {request.RequestUri!.AbsolutePath}");
            seen.Add(request.Method + " " + match.Name);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
