using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Migrator;

string[] supportedCommands = ["up", "down"];
if (args.Length != 1 || !supportedCommands.Contains(args[0]))
{
    Console.Error.WriteLine("Usage: Reconciliation.Migrator up|down   (down reverts one migration)");
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set the environment variable ConnectionStrings__Default.");
    return 2;
}

try
{
    using var migrationServices = new ServiceCollection()
        .AddFluentMigratorCore()
        .ConfigureRunner(runner => runner
            .AddPostgres()
            .WithGlobalConnectionString(connectionString)
            .ScanIn(typeof(Program).Assembly).For.Migrations())
        .AddLogging(logging => logging.AddFluentMigratorConsole())
        .BuildServiceProvider(validateScopes: false);

    using var migrationScope = migrationServices.CreateScope();
    var migrationRunner = migrationScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    if (args[0] == "up")
    {
        migrationRunner.MigrateUp();
        var operatorEmail = Environment.GetEnvironmentVariable("OPERATOR_EMAIL");
        var operatorPassword = Environment.GetEnvironmentVariable("OPERATOR_PASSWORD");
        if (!string.IsNullOrEmpty(operatorEmail) || !string.IsNullOrEmpty(operatorPassword))
            await OperatorBootstrap.SeedAsync(connectionString, operatorEmail ?? "", operatorPassword ?? "");
        var appRolePassword = Environment.GetEnvironmentVariable("APP_DB_PASSWORD");
        if (!string.IsNullOrEmpty(appRolePassword))
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var formatRoleCommand = new NpgsqlCommand("SELECT format('ALTER ROLE cfdi_app LOGIN PASSWORD %L', @password)", connection);
            formatRoleCommand.Parameters.AddWithValue("password", appRolePassword);
            var alterRoleSql = (string)(await formatRoleCommand.ExecuteScalarAsync())!;
            await using var alterRoleCommand = new NpgsqlCommand(alterRoleSql, connection);
            await alterRoleCommand.ExecuteNonQueryAsync();
        }
    }
    else
    {
        migrationRunner.Rollback(1);
    }

    return 0;
}
#pragma warning disable CA1031 // Program boundary: any failure must become a clear message and a non-zero exit code.
catch (Exception exception)
#pragma warning restore CA1031
{
    Console.Error.WriteLine($"Migration failed: {exception.Message}");
    return 1;
}
