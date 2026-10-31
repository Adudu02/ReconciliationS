using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Reconciliation.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    public const string Password = "synthetic-password-123";
    public PostgresFixture Database { get; } = new();
    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", Database.AppConnectionString);
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
