using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Migrator;

internal static class MigrationCommands
{
    public static async Task RunAsync(string command, string connectionString)
    {
        using var migrationServices = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationCommands).Assembly).For.Migrations())
            .AddLogging(logging => logging.AddFluentMigratorConsole())
            .BuildServiceProvider(validateScopes: false);

        using var migrationScope = migrationServices.CreateScope();
        var migrationRunner = migrationScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        if (command == "up")
        {
            migrationRunner.MigrateUp();
            var operatorEmail = Environment.GetEnvironmentVariable("OPERATOR_EMAIL");
            var operatorPassword = Environment.GetEnvironmentVariable("OPERATOR_PASSWORD");
            if (!string.IsNullOrEmpty(operatorEmail) || !string.IsNullOrEmpty(operatorPassword))
                await OperatorBootstrap.SeedAsync(connectionString, operatorEmail ?? "", operatorPassword ?? "");
            await AppRoleBootstrap.ConfigureAsync(connectionString);
        }
        else
        {
            migrationRunner.Rollback(1);
        }

    }
}
