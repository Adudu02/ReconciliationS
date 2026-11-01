using System.Data;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

internal sealed class StoredOriginalResult(TenantContext tenant, Guid companyId, Guid resourceId, string sql, string contentType, string fileName) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await tenant.ExecuteAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("resourceId", resourceId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, transactionCancellationToken);
            if (!await reader.ReadAsync(transactionCancellationToken) || await reader.IsDBNullAsync(0, transactionCancellationToken))
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return 0;
            }
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await using var stream = reader.GetStream(0);
            await Results.Stream(stream, contentType, fileName).ExecuteAsync(httpContext);
            return 0;
        }, httpContext.RequestAborted);
    }
}
