using Dapper;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record DirectoryCompany(Guid Id, string Rfc, string Name, DateTime? ArchivedAt, string Role);

public sealed class CompanyDirectory(NpgsqlDataSource dataSource)
{
    public async Task<IEnumerable<DirectoryCompany>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(Command("SELECT set_config('app.user_id',@value,true)", new { value = userId.ToString() }, transaction, cancellationToken: cancellationToken));
        var companies = await connection.QueryAsync<DirectoryCompany>(Command("SELECT id,rfc,name,archived_at AS ArchivedAt,role FROM user_companies(@userId)", new { userId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return companies;
    }
}
