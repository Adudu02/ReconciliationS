using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CompanySettingsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Tolerance_changed_is_persisted_and_audits_previous_and_new_values()
    {
        var (user, id) = await fixture.SeedCompanyWithAdminAsync();
        using var admin = await fixture.LoginAsync(user.Email, id);
        using var changed = await admin.PatchAsJsonAsync($"/companies/{id}/settings", new { amountTolerance = 0.02m, dateToleranceDays = 5, complementNextBizDay = true }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var read = await admin.GetAsync($"/companies/{id}/settings", TestContext.Current.CancellationToken);
        var json = await read.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0.02m, json.GetProperty("amountTolerance").GetDecimal());
        Assert.Equal(5, json.GetProperty("dateToleranceDays").GetInt32());
        Assert.True(json.GetProperty("complementNextBizDay").GetBoolean());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var entry = await owner.QuerySingleAsync<(Guid Actor, string Old, string New)>(new CommandDefinition("SELECT actor_id, old_values::text, new_values::text FROM audit_entry WHERE company_id = @id AND action = 'company.settings_changed'", new { id }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(user.Id, entry.Actor);
        using var oldValues = JsonDocument.Parse(entry.Old);
        using var newValues = JsonDocument.Parse(entry.New);
        Assert.Equal(0.01m, oldValues.RootElement.GetProperty("amountTolerance").GetDecimal());
        Assert.Equal(0.02m, newValues.RootElement.GetProperty("amountTolerance").GetDecimal());
    }

    [Theory]
    [InlineData("accountant")]
    [InlineData("viewer")]
    public async Task Accountant_cannot_change_settings_and_nothing_is_audited(string role)
    {
        var user = await fixture.SeedUserAsync();
        var id = await fixture.SeedCompanyAsync(user.Id, role);
        using var client = await fixture.LoginAsync(user.Email, id);
        using var refused = await client.PatchAsJsonAsync($"/companies/{id}/settings", new { amountTolerance = 0.02m }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var read = await client.GetAsync($"/companies/{id}/settings", TestContext.Current.CancellationToken);
        var json = await read.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0.01m, json.GetProperty("amountTolerance").GetDecimal());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM audit_entry WHERE company_id = @id", new { id }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("{\"amountTolerance\":-0.01}")]
    [InlineData("{\"amountTolerance\":0.001}")]
    [InlineData("{\"dateToleranceDays\":-1}")]
    [InlineData("{\"rfc\":\"BBB010101BBB\"}")]
    public async Task Invalid_settings_and_RFC_changes_are_rejected(string body)
    {
        var (user, id) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, id);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync($"/companies/{id}/settings", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RFC_is_immutable_even_through_direct_database_update()
    {
        var (user, id) = await fixture.SeedCompanyWithAdminAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("UPDATE company SET rfc = 'BBB010101BBB' WHERE id = @id", new { id }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Archive_keeps_company_memberships_and_audits_and_no_DELETE_route_exists()
    {
        var (user, id) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, id);
        using var archived = await client.PostAsync($"/companies/{id}/archive", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        using var read = await client.GetAsync($"/companies/{id}", TestContext.Current.CancellationToken);
        var company = await read.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.NotEqual(JsonValueKind.Null, company.GetProperty("archivedAt").ValueKind);
        using var delete = await client.DeleteAsync($"/companies/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        var endpoints = fixture.App.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE", StringComparer.Ordinal) == true);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM membership WHERE company_id = @id", new { id }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM audit_entry WHERE company_id = @id AND action = 'company.archived'", new { id }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_settings_change()
    {
        var (user, id) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, id);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("REVOKE INSERT ON audit_entry FROM cfdi_app", cancellationToken: TestContext.Current.CancellationToken));
        try
        {
            using var failed = await client.PatchAsJsonAsync($"/companies/{id}/settings", new { amountTolerance = 0.02m }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(0.01m, await owner.QuerySingleAsync<decimal>(new CommandDefinition("SELECT amount_tolerance FROM company WHERE id = @id", new { id }, cancellationToken: TestContext.Current.CancellationToken)));
        }
        finally
        {
            await owner.ExecuteAsync(new CommandDefinition("GRANT INSERT ON audit_entry TO cfdi_app", cancellationToken: CancellationToken.None));
        }
    }
}
