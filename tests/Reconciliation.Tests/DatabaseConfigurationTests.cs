using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Common;

namespace Reconciliation.Tests;

public sealed class DatabaseConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void Missing_connection_string_is_a_clear_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddReconciliationDatabase(Config()));

        Assert.Contains("ConnectionStrings__Default", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_string_registers_the_database_health_check()
    {
        var services = new ServiceCollection().AddReconciliationDatabase(
            Config(("ConnectionStrings:Default", "Host=localhost;Username=u;Password=p;Database=d")));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<NpgsqlDatabaseHealth>(provider.GetRequiredService<IDatabaseHealth>());
    }
}
