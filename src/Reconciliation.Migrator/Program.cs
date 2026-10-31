using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

string[] commands = ["up", "down"];
if (args.Length != 1 || !commands.Contains(args[0]))
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
    using var provider = new ServiceCollection()
        .AddFluentMigratorCore()
        .ConfigureRunner(runner => runner
            .AddPostgres()
            .WithGlobalConnectionString(connectionString)
            .ScanIn(typeof(Program).Assembly).For.Migrations())
        .AddLogging(logging => logging.AddFluentMigratorConsole())
        .BuildServiceProvider(validateScopes: false);

    using var scope = provider.CreateScope();
    var migrations = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    if (args[0] == "up")
    {
        migrations.MigrateUp();
        var appPassword = Environment.GetEnvironmentVariable("APP_DB_PASSWORD");
        if (!string.IsNullOrEmpty(appPassword))
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var format = new NpgsqlCommand("SELECT format('ALTER ROLE cfdi_app LOGIN PASSWORD %L', @password)", connection);
            format.Parameters.AddWithValue("password", appPassword);
            var sql = (string)(await format.ExecuteScalarAsync())!;
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
    else
    {
        migrations.Rollback(1);
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
