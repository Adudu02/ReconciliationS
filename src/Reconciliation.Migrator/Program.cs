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
    await MigrationCommands.RunAsync(args[0], connectionString);
    return 0;
}
#pragma warning disable CA1031 // Program boundary: any failure must become a clear message and a non-zero exit code.
catch (Exception exception)
#pragma warning restore CA1031
{
    Console.Error.WriteLine($"Migration failed: {exception.Message}");
    return 1;
}
