using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CfdiOriginalsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Same_file_uploaded_twice_keeps_one_invoice_and_original_available_byte_for_byte()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var importer = fixture.App.Services.GetRequiredService<CfdiImporter>();
        var bytes = SyntheticCfdi.Invoice();
        var first = Assert.Single((await importer.ImportAsync(company, user.Id, [UploadedFile.FromBytes("synthetic.xml", bytes)], TestContext.Current.CancellationToken)).Files);
        var second = Assert.Single((await importer.ImportAsync(company, user.Id, [UploadedFile.FromBytes("same.xml", bytes)], TestContext.Current.CancellationToken)).Files);
        Assert.Equal("imported", first.Status);
        Assert.Equal("already_imported", second.Status);
        Assert.Equal(first.CfdiId, second.CfdiId);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM cfdi WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        var original = await owner.QuerySingleAsync<(byte[] Bytes, byte[] Hash, bool Retained)>(new CommandDefinition("SELECT original, sha256, retained_until >= created_at + interval '10 years' FROM import_file WHERE id = @FileId", new { first.FileId }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(bytes, original.Bytes);
        Assert.Equal(SHA256.HashData(bytes), original.Hash);
        Assert.True(original.Retained);
        using var source = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        await using var app = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await app.ExecuteAsync(new CommandDefinition("SELECT set_config('app.company_id', @company, true)", new { company = company.ToString() }, transaction, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(new CommandDefinition("UPDATE import_file SET original = '\x01', sha256 = sha256('\x01'::bytea) WHERE id = @FileId", new { first.FileId }, transaction, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Mixed_upload_reports_imported_duplicate_and_rejected_independently()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var importer = fixture.App.Services.GetRequiredService<CfdiImporter>();
        var existing = UploadedFile.FromBytes("existing.xml", SyntheticCfdi.Invoice());
        await importer.ImportAsync(company, user.Id, [existing], TestContext.Current.CancellationToken);
        var report = await importer.ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("new.xml", SyntheticCfdi.Invoice()), existing, UploadedFile.FromBytes("invalid.xml", Encoding.UTF8.GetBytes("<invalid/>"))], TestContext.Current.CancellationToken);
        Assert.Equal(["imported", "already_imported", "rejected"], report.Files.Select(file => file.Status));
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(2, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM cfdi WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(3, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM import_file WHERE company_id = @company AND upload_id = @UploadId", new { company, report.UploadId }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Same_UUID_in_two_companies_and_concurrent_duplicates_are_isolated_and_idempotent()
    {
        var (user, a) = await fixture.SeedCompanyWithAdminAsync();
        var b = await fixture.SeedCompanyAsync(user.Id);
        var importer = fixture.App.Services.GetRequiredService<CfdiImporter>();
        var file = UploadedFile.FromBytes("same.xml", SyntheticCfdi.Invoice());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => importer.ImportAsync(a, user.Id, [file], TestContext.Current.CancellationToken)));
        Assert.Equal(1, attempts.Count(report => report.Files.Single().Status == "imported"));
        Assert.Equal(2, attempts.Count(report => report.Files.Single().Status == "already_imported"));
        Assert.Equal("imported", Assert.Single((await importer.ImportAsync(b, user.Id, [file], TestContext.Current.CancellationToken)).Files).Status);
    }
}
