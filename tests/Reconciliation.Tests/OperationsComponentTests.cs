using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.Shared;
using Reconciliation.Web.ViewModels;
using TestContext = Xunit.TestContext;

namespace Reconciliation.Tests;

public sealed class OperationsComponentTests
{
    [Fact]
    public async Task Progress_polls_with_backoff_and_stops_at_terminal_state_without_sleeps()
    {
        var calls = 0;
        var clock = new ManualTimeProvider();
        var session = State("accountant");
        var id = Guid.NewGuid();
        using var http = new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(Job(id, session.Company!.Id, ++calls >= 6 ? "succeeded" : "running", Math.Min(calls, 4))))) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        using var model = new JobProgressViewModel(api, clock);
        await using var context = new BunitContext();
        TestLanguage.Register(context);
        var component = context.Render<JobProgress>(p => p.Add(x => x.Model, model));
        var polling = model.WatchAsync(id, TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(2));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(4));
        clock.Advance(TimeSpan.FromSeconds(4));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        await polling;
        component.WaitForAssertion(() => Assert.Contains("succeeded", component.Markup, StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Job_progress_label_localizes_state_but_not_polling()
    {
        var calls = 0;
        var clock = new ManualTimeProvider();
        var session = State("accountant");
        var id = Guid.NewGuid();
        using var http = new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(Job(id, session.Company!.Id, ++calls >= 3 ? "succeeded" : "running", Math.Min(calls, 4))))) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        using var model = new JobProgressViewModel(api, clock);
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var component = context.Render<JobProgress>(p => p.Add(x => x.Model, model));
        var polling = model.WatchAsync(id, TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        component.WaitForAssertion(() => Assert.Equal("En ejecución", component.Find("strong").TextContent));
        Assert.Contains("Progreso 1 / 4", component.Markup, StringComparison.Ordinal);
        Assert.Contains("intento 1 / 5", component.Markup, StringComparison.Ordinal);

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");

        component.WaitForAssertion(() => Assert.Equal("Running", component.Find("strong").TextContent));
        Assert.Contains("Progress 1 / 4", component.Markup, StringComparison.Ordinal);
        Assert.Contains("attempt 1 / 5", component.Markup, StringComparison.Ordinal);
        Assert.Equal("running", component.Find("strong").GetAttribute("data-state"));
        Assert.Equal(id, model.Current!.Id);
        Assert.Equal(1, calls);
        Assert.False(polling.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(2));
        await polling;
        Assert.Equal(3, calls);
        Assert.Equal(id, model.Current!.Id);
        component.WaitForAssertion(() => Assert.Equal("Completed", component.Find("strong").TextContent));
        Assert.Equal("succeeded", component.Find("strong").GetAttribute("data-state"));
    }

    [Fact]
    public async Task Failed_job_stops_immediately_and_does_not_render_raw_errors()
    {
        var session = State("accountant");
        using var http = new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(Job(Guid.NewGuid(), session.Company!.Id, "failed", 0) with { LastError = "Synthetic raw exception stack" }))) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        using var model = new JobProgressViewModel(api, new ManualTimeProvider());
        await model.WatchAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        await using var context = new BunitContext();
        TestLanguage.Register(context);
        var component = context.Render<JobProgress>(p => p.Add(x => x.Model, model));
        Assert.Contains("failed", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic raw exception", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imports_send_multipart_then_poll_and_read_per_file_report_and_run_reconciliation()
    {
        var session = State("accountant");
        var job = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var calls = new List<string>();
        using var http = new HttpClient(new WebTestHandler(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("imports/bank-statement", StringComparison.Ordinal))
            {
                Assert.IsType<MultipartFormDataContent>(request.Content);
                return WebTestHandler.Json(new ImportReceipt(job, upload));
            }
            if (request.RequestUri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal)) return WebTestHandler.Json(Job(job, session.Company!.Id, "succeeded", 2));
            if (request.RequestUri.AbsolutePath.EndsWith("reconciliation-runs", StringComparison.Ordinal)) return WebTestHandler.Json(new RunReceipt(job, "queued"));
            return WebTestHandler.Json(new ImportReportResponse(upload, [new(Guid.NewGuid(), "synthetic.csv", "imported", null, null, 2, 0)]));
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        using var jobs = new JobProgressViewModel(api, new ManualTimeProvider());
        var imports = new ImportViewModel(api, jobs, session) { Kind = "bank-statement", Bank = "reference", AccountReference = "Synthetic-account" };
        await imports.UploadAsync([new("synthetic.csv", [1, 2, 3])], TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(imports.Report!.Files).ImportedMovements);
        await jobs.StartRunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", jobs.Current!.State);
        Assert.Contains(calls, call => call.EndsWith($"imports/{upload}", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.EndsWith("reconciliation-runs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("viewer", 0)]
    [InlineData("accountant", 1)]
    public async Task Movement_classification_actions_follow_role_and_display_derived_state(string role, int actions)
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context);
        var session = State(role);
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(new MovementPage([new(Guid.NewGuid(), new(2026, 10, 10), "Synthetic transfer", 100, 0, null, "pending", null, null, "No active link", 0, 0, 100, 100)], null, null)))) { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<MovementsViewModel>();
        var page = context.Render<Movements>();
        page.WaitForAssertion(() => Assert.Contains("Synthetic transfer", page.Markup, StringComparison.Ordinal));
        Assert.Contains("pending", page.Markup, StringComparison.Ordinal);
        Assert.Equal(actions, page.FindAll("button[data-classify]").Count);
    }

    [Fact]
    public async Task Classification_uses_PATCH_contract_then_refreshes_movements()
    {
        var session = State("accountant");
        var writes = 0;
        using var http = new HttpClient(new WebTestHandler(request =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                writes++;
                var body = request.Content!.ReadFromJsonAsync<ClassificationRequest>(TestContext.Current.CancellationToken).GetAwaiter().GetResult()!;
                Assert.Equal("loan", body.Classification);
                return new(HttpStatusCode.NoContent);
            }
            return WebTestHandler.Json(new MovementPage([], null, null));
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        var model = new MovementsViewModel(api, session);
        await model.ClassifyAsync(Guid.NewGuid(), "loan", "Synthetic classification");
        Assert.Equal(1, writes);
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task Filter_options_keep_machine_values_in_spanish_for_movements_and_imports()
    {
        await using var movements = new BunitContext();
        TestLanguage.Register(movements, "es");
        var page = RenderMovements(movements, "viewer", [], _ => { });
        page.WaitForAssertion(() => Assert.Contains("No hay movimientos que coincidan con este filtro.", page.Markup, StringComparison.Ordinal));
        var options = page.Find("#movement-state").QuerySelectorAll("option");
        Assert.Equal(["", "pending", "suggested", "reconciled"], options.Select(o => o.GetAttribute("value")).ToArray());
        Assert.Equal(["Todos los estados (incluye clasificados)", "Pendiente (sin clasificar)", "Sugerido", "Conciliado"], options.Select(o => o.TextContent).ToArray());
        Assert.Equal("ESTADOS DE CUENTA BANCARIOS", page.Find(".eyebrow").TextContent);
        Assert.Equal("Movimientos", page.Find("h1").TextContent);
        Assert.Equal("Aplicar filtro", page.Find("form button[type=submit]").TextContent);
        Assert.Equal("Movimientos bancarios", page.Find(".table-wrap").GetAttribute("aria-label"));
        Assert.Equal("region", page.Find(".table-wrap").GetAttribute("role"));
        Assert.Equal("0", page.Find(".table-wrap").GetAttribute("tabindex"));
        Assert.Equal(["Movimiento", "Abono", "Cargo", "Estado", "Clasificación"], page.FindAll("thead th").Select(h => h.TextContent).ToArray());

        await using var imports = new BunitContext();
        TestLanguage.Register(imports, "es");
        var import = RenderImports(imports, "accountant", out _);
        import.WaitForAssertion(() => Assert.NotEmpty(import.FindAll("#kind")));
        var kinds = import.Find("#kind").QuerySelectorAll("option");
        Assert.Equal(["cfdi", "bank-statement"], kinds.Select(o => o.GetAttribute("value")).ToArray());
        Assert.Equal(["Archivos XML de CFDI", "Estado de cuenta bancario en CSV"], kinds.Select(o => o.TextContent).ToArray());
        Assert.Equal("files", import.Find("input[type=file]").Id);
        Assert.Equal("submit", import.Find("form button").GetAttribute("type"));
    }

    [Fact]
    public async Task Classification_options_post_machine_values()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var bodies = new List<ClassificationRequest>();
        var item = new MovementItem(Guid.NewGuid(), new(2026, 10, 10), "Synthetic transfer", 100, 0, null, "pending", null, null, "No active link", 0, 0, 100, 100);
        var page = RenderMovements(context, "accountant", [item], request =>
        {
            if (request.Method == HttpMethod.Patch) bodies.Add(request.Content!.ReadFromJsonAsync<ClassificationRequest>(TestContext.Current.CancellationToken).GetAwaiter().GetResult()!);
        });
        page.WaitForAssertion(() => Assert.Equal("Clasificar", page.Find("button[data-classify]").TextContent));
        await page.InvokeAsync(() => page.Find("button[data-classify]").Click());
        var options = page.Find("#classification").QuerySelectorAll("option");
        Assert.Equal(["", "loan", "partner_contribution", "tax_payment", "late_invoicing", "other"], options.Select(o => o.GetAttribute("value")).ToArray());
        Assert.Equal(["Quitar clasificación", "Préstamo", "Aportación de socios", "Pago de impuestos", "Facturación tardía", "Otro"], options.Select(o => o.TextContent).ToArray());
        Assert.Equal("Clasificar movimiento", page.Find("tr.row-edit h2").TextContent);
        Assert.Equal("Nota (opcional)", page.Find("label[for=classification-note]").TextContent);
        Assert.Equal("6", page.Find("tr.row-edit td").GetAttribute("colspan"));
        Assert.Equal(["Guardar clasificación", "Cancelar clasificación"], page.FindAll("tr.row-edit button").Select(b => b.TextContent).ToArray());
        var loan = options.Single(o => o.TextContent == "Préstamo").GetAttribute("value")!;
        await page.InvokeAsync(() => page.Find("#classification").Change(loan));
        await page.InvokeAsync(() => page.Find("tr.row-edit form").Submit());
        page.WaitForAssertion(() => Assert.Single(bodies));
        Assert.Equal("loan", bodies[0].Classification);
    }

    [Fact]
    public async Task Unknown_state_in_a_table_row_renders_raw_for_movements()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var item = new MovementItem(Guid.NewGuid(), new(2026, 10, 10), "Synthetic transfer", 100, 0, null, "brand_new_state", "unheard_of", null, "No active link", 0, 0, 100, 100);
        var page = RenderMovements(context, "viewer", [item], _ => { });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        var badge = page.Find("tbody .badge");
        Assert.Equal("brand_new_state", badge.TextContent);
        Assert.Equal("badge brand_new_state", badge.ClassName);
        Assert.Contains("unheard_of", page.Find("tbody tr").QuerySelectorAll("td")[3].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Movement_rows_localize_labels_and_keep_bank_and_server_text()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var pending = new MovementItem(Guid.NewGuid(), new(2026, 10, 10), "Synthetic transfer", 11600, 0, null, "pending", null, null, "No active link", 0, 0, 100, 100);
        var suggested = pending with { Id = Guid.NewGuid(), Description = "Synthetic suggestion", State = "suggested", Reason = "amount equals remaining payable" };
        var classified = pending with { Id = Guid.NewGuid(), Description = "Synthetic loan", Credit = 0, Debit = 5, State = "reconciled", Classification = "loan", Note = "Nota del usuario", Reason = "Nota del usuario" };
        var coded = pending with { Id = Guid.NewGuid(), Description = "Synthetic coded", Classification = "tax_payment", Reason = "tax_payment" };
        var page = RenderMovements(context, "accountant", [pending, suggested, classified, coded], _ => { });
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll("tbody tr").Count));
        var rows = page.FindAll("tbody tr");
        Assert.Equal("Sin clasificar", rows[0].QuerySelectorAll("td")[3].TextContent);
        Assert.Equal("Pendiente", rows[0].QuerySelector(".badge")!.TextContent);
        Assert.Equal("11,600.00", rows[0].QuerySelector("td.money")!.TextContent);
        Assert.Equal("2026-10-10", rows[0].QuerySelector("th")!.TextContent[..10]);
        Assert.Equal("Sin vínculo activo", rows[0].QuerySelector("small")!.TextContent);
        Assert.Null(rows[0].QuerySelector("small")!.GetAttribute("lang"));
        Assert.Equal("amount equals remaining payable", rows[1].QuerySelector("small")!.TextContent);
        Assert.Equal("en", rows[1].QuerySelector("small")!.GetAttribute("lang"));
        Assert.Equal("Synthetic suggestion", rows[1].QuerySelector("th p")!.TextContent);
        Assert.Null(rows[1].QuerySelector("th p")!.GetAttribute("lang"));
        Assert.StartsWith("Préstamo", rows[2].QuerySelectorAll("td")[3].TextContent, StringComparison.Ordinal);
        Assert.Contains("Nota del usuario", rows[2].QuerySelectorAll("td")[3].TextContent, StringComparison.Ordinal);
        Assert.Null(rows[2].QuerySelector("small")!.GetAttribute("lang"));
        Assert.Equal("Pago de impuestos", rows[3].QuerySelector("small")!.TextContent);
        Assert.Equal("Acción", page.FindAll("thead th")[5].TextContent);
        Assert.Equal("Clasificar", rows[0].QuerySelector("button[data-classify]")!.TextContent);

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        page.WaitForAssertion(() => Assert.Equal("Unclassified", page.FindAll("tbody tr")[0].QuerySelectorAll("td")[3].TextContent));
        Assert.Equal("No active link", page.FindAll("tbody tr")[0].QuerySelector("small")!.TextContent);
        Assert.Null(page.FindAll("tbody tr")[1].QuerySelector("small")!.GetAttribute("lang"));
    }

    [Fact]
    public async Task Imports_page_is_spanish_and_file_count_follows_language()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var page = RenderImports(context, "accountant", out _);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#files")));
        Assert.Equal("INGESTA DE DATOS", page.Find(".eyebrow").TextContent);
        Assert.Equal("Importar archivos", page.Find("h1").TextContent);
        Assert.Contains("Cargue archivos XML de CFDI 4.0 o un estado de cuenta bancario en el formato CSV de referencia compatible.", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Hasta 20 archivos, 10 MiB cada uno y 20 MiB en total. La API puede aplicar límites configurados más bajos.", page.Markup, StringComparison.Ordinal);
        Assert.Equal("Tipo de importación", page.Find("label[for=kind]").TextContent);
        Assert.Equal("Archivos", page.Find("label[for=files]").TextContent);
        Assert.Equal("Cargar archivos", page.Find("form button[type=submit]").TextContent);
        Assert.Contains("0 archivo(s) seleccionado(s)", page.Markup, StringComparison.Ordinal);

        await page.InvokeAsync(() => page.Find("#kind").Change("bank-statement"));
        Assert.Equal("Formato de banco compatible", page.Find("label[for=bank]").TextContent);
        Assert.Equal("Referencia de cuenta", page.Find("label[for=account]").TextContent);
        await page.InvokeAsync(() => page.Find("#kind").Change("cfdi"));

        page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("<x/>", "a.xml"));
        page.WaitForAssertion(() => Assert.Contains("1 archivo(s) seleccionado(s)", page.Markup, StringComparison.Ordinal));

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        page.WaitForAssertion(() => Assert.Contains("1 file(s) selected", page.Markup, StringComparison.Ordinal));
        Assert.Equal("Import files", page.Find("h1").TextContent);
        Assert.Equal("Upload files", page.Find("form button[type=submit]").TextContent);
    }

    [Fact]
    public async Task Imports_page_read_only_message_is_spanish_for_viewers()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var page = RenderImports(context, "viewer", out _);
        Assert.Equal("Este espacio de trabajo es de solo lectura. Los contadores y administradores pueden importar a una empresa activa.", page.Find("section > p:not(.eyebrow)").TextContent);
        Assert.Empty(page.FindAll("form"));

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        page.WaitForAssertion(() => Assert.Equal("This workspace is read-only. Accountants and administrators can import into an active company.", page.Find("section > p:not(.eyebrow)").TextContent));
    }

    [Fact]
    public async Task Imports_report_is_localized_and_keeps_server_text()
    {
        await using var context = new BunitContext();
        TestLanguage.Register(context, "es");
        var session = State("accountant");
        var job = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var http = new HttpClient(new WebTestHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("banks", StringComparison.Ordinal)) return WebTestHandler.Json(Array.Empty<string>());
            if (path.EndsWith("imports/cfdi", StringComparison.Ordinal)) return WebTestHandler.Json(new ImportReceipt(job, upload));
            if (path.Contains("/jobs/", StringComparison.Ordinal)) return WebTestHandler.Json(Job(job, session.Company!.Id, "succeeded", 2));
            return WebTestHandler.Json(new ImportReportResponse(upload, [new(Guid.NewGuid(), "ok.xml", "imported", null, null, 3, 1), new(Guid.NewGuid(), "bad.xml", "rejected", "File is not a CFDI 4.0 XML", null, 0, 0), new(Guid.NewGuid(), "odd.xml", "novel_status", null, null, 0, 0)]));
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        var api = new ApiClient(http, session);
        var jobs = new JobProgressViewModel(api, new ManualTimeProvider());
        var imports = new ImportViewModel(api, jobs, session);
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(imports);
        await imports.UploadAsync([new("ok.xml", [1, 2, 3])], TestContext.Current.CancellationToken);
        Assert.NotNull(imports.Report);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var page = context.Render<Imports>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        Assert.Equal("Reporte por archivo", page.Find("section.table-wrap h2").TextContent);
        Assert.Equal($"Carga {upload}", page.Find("caption").TextContent);
        Assert.Equal(["Archivo", "Estado", "Detalles", "Movimientos"], page.FindAll("thead th").Select(h => h.TextContent).ToArray());
        var rows = page.FindAll("tbody tr");
        Assert.Equal("3 importados · 1 duplicados", rows[0].QuerySelectorAll("td")[2].TextContent);
        Assert.Equal("Importado", rows[0].QuerySelectorAll("td")[0].TextContent);
        Assert.Equal("Rechazado", rows[1].QuerySelectorAll("td")[0].TextContent);
        Assert.Equal("File is not a CFDI 4.0 XML", rows[1].QuerySelectorAll("td")[1].TextContent);
        Assert.Equal("en", rows[1].QuerySelectorAll("td")[1].QuerySelector("[lang]")!.GetAttribute("lang"));
        Assert.Equal("novel_status", rows[2].QuerySelectorAll("td")[0].TextContent);
        Assert.Equal("Abrir el resumen para ejecutar la conciliación", page.Find("a[href='/ui/overview']").TextContent);

        context.Services.GetRequiredService<Reconciliation.Web.Localization.Localizer>().Set("en");
        page.WaitForAssertion(() => Assert.Equal("Per-file report", page.Find("section.table-wrap h2").TextContent));
        Assert.Equal("3 imported · 1 duplicates", page.FindAll("tbody tr")[0].QuerySelectorAll("td")[2].TextContent);
        Assert.Equal("Imported", page.FindAll("tbody tr")[0].QuerySelectorAll("td")[0].TextContent);
        Assert.Null(page.FindAll("tbody tr")[1].QuerySelector("[lang]"));
    }

    private static readonly string[] Banks = ["reference"];

    private static IRenderedComponent<Movements> RenderMovements(BunitContext context, string role, MovementItem[] items, Action<HttpRequestMessage> onRequest)
    {
        context.Services.AddSingleton(State(role));
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            onRequest(request);
            return request.Method == HttpMethod.Patch ? new(HttpStatusCode.NoContent) : WebTestHandler.Json(new MovementPage(items, null, null));
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<MovementsViewModel>();
        return context.Render<Movements>();
    }

    private static IRenderedComponent<Imports> RenderImports(BunitContext context, string role, out SessionState session)
    {
        session = State(role);
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(Banks))) { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddSingleton<TimeProvider>(new ManualTimeProvider());
        context.Services.AddTransient<JobProgressViewModel>();
        context.Services.AddTransient<ImportViewModel>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context.Render<Imports>();
    }

    internal static SessionState State(string role)
    {
        var session = new SessionState();
        session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic", null, role));
        return session;
    }
    internal static JobResponse Job(Guid id, Guid company, string state, int done) => new(id, company, "import_cfdi", state, "{}", null, 1, 5, DateTime.UtcNow, null, null, done, 4, null, Guid.NewGuid(), DateTime.UtcNow, state == "succeeded" ? DateTime.UtcNow : null, []);
}
