using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reconciliation.Core.Common;

namespace Reconciliation.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeDatabaseHealth(bool reachable) : IDatabaseHealth
    {
        public Task<bool> IsReachableAsync(CancellationToken cancellationToken = default) => Task.FromResult(reachable);
    }

    private HttpClient ClientWithDatabase(bool reachable) =>
        factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Jwt:SigningKey", "synthetic-test-signing-key-at-least-32-bytes");
                builder.UseSetting("ConnectionStrings:Default", "Host=localhost;Username=test;Password=test;Database=test");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDatabaseHealth>();
                    services.AddSingleton<IDatabaseHealth>(new FakeDatabaseHealth(reachable));
                });
            })
            .CreateClient();

    [Fact]
    public async Task Health_returns_200_when_the_database_is_reachable()
    {
        using var client = ClientWithDatabase(reachable: true);

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"database\":\"up\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Health_returns_503_when_the_database_is_down()
    {
        using var client = ClientWithDatabase(reachable: false);

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
