using System.Data;
using Dapper;

namespace Reconciliation.Core.Common;

public static class DapperExtensions
{
    public static CommandDefinition Command(string sql, object? parameters = null, IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) => new(sql, parameters, transaction, cancellationToken: cancellationToken);
}
