using Npgsql;

namespace Reconciliation.Migrator;

internal static class AppRoleBootstrap
{
    public static async Task ConfigureAsync(string connectionString)
    {
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
}
