using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuditRefusalTests(ApiFixture fixture)
{
    [Theory(DisplayName = "Attempt to modify")]
    [InlineData("UPDATE audit_entry SET action='changed' WHERE company_seq=1")]
    [InlineData("DELETE FROM audit_entry WHERE company_seq=1")]
    public async Task Refusal_is_recorded_in_a_separate_transaction_after_the_failed_transaction_rolls_back(string sql)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var scope = fixture.App.Services.GetRequiredService<CompanyScope>();
        await scope.ExecuteAsync(company, (connection, transaction, _) => AuditDatabaseTests.AppendAsync(connection, transaction, company, user.Id), TestContext.Current.CancellationToken);
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(company,
            (connection, transaction, token) => connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: token)), TestContext.Current.CancellationToken));
        // Internal seam only; no route permits an audit mutation.
        await AuditLog.RecordModificationRefusedAsync(scope, company, user.Id, 1, refusal, TestContext.Current.CancellationToken);
        await scope.ExecuteAsync(company, async (connection, transaction, token) =>
        {
            var entry = await connection.QuerySingleAsync<(Guid Actor, long Target, string Action)>(new CommandDefinition(
                "SELECT actor_id,(detail->'new'->>'targetSeq')::bigint,action FROM audit_entry WHERE company_seq=2", transaction: transaction, cancellationToken: token));
            Assert.Equal((user.Id, 1L, "audit.modification_refused"), entry);
            Assert.Equal(new AuditChainTests.Verification(true, 2, null, null), await AuditChainTests.VerifyAsync(connection, transaction, company));
            return 0;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Refusal_seam_rejects_unrelated_errors_and_targets_outside_the_company()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var scope = fixture.App.Services.GetRequiredService<CompanyScope>();
        await Assert.ThrowsAsync<ArgumentException>(() => AuditLog.RecordModificationRefusedAsync(scope, company, user.Id, 1,
            new PostgresException("synthetic unrelated failure", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation), TestContext.Current.CancellationToken));
        var refusal = new PostgresException("permission denied for table audit_entry", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege);
        await Assert.ThrowsAsync<ArgumentException>(() => AuditLog.RecordModificationRefusedAsync(scope, company, user.Id, 1, refusal, TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company }));
    }
}
