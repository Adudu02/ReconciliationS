using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using TestContext = Xunit.TestContext;

namespace Reconciliation.Tests;

public sealed class ClientSessionTests
{
    [Fact]
    public async Task One_401_refreshes_once_retries_with_new_bearer_and_logout_clears_memory()
    {
        var refreshes = 0;
        var reads = 0;
        var session = new SessionState();
        using var http = new HttpClient(new WebTestHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/tokens") return WebTestHandler.Json(new SessionTokens("synthetic-access", 900));
            if (path == "/auth/refresh") { refreshes++; return WebTestHandler.Json(new SessionTokens("synthetic-renewed", 900)); }
            if (path == "/auth/logout") return new(HttpStatusCode.NoContent);
            if (++reads == 1) return new(HttpStatusCode.Unauthorized);
            Assert.Equal("synthetic-renewed", request.Headers.Authorization?.Parameter);
            return WebTestHandler.Json(Array.Empty<CompanyItem>());
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        await api.LoginAsync("synthetic@example.invalid", "synthetic-password", TestContext.Current.CancellationToken);
        Assert.True(session.IsAuthenticated);
        Assert.Empty(await api.GetAsync<CompanyItem[]>("companies", TestContext.Current.CancellationToken));
        Assert.Equal(1, refreshes);
        await api.LogoutAsync(TestContext.Current.CancellationToken);
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Company);
    }

    [Fact]
    public async Task Fresh_login_discards_the_previous_workspace_selection()
    {
        var session = new SessionState();
        session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic old workspace", null, "administrator"));
        using var http = new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(new SessionTokens("synthetic-new-user", 900)))) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        await api.LoginAsync("synthetic@example.invalid", "synthetic-password", TestContext.Current.CancellationToken);
        Assert.Null(session.Company);
    }

    [Fact]
    public async Task Failed_refresh_returns_to_login_without_retry_loop()
    {
        var calls = 0;
        var session = new SessionState();
        using var http = new HttpClient(new WebTestHandler(_ => { calls++; return new(HttpStatusCode.Unauthorized); })) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        var error = await Assert.ThrowsAsync<ApiProblemException>(() => api.GetAsync<CompanyItem[]>("companies", TestContext.Current.CancellationToken));
        Assert.Equal(401, error.Status);
        Assert.Equal(2, calls);
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public async Task Page_reload_silently_restores_only_access_token_via_cookie_transport()
    {
        var session = new SessionState();
        using var http = new HttpClient(new WebTestHandler(request =>
        {
            Assert.Equal("/auth/refresh", request.RequestUri!.AbsolutePath);
            Assert.Equal("cookie", Assert.Single(request.Headers.GetValues("X-Refresh-Transport")));
            Assert.Null(request.Content);
            return WebTestHandler.Json(new SessionTokens("synthetic-restored", 900));
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, session);
        Assert.True(await api.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task Login_component_is_labelled_shows_disclaimer_and_submits_without_rendering_credentials()
    {
        await using var context = new BunitContext();
        var session = new SessionState();
        using var http = new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(new SessionTokens("synthetic-access", 900)))) { BaseAddress = new Uri("https://app.example.invalid/") };
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(new ApiClient(http, session));
        var page = context.Render<Login>();
        Assert.Contains("results have no fiscal or legal validity", page.Markup, StringComparison.Ordinal);
        page.Find("input[type=email]").Change("synthetic@example.invalid");
        page.Find("input[type=password]").Change("synthetic-password");
        await page.InvokeAsync(() => page.Find("form").Submit());
        page.WaitForAssertion(() => Assert.True(session.IsAuthenticated));
        Assert.DoesNotContain("synthetic-access", page.Markup, StringComparison.Ordinal);
    }
}

internal sealed class WebTestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    internal static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
