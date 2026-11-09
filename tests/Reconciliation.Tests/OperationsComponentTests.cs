using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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

    internal static SessionState State(string role)
    {
        var session = new SessionState();
        session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic", null, role));
        return session;
    }
    internal static JobResponse Job(Guid id, Guid company, string state, int done) => new(id, company, "import_cfdi", state, "{}", null, 1, 5, DateTime.UtcNow, null, null, done, 4, null, Guid.NewGuid(), DateTime.UtcNow, state == "succeeded" ? DateTime.UtcNow : null, []);
}
