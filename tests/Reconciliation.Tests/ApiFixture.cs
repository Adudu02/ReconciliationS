extern alias api;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ApiProgram = api::Program;

namespace Reconciliation.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    public const string Password = "synthetic-password-123";
    public PostgresFixture Database { get; } = new();
    public WebApplicationFactory<ApiProgram> App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        App = new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", Database.AppConnectionString);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RoleProbeStartup>());
            builder.UseSetting("Jwt:SigningKey", "synthetic-test-signing-key-at-least-32-bytes");
        });
    }

    public async Task<(Guid Id, string Email)> SeedUserAsync()
    {
        var id = Guid.NewGuid();
        var email = $"synthetic-{id:N}@example.invalid";
        var hasher = App.Services.GetRequiredService<IPasswordHasher<string>>();
        var hash = hasher.HashPassword(email, Password);
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        await owner.ExecuteAsync(new CommandDefinition("INSERT INTO app_user(id, email, password_hash) VALUES (@id, @email, @hash)",
            new { id, email, hash }, cancellationToken: TestContext.Current.CancellationToken));
        return (id, email);
    }

    public async Task<Guid> SeedCompanyAsync(Guid userId, string role = "administrator")
    {
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        var id = await owner.QuerySingleAsync<Guid>(new CommandDefinition(
            "INSERT INTO company(rfc, name) VALUES ('AAA010101AAA', 'Synthetic company') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("INSERT INTO membership(company_id, user_id, role) VALUES (@id, @userId, @role::membership_role)",
            new { id, userId, role }, cancellationToken: TestContext.Current.CancellationToken));
        return id;
    }

    private sealed class RoleProbeStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            // The real link-decision endpoint arrives in group 8; this exercises its policy only.
            app.UseEndpoints(routes => routes.MapPost("/test/decisions", () => Results.NoContent()).RequireAuthorization("accountant"));
        };
    }

    public async Task<HttpClient> LoginAsync(string email, Guid? companyId = null)
    {
        var client = App.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/tokens", new { email, password = Password }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        if (companyId is not null) client.DefaultRequestHeaders.Add("X-Company-Id", companyId.ToString());
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Database.DisposeAsync();
    }
}

[CollectionDefinition("tenancy-api")]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>;
