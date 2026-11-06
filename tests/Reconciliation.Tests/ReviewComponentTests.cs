using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.ViewModels;
using TestContext = Xunit.TestContext;

namespace Reconciliation.Tests;

public sealed class ReviewComponentTests
{
    [Theory]
    [InlineData("viewer", false, false)]
    [InlineData("accountant", false, true)]
    [InlineData("administrator", false, true)]
    [InlineData("accountant", true, false)]
    public async Task Review_actions_follow_the_live_company_role_and_archive_state(string role, bool archived, bool actions)
    {
        await using var context = new BunitContext();
        Register(context, role, archived);
        var page = context.Render<Review>();
        page.WaitForAssertion(() => Assert.Contains("Synthetic suggestion", page.Markup, StringComparison.Ordinal));
        Assert.Equal(actions ? 3 : 0, page.FindAll("button[data-decision]").Count);
        Assert.Contains("high", page.Markup, StringComparison.Ordinal);
        Assert.Contains("amount close", page.Markup, StringComparison.Ordinal);
        Assert.Contains("complement overdue", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Already_decided_conflict_is_shown_gracefully_and_queue_is_refreshed()
    {
        await using var context = new BunitContext();
        var reads = 0;
        Register(context, "accountant", false, request =>
        {
            if (request.Method == HttpMethod.Post) return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "Conflict", state = "reconciled" }) };
            return WebTestHandler.Json(++reads == 1 ? Queue() : new ReviewPage([], null, null, null));
        });
        var page = context.Render<Review>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button[data-decision]")));
        await page.InvokeAsync(() => page.Find("button[data-decision=confirm]").Click());
        page.WaitForAssertion(() => Assert.Contains("already decided", page.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Equal(2, reads);
        Assert.Empty(page.FindAll("button[data-decision]"));
    }

    [Fact]
    public async Task Confirm_reject_and_change_send_the_decision_contract_and_reload_the_queue()
    {
        foreach (var type in new[] { "confirm", "reject", "change" })
        {
            var postCount = 0;
            using var http = new HttpClient(new WebTestHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    postCount++;
                    var sent = request.Content!.ReadFromJsonAsync<DecisionRequest>(TestContext.Current.CancellationToken).GetAwaiter().GetResult()!;
                    Assert.Equal(type, sent.Type);
                    Assert.Equal(type == "change" ? 5800m : null, sent.AmountApplied);
                    return WebTestHandler.Json(new DecisionResponse(new(Guid.NewGuid(), type, Guid.NewGuid(), DateTime.UtcNow), []));
                }
                return WebTestHandler.Json(new ReviewPage([], null, null, null));
            }))
            { BaseAddress = new Uri("https://app.example.invalid/") };
            var session = new SessionState();
            session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic", null, "accountant"));
            using var api = new ApiClient(http, session);
            var model = new ReviewQueueViewModel(api, session);
            await model.DecideAsync(Guid.NewGuid(), new(type, type == "change" ? Guid.NewGuid() : null, type == "change" ? 5800 : null));
            Assert.Equal(1, postCount);
            Assert.Null(model.Error);
        }
    }

    private static void Register(BunitContext context, string role, bool archived, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var session = new SessionState();
        session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic", archived ? DateTime.UtcNow : null, role));
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(respond ?? (_ => WebTestHandler.Json(Queue())))) { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<ReviewQueueViewModel>();
    }

    private static ReviewPage Queue() => new([new("link", Guid.NewGuid(), Guid.NewGuid(), new(new(2026, 10, 2), 11600, "AAA010101AAA", "XEXX010101000"), [new("amount close", 10, "Synthetic criterion")], "high", "Synthetic suggestion", [new("complement overdue", true)], Guid.NewGuid(), Guid.NewGuid(), new(new(2026, 10, 10), 5800, "Synthetic payment"))], null, null, null);
}

