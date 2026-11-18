using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Pages;
using Reconciliation.Web.ViewModels;
using TestContext = Xunit.TestContext;

namespace Reconciliation.Tests;

public sealed class TeamAccountComponentTests
{
    [Theory]
    [InlineData("es", "Equipo", "Contador")]
    [InlineData("en", "Team", "Accountant")]
    public async Task Team_adds_by_email_with_machine_role_and_clears_the_initial_password(string language, string heading, string roleLabel)
    {
        await using var context = new BunitContext();
        var session = await RegisterAsync(context, language, "administrator");
        var added = false;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            Assert.Equal(session.Company!.Id.ToString(), request.Headers.GetValues("X-Company-Id").Single());
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content!.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Assert.Equal("new@example.invalid", body.GetProperty("email").GetString());
                Assert.Equal("accountant", body.GetProperty("role").GetString());
                Assert.Equal("synthetic-initial-password", body.GetProperty("initialPassword").GetString());
                added = true;
                return new(HttpStatusCode.NoContent);
            }
            return WebTestHandler.Json(added ? new MemberItem[] { new(Guid.NewGuid(), "new@example.invalid", "accountant") } : []);
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        Assert.Equal(heading, page.Find("h1").TextContent);
        page.Find("#member-email").Change("new@example.invalid");
        page.Find("#member-role").Change("accountant");
        page.Find("#initial-password").Change("synthetic-initial-password");
        page.Find("[data-show-password]").Click();
        Assert.Equal("text", page.Find("#initial-password").GetAttribute("type"));
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("new@example.invalid", page.Find("tbody").TextContent, StringComparison.Ordinal));
        Assert.Contains(roleLabel, page.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Equal("", page.Find("#initial-password").GetAttribute("value"));
        Assert.Equal("password", page.Find("#initial-password").GetAttribute("type"));
        Assert.DoesNotContain("synthetic-initial-password", page.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es", "viewer", false)]
    [InlineData("en", "accountant", false)]
    [InlineData("es", "administrator", true)]
    [InlineData("en", "administrator", true)]
    public async Task Team_is_read_only_for_nonadministrators_and_archived_companies(string language, string role, bool archived)
    {
        await using var context = new BunitContext();
        var session = await RegisterAsync(context, language, role, archived);
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(new MemberItem[] { new(Guid.NewGuid(), "member@example.invalid", role) }))) { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        page.WaitForAssertion(() => Assert.Contains("member@example.invalid", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("form"));
        Assert.False(session.Company is { ArchivedAt: null, Role: "administrator" });
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Team_shows_API_errors_and_never_retains_passwords_after_failure(string language)
    {
        await using var context = new BunitContext();
        await RegisterAsync(context, language, "administrator");
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request => request.Method == HttpMethod.Get
            ? WebTestHandler.Json(Array.Empty<MemberItem>())
            : new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = "Synthetic refusal" }) }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        page.Find("#member-email").Change("new@example.invalid");
        page.Find("#initial-password").Change("synthetic-initial-password");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Synthetic refusal", page.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("synthetic-initial-password", page.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es", "La contraseña se cambió. Inicie sesión de nuevo.")]
    [InlineData("en", "Password changed. Sign in again.")]
    public async Task Account_changes_password_once_clears_session_and_shows_notice_on_login(string language, string notice)
    {
        await using var context = new BunitContext();
        var session = await RegisterAsync(context, language, "viewer");
        var calls = 0;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            calls++;
            Assert.Equal("/auth/password", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-access", request.Headers.Authorization!.Parameter);
            Assert.False(request.Headers.Contains("X-Company-Id"));
            var body = request.Content!.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.Equal("synthetic-current-password", body.GetProperty("currentPassword").GetString());
            Assert.Equal("synthetic-new-password", body.GetProperty("newPassword").GetString());
            return new(HttpStatusCode.NoContent);
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Account>();
        FillAccount(page, "synthetic-new-password", "synthetic-new-password");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.False(session.IsAuthenticated));
        Assert.Null(session.Company);
        Assert.Equal(1, calls);
        Assert.EndsWith("/ui/login", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-new-password", page.Markup, StringComparison.Ordinal);
        var login = context.Render<Login>();
        Assert.Contains(notice, login.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es", "short", "short", "al menos 12 caracteres")]
    [InlineData("en", "short", "short", "at least 12 characters")]
    [InlineData("es", "synthetic-new-password", "different-password", "no coinciden")]
    [InlineData("en", "synthetic-new-password", "different-password", "do not match")]
    public async Task Account_validates_before_sending_and_clears_password_fields(string language, string password, string confirmation, string expected)
    {
        await using var context = new BunitContext();
        await RegisterAsync(context, language, "accountant");
        var calls = 0;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => { calls++; return new(HttpStatusCode.NoContent); })) { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Account>();
        FillAccount(page, password, confirmation);
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains(expected, page.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Equal(0, calls);
        Assert.All(page.FindAll("input[type=password]"), field => Assert.Equal("", field.GetAttribute("value")));
    }

    [Theory]
    [InlineData("es", HttpStatusCode.Unauthorized, "No se pudo verificar la contraseña actual")]
    [InlineData("en", HttpStatusCode.Unauthorized, "The current password could not be verified")]
    [InlineData("es", HttpStatusCode.Forbidden, "Esta sesión no puede cambiar la contraseña")]
    [InlineData("en", HttpStatusCode.Forbidden, "This session cannot change the password")]
    [InlineData("es", HttpStatusCode.BadRequest, "al menos 12 caracteres")]
    [InlineData("en", HttpStatusCode.BadRequest, "at least 12 characters")]
    public async Task Account_handles_real_error_codes_without_refreshing_or_replaying_passwords(string language, HttpStatusCode status, string expected)
    {
        await using var context = new BunitContext();
        var session = await RegisterAsync(context, language, "viewer");
        var calls = 0;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => { calls++; return new(status); })) { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Account>();
        FillAccount(page, "synthetic-new-password", "synthetic-new-password");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains(expected, page.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Equal(1, calls);
        Assert.True(session.IsAuthenticated);
        Assert.All(page.FindAll("input[type=password]"), field => Assert.Equal("", field.GetAttribute("value")));
    }

    [Theory]
    [InlineData("es", "Mi cuenta")]
    [InlineData("en", "My account")]
    public async Task Account_is_accessible_from_the_shell_without_company_memberships(string language, string heading)
    {
        await using var context = new BunitContext();
        var session = await RegisterAsync(context, language, "viewer");
        session.SwitchCompany();
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(Array.Empty<CompanyItem>()))) { BaseAddress = new Uri("https://app.example.invalid/") });
        context.Services.AddTransient<WorkspaceViewModel>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/ui/account?lang={language}");
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Equal(heading, app.Find("h1").TextContent));
        Assert.Single(app.FindAll("a[href='/ui/account']"));
        Assert.NotEmpty(app.FindAll("#current-password"));
        Assert.Empty(app.FindAll("button[data-company]"));
    }

    [Theory]
    [InlineData("es", "ya pertenece")]
    [InlineData("en", "already belongs")]
    public async Task Team_add_form_does_not_edit_an_existing_members_role(string language, string expected)
    {
        await using var context = new BunitContext();
        await RegisterAsync(context, language, "administrator");
        var calls = 0;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return WebTestHandler.Json(new MemberItem[] { new(Guid.NewGuid(), "existing@example.invalid", "viewer") });
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        page.Find("#member-email").Change("EXISTING@example.invalid");
        page.Find("#member-role").Change("accountant");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains(expected, page.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("es", "La contraseña inicial debe tener al menos 12 caracteres.")]
    [InlineData("en", "The initial password must have at least 12 characters.")]
    public async Task Team_rejects_a_short_initial_password_without_a_request(string language, string expected)
    {
        await using var context = new BunitContext();
        await RegisterAsync(context, language, "administrator");
        var posts = 0;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return WebTestHandler.Json(Array.Empty<MemberItem>());
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        page.Find("#member-email").Change("new@example.invalid");
        page.Find("#initial-password").Change("short");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal(expected, page.Find("[role=alert]").TextContent));
        Assert.Equal(0, posts);
        Assert.Equal("", page.Find("#initial-password").GetAttribute("value"));
    }

    [Theory]
    [InlineData("es", HttpStatusCode.BadRequest, "error", "La contraseña inicial debe tener al menos 12 caracteres.")]
    [InlineData("en", HttpStatusCode.BadRequest, "error", "The initial password must have at least 12 characters.")]
    [InlineData("es", HttpStatusCode.BadRequest, "detail", "La contraseña inicial debe tener al menos 12 caracteres.")]
    [InlineData("en", HttpStatusCode.BadRequest, "detail", "The initial password must have at least 12 characters.")]
    [InlineData("es", HttpStatusCode.Conflict, "error", "La empresa debe conservar al menos un administrador.")]
    [InlineData("en", HttpStatusCode.Conflict, "error", "The company must retain at least one administrator.")]
    [InlineData("es", HttpStatusCode.Conflict, "detail", "La empresa debe conservar al menos un administrador.")]
    [InlineData("en", HttpStatusCode.Conflict, "detail", "The company must retain at least one administrator.")]
    public async Task Team_localizes_known_server_refusals_and_sends_empty_passwords_for_server_validation(string language, HttpStatusCode status, string field, string expected)
    {
        await using var context = new BunitContext();
        await RegisterAsync(context, language, "administrator");
        var posts = 0;
        var raw = status == HttpStatusCode.BadRequest ? "Initial password requires at least 12 characters." : "The company must retain at least one administrator.";
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return WebTestHandler.Json(Array.Empty<MemberItem>());
            posts++;
            var body = request.Content!.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            if (status == HttpStatusCode.BadRequest) Assert.Equal(JsonValueKind.Null, body.GetProperty("initialPassword").ValueKind);
            return new(status) { Content = JsonContent.Create(new Dictionary<string, string> { [field] = raw }) };
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        page.Find("#member-email").Change("new@example.invalid");
        if (status == HttpStatusCode.Conflict) page.Find("#initial-password").Change("synthetic-initial-password");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal(expected, page.Find("[role=alert]").TextContent));
        Assert.Equal(1, posts);
        Assert.Empty(page.FindAll("[role=alert] small"));
        Assert.Equal("", page.Find("#initial-password").GetAttribute("value"));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Team_can_attach_an_existing_account_without_an_initial_password(string language)
    {
        await using var context = new BunitContext();
        await RegisterAsync(context, language, "administrator");
        var added = false;
        context.Services.AddSingleton(new HttpClient(new WebTestHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content!.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Assert.Equal("existing@example.invalid", body.GetProperty("email").GetString());
                Assert.Equal(JsonValueKind.Null, body.GetProperty("initialPassword").ValueKind);
                added = true;
                return new(HttpStatusCode.NoContent);
            }
            return WebTestHandler.Json(added ? new MemberItem[] { new(Guid.NewGuid(), "existing@example.invalid", "accountant") } : []);
        }))
        { BaseAddress = new Uri("https://app.example.invalid/") });
        var page = context.Render<Team>();
        page.Find("#member-email").Change("existing@example.invalid");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("existing@example.invalid", page.Find("tbody").TextContent, StringComparison.Ordinal));
        Assert.True(added);
        Assert.Empty(page.FindAll("[role=alert]"));
    }

    private static void FillAccount(IRenderedComponent<Account> page, string password, string confirmation)
    {
        page.Find("#current-password").Change("synthetic-current-password");
        page.Find("#new-password").Change(password);
        page.Find("#confirm-password").Change(confirmation);
    }

    private static async Task<SessionState> RegisterAsync(BunitContext context, string language, string role, bool archived = false)
    {
        TestLanguage.Register(context, language);
        var session = new SessionState();
        using var loginHttp = new HttpClient(new WebTestHandler(_ => WebTestHandler.Json(new SessionTokens("synthetic-access", 900)))) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var loginApi = new ApiClient(loginHttp, session);
        await loginApi.LoginAsync("synthetic@example.invalid", "synthetic-password", TestContext.Current.CancellationToken);
        session.SelectCompany(new(Guid.NewGuid(), "AAA010101AAA", "Synthetic company", archived ? DateTime.UtcNow : null, role));
        context.Services.AddSingleton(session);
        context.Services.AddSingleton<ApiClient>();
        context.Services.AddTransient<TeamViewModel>();
        context.Services.AddTransient<AccountViewModel>();
        return session;
    }
}
