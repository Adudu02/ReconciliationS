using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Migrator;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CompanyManagementTests(ApiFixture fixture)
{
    private async Task<HttpClient> OperatorAsync()
    {
        var email = $"operator-{Guid.NewGuid():N}@example.invalid";
        await OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, email, ApiFixture.Password, TestContext.Current.CancellationToken);
        return await fixture.LoginAsync(email);
    }

    [Fact]
    public async Task Company_created_with_first_administrator_and_audit()
    {
        using var client = await OperatorAsync();
        var email = $"admin-{Guid.NewGuid():N}@example.invalid";
        using var response = await client.PostAsJsonAsync("/companies", new { rfc = "AAA010101AAA", name = "Synthetic company", adminEmail = email, adminPassword = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal($"/companies/{id}", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("password", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var admin = await fixture.LoginAsync(email, id);
        using var company = await admin.GetAsync($"/companies/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, company.StatusCode);
        using var members = await admin.GetAsync($"/companies/{id}/members", TestContext.Current.CancellationToken);
        Assert.Contains("administrator", await members.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM audit_entry WHERE company_id = @id AND action = 'company.created'", new { id }, cancellationToken: TestContext.Current.CancellationToken)));
        var hash = await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT password_hash FROM app_user WHERE email = @email", new { email }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotEqual(ApiFixture.Password, hash);
        Assert.True(Convert.FromBase64String(hash).Length >= 49);
    }

    [Fact]
    public async Task Existing_administrator_keeps_password_and_response_shape()
    {
        var existing = await fixture.SeedUserAsync();
        using var client = await OperatorAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var before = await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT password_hash FROM app_user WHERE id = @Id", new { existing.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var attached = await client.PostAsJsonAsync("/companies", new { rfc = "AAA010101AAA", name = "Synthetic existing admin", adminEmail = existing.Email, adminPassword = "ignored" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        var existingJson = await attached.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        using var created = await client.PostAsJsonAsync("/companies", new { rfc = "BBB010101BBB", name = "Synthetic new admin", adminEmail = $"new-{Guid.NewGuid():N}@example.invalid", adminPassword = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var newJson = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(existingJson.EnumerateObject().Select(p => p.Name).Order(), newJson.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(before, await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT password_hash FROM app_user WHERE id = @Id", new { existing.Id }, cancellationToken: TestContext.Current.CancellationToken)));
        using var signedIn = await fixture.LoginAsync(existing.Email, existingJson.GetProperty("id").GetGuid());
        using var members = await signedIn.GetAsync($"/companies/{existingJson.GetProperty("id").GetGuid()}/members", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
    }

    [Theory]
    [InlineData("INVALID", "admin@example.invalid", ApiFixture.Password, "RFC")]
    [InlineData("AAA010101AAA", "not-an-email", ApiFixture.Password, "email")]
    [InlineData("AAA010101AAA", "short-new@example.invalid", "short", "12")]
    public async Task Invalid_company_input_is_rejected_with_reason_and_no_partial_rows(string rfc, string email, string password, string reason)
    {
        using var client = await OperatorAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var before = await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM company", cancellationToken: TestContext.Current.CancellationToken));
        using var response = await client.PostAsJsonAsync("/companies", new { rfc, name = "Synthetic invalid", adminEmail = email, adminPassword = password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM company", cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Not_an_operator_cannot_create_company()
    {
        var (user, id) = await fixture.SeedCompanyWithAdminAsync();
        using var admin = await fixture.LoginAsync(user.Email, id);
        using var response = await admin.PostAsJsonAsync("/companies", new { rfc = "AAA010101AAA", name = "Synthetic refused", adminEmail = "synthetic@example.invalid", adminPassword = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operator_bootstrap_is_idempotent_and_does_not_reset_credentials_or_require_membership()
    {
        var email = $"bootstrap-{Guid.NewGuid():N}@example.invalid";
        await OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, email, ApiFixture.Password, TestContext.Current.CancellationToken);
        await OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, email, "different-synthetic-password", TestContext.Current.CancellationToken);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM membership m JOIN app_user u ON u.id = m.user_id WHERE u.email = @email", new { email }, cancellationToken: TestContext.Current.CancellationToken)));
        using var client = await fixture.LoginAsync(email);
        await Assert.ThrowsAsync<ArgumentException>(() => OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, "invalid-email", "short", TestContext.Current.CancellationToken));
    }
}
