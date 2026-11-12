using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Reconciliation.Web;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MemberAccountTests(ApiFixture fixture)
{
    [Fact]
    public async Task Administrator_can_create_an_account_and_attach_it_without_returning_credentials()
    {
        var (admin, company) = await fixture.SeedCompanyWithAdminAsync();
        using var logs = new CredentialLogCapture();
        using var host = new SessionTestHost(fixture, builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        host.Authorize(await host.LoginAsync(admin.Email), company);
        var client = host.Client;
        var email = $"member-{Guid.NewGuid():N}@example.invalid";
        const string password = "synthetic-new-member-password";
        using var response = await client.PostAsJsonAsync($"/companies/{company}/members", new { email, role = "accountant", initialPassword = password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var member = fixture.App.CreateClient();
        using var login = await member.PostAsJsonAsync("/auth/tokens", new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var pair = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        member.DefaultRequestHeaders.Authorization = new("Bearer", pair.GetProperty("accessToken").GetString());
        var companies = await member.GetFromJsonAsync<JsonElement>("/companies", TestContext.Current.CancellationToken);
        var entry = Assert.Single(companies.EnumerateArray());
        Assert.Equal(company, entry.GetProperty("id").GetGuid());
        Assert.Equal("accountant", entry.GetProperty("role").GetString());
        var audit = await client.GetStringAsync($"/companies/{company}/audit-entries", TestContext.Current.CancellationToken);
        Assert.Contains("account.created", audit, StringComparison.Ordinal);
        Assert.Contains("member.added", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(password, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("password", audit, StringComparison.OrdinalIgnoreCase);
        var hash = await fixture.Database.ScalarAsync<string>("SELECT password_hash FROM app_user WHERE email=@email", new { email });
        Assert.NotEqual(password, hash);
        Assert.All(logs.Messages, message =>
        {
            Assert.DoesNotContain(password, message, StringComparison.Ordinal);
            Assert.DoesNotContain(hash!, message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Existing_email_keeps_credentials_and_reattaching_is_an_unaudited_noop()
    {
        var (admin, company) = await fixture.SeedCompanyWithAdminAsync();
        var existing = await fixture.SeedUserAsync();
        using var client = await fixture.LoginAsync(admin.Email, company);
        for (var i = 0; i < 2; i++)
        {
            using var attached = await client.PostAsJsonAsync($"/companies/{company}/members",
                new { email = existing.Email.ToUpperInvariant(), role = "viewer", initialPassword = "ignored" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, attached.StatusCode);
        }
        using var login = await fixture.LoginAsync(existing.Email, company);
        using var read = await login.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var audit = await client.GetFromJsonAsync<JsonElement>($"/companies/{company}/audit-entries", TestContext.Current.CancellationToken);
        var entry = Assert.Single(audit.GetProperty("entries").EnumerateArray());
        Assert.Equal("member.added", entry.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("neither")]
    [InlineData("both")]
    [InlineData("invalid-email")]
    [InlineData("empty-email")]
    [InlineData("missing-password")]
    [InlineData("short-password")]
    [InlineData("invalid-role")]
    [InlineData("unknown-user")]
    public async Task Invalid_member_creation_is_atomic(string scenario)
    {
        var (admin, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(admin.Email, company);
        var email = $"invalid-{Guid.NewGuid():N}@example.invalid";
        var body = new Dictionary<string, object?> { ["email"] = email, ["role"] = "viewer", ["initialPassword"] = ApiFixture.Password };
        switch (scenario)
        {
            case "neither": body.Remove("email"); break;
            case "both": body["userId"] = admin.Id; break;
            case "invalid-email": body["email"] = "not-an-email"; break;
            case "empty-email": body["email"] = ""; break;
            case "missing-password": body.Remove("initialPassword"); break;
            case "short-password": body["initialPassword"] = "short"; break;
            case "invalid-role": body["role"] = "operator"; break;
            case "unknown-user": body.Remove("email"); body["userId"] = Guid.NewGuid(); break;
        }
        using var denied = await client.PostAsJsonAsync($"/companies/{company}/members", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM app_user WHERE email=@email)", new { email }));
        var audit = await client.GetFromJsonAsync<JsonElement>($"/companies/{company}/audit-entries", TestContext.Current.CancellationToken);
        Assert.Empty(audit.GetProperty("entries").EnumerateArray());
    }

    [Theory]
    [InlineData("viewer", false, HttpStatusCode.Forbidden)]
    [InlineData("accountant", false, HttpStatusCode.Forbidden)]
    [InlineData("administrator", true, HttpStatusCode.Conflict)]
    public async Task Only_an_active_company_administrator_can_create_members(string role, bool archived, HttpStatusCode expected)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, role);
        if (archived)
        {
            await using var owner = await fixture.Database.OpenOwnerAsync();
            await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        }
        using var client = await fixture.LoginAsync(user.Email, company);
        var email = $"denied-{Guid.NewGuid():N}@example.invalid";
        using var denied = await client.PostAsJsonAsync($"/companies/{company}/members", new { email, role = "viewer", initialPassword = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, denied.StatusCode);
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM app_user WHERE email=@email)", new { email }));
    }

    [Fact]
    public async Task Concurrent_attachment_to_two_companies_creates_one_account_without_resetting_it()
    {
        var (a, companyA) = await fixture.SeedCompanyWithAdminAsync();
        var (b, companyB) = await fixture.SeedCompanyWithAdminAsync();
        using var clientA = await fixture.LoginAsync(a.Email, companyA);
        using var clientB = await fixture.LoginAsync(b.Email, companyB);
        var email = $"concurrent-{Guid.NewGuid():N}@example.invalid";
        var body = new { email, role = "accountant", initialPassword = ApiFixture.Password };
        var responses = await Task.WhenAll(clientA.PostAsJsonAsync($"/companies/{companyA}/members", body, TestContext.Current.CancellationToken),
            clientB.PostAsJsonAsync($"/companies/{companyB}/members", body, TestContext.Current.CancellationToken));
        foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); }
        using var login = await fixture.LoginAsync(email);
        var companies = await login.GetFromJsonAsync<JsonElement>("/companies", TestContext.Current.CancellationToken);
        Assert.Equal(2, companies.GetArrayLength());
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id IN (@companyA,@companyB) AND action='account.created'", new { companyA, companyB }));
    }

    [Fact]
    public async Task Account_client_changes_real_credentials_revokes_refresh_sessions_and_never_logs_passwords()
    {
        var user = await fixture.SeedUserAsync();
        using var logs = new CredentialLogCapture();
        using var host = new SessionTestHost(fixture, builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        var earlierSession = await host.LoginAsync(user.Email);
        var state = new SessionState();
        using var api = new ApiClient(host.Client, state);
        await api.LoginAsync(user.Email, ApiFixture.Password, TestContext.Current.CancellationToken);
        const string password = "synthetic-changed-account-password";
        await api.ChangePasswordAsync(ApiFixture.Password, password, TestContext.Current.CancellationToken);
        Assert.False(state.IsAuthenticated);
        Assert.True(state.PasswordChangeCompleted);
        using var revoked = await host.RefreshAsync(earlierSession.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var oldLogin = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        await host.LoginAsync(user.Email, password);
        var hash = await fixture.Database.ScalarAsync<string>("SELECT password_hash FROM app_user WHERE id=@Id", new { user.Id });
        Assert.All(logs.Messages, message =>
        {
            Assert.DoesNotContain(password, message, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiFixture.Password, message, StringComparison.Ordinal);
            Assert.DoesNotContain(hash!, message, StringComparison.Ordinal);
        });
    }

    private sealed class CredentialLogCapture : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
