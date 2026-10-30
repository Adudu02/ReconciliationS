using Npgsql;

namespace Reconciliation.Core.Common;

public sealed class NpgsqlDatabaseHealth(NpgsqlDataSource dataSource) : IDatabaseHealth
{
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }
}
