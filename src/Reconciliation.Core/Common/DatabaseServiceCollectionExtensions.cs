using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Reconciliation.Core.Common;

public static class DatabaseServiceCollectionExtensions
{
    public const string ConnectionStringName = "Default";

    /// <summary>Registers the shared PostgreSQL data source from ConnectionStrings:Default.</summary>
    public static IServiceCollection AddReconciliationDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Missing connection string 'ConnectionStrings:{ConnectionStringName}' (environment variable ConnectionStrings__{ConnectionStringName}).");
        }

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<Reconciliation.Core.Tenancy.CompanyScope>();
        services.AddSingleton<Reconciliation.Core.Tenancy.CompanyDirectory>();
        services.AddSingleton<IDatabaseHealth, NpgsqlDatabaseHealth>();
        return services;
    }
}
