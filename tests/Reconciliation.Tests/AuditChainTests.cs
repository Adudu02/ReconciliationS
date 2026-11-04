using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuditChainTests(ApiFixture fixture)
{
    internal sealed record Verification(bool Consistent, long Entries, long? FirstInconsistentSeq, string? Reason);
    internal static Task<Verification> VerifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid company) =>
        connection.QuerySingleAsync<Verification>(new CommandDefinition("""
            SELECT consistent,entries,first_inconsistent_seq AS FirstInconsistentSeq,reason FROM verify_audit_chain(@company)
            """, new { company }, transaction, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(DisplayName = "Intact log")]
    public async Task Intact_log_and_empty_log_are_consistent_and_verification_is_read_only_under_RLS()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(connection, tx, company);
        Assert.Equal(new Verification(true, 0, null, null), await VerifyAsync(connection, tx, company));
        for (var i = 0; i < 3; i++) await AuditDatabaseTests.AppendAsync(connection, tx, company, user.Id);
        Assert.Equal(new Verification(true, 3, null, null), await VerifyAsync(connection, tx, company));
        var other = await fixture.SeedCompanyAsync(user.Id);
        Assert.Equal(new Verification(true, 0, null, null), await VerifyAsync(connection, tx, other));
        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT NOT prosecdef AND provolatile='s' FROM pg_proc WHERE proname='verify_audit_chain'"));
    }

    [Theory(DisplayName = "Altered entry (including column edited, canonical untouched)")]
    [InlineData("UPDATE audit_entry SET action='tampered' WHERE company_seq=2", 2, "canonical_mismatch")]
    [InlineData("UPDATE audit_entry SET hash=decode(repeat('ff',32),'hex') WHERE company_seq=2", 2, "hash_mismatch")]
    [InlineData("UPDATE audit_entry SET canonical='invalid-json' WHERE company_seq=2", 2, "canonical_invalid")]
    [InlineData("DELETE FROM audit_entry WHERE company_seq=2", 2, "sequence_gap")]
    [InlineData("DELETE FROM audit_entry WHERE company_seq=3", 3, "head_mismatch")]
    public async Task Tampering_reports_first_inconsistent_sequence(string mutation, long position, string reason)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        await using var connection = await fixture.Database.OpenOwnerAsync();
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(connection, tx, company);
        for (var i = 0; i < 3; i++) await AuditDatabaseTests.AppendAsync(connection, tx, company, user.Id);
        // Test-only tampering is rolled back, including the disabled trigger and every row.
        await connection.ExecuteAsync(new CommandDefinition("ALTER TABLE audit_entry DISABLE TRIGGER audit_entry_immutable", transaction: tx, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(mutation + " AND company_id=@company", new { company }, tx, cancellationToken: TestContext.Current.CancellationToken));
        var result = await VerifyAsync(connection, tx, company);
        Assert.False(result.Consistent);
        Assert.Equal(position, result.FirstInconsistentSeq);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task Concurrent_appends_from_many_connections_are_gapless_and_cannot_fork()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var sequences = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await AuditDatabaseTests.SetContextAsync(connection, tx, company);
            var seq = await AuditDatabaseTests.AppendAsync(connection, tx, company, user.Id);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
            return seq;
        }));
        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), sequences.Order());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await using var transaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Verification(true, 24, null, null), await VerifyAsync(owner, transaction, company));
    }

    [Fact]
    public async Task A_locked_company_chain_does_not_block_another_company()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var other = await fixture.SeedCompanyAsync(user.Id);
        await using var a = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await using var b = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await a.OpenAsync(TestContext.Current.CancellationToken);
        await b.OpenAsync(TestContext.Current.CancellationToken);
        await using var txA = await a.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var txB = await b.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(a, txA, company);
        await AuditDatabaseTests.SetContextAsync(b, txB, other);
        await AuditDatabaseTests.AppendAsync(a, txA, company, user.Id);
        Assert.Equal(1, await AuditDatabaseTests.AppendAsync(b, txB, other, user.Id).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(new Verification(true, 1, null, null), await VerifyAsync(b, txB, other));
        Assert.Equal(new Verification(true, 1, null, null), await VerifyAsync(a, txA, company));
    }

    [Fact]
    public async Task Fixed_vector_freezes_canonical_UTF8_and_SHA256_with_binary_previous_hash()
    {
        const string canonical = """
            ["00000000-0000-0000-0000-000000000001", "1", "1767225600123456", "user", "00000000-0000-0000-0000-000000000002", "synthetic.vector", [{"id": "00000000-0000-0000-0000-000000000003", "kind": "cfdi"}], {"new": {"n": 1.20}, "old": null}]
            """;
        var actual = await fixture.Database.ScalarAsync<string>("""
            SELECT audit_canonical('00000000-0000-0000-0000-000000000001',1,'2026-01-01T00:00:00.123456Z','user',
              '00000000-0000-0000-0000-000000000002','synthetic.vector',
              '[{"kind":"cfdi","id":"00000000-0000-0000-0000-000000000003"}]','{"old":null,"new":{"n":1.20}}')
            """);
        Assert.Equal(canonical, actual);
        Assert.Equal("6fe438cb1966bbf22f4db35bcdc14ff8a6de0c89c4b977e3edcce87ea301b2dd", await fixture.Database.ScalarAsync<string>(
            "SELECT encode(sha256(convert_to(@actual,'UTF8') || decode(repeat('00',32),'hex')),'hex')", new { actual }));
        // The verifier is required even when the canonical writer already matches the vector.
        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM pg_proc WHERE proname='verify_audit_chain')"));
    }

    [Theory]
    [InlineData("user", false, "{}", false)]
    [InlineData("system", true, "{\"source\":\"synthetic\"}", false)]
    [InlineData("system", false, "{}", false)]
    [InlineData("system", false, "{\"source\":\"synthetic-worker\"}", true)]
    public async Task Actors_require_a_user_id_or_the_literal_system_with_source(string type, bool hasActor, string detail, bool accepted)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(app, tx, company);
        var command = new CommandDefinition("SELECT canonical FROM append_audit_entry(@company,@type,@actor,'synthetic.system','[]',@detail::jsonb)",
            new { company, type, actor = hasActor ? (Guid?)user.Id : null, detail }, tx, cancellationToken: TestContext.Current.CancellationToken);
        if (!accepted) await Assert.ThrowsAsync<PostgresException>(() => app.QuerySingleAsync<string>(command));
        else
        {
            Assert.Contains("\"system\"", await app.QuerySingleAsync<string>(command), StringComparison.Ordinal);
            Assert.Equal(new Verification(true, 1, null, null), await VerifyAsync(app, tx, company));
        }
    }
}
