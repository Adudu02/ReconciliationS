using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class IngestionHttpTests(ApiFixture fixture)
{
    internal static MultipartFormDataContent CfdiBody(params byte[][] files)
    {
        var content = new MultipartFormDataContent();
        for (var index = 0; index < files.Length; index++) content.Add(new ByteArrayContent(files[index]), "files", $"synthetic-{index}.xml");
        return content;
    }

    [Fact]
    public async Task Accountant_imports_and_viewer_reads_invoice_report_and_original()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "accountant");
        using var client = await fixture.LoginAsync(user.Email, company);
        var bytes = SyntheticCfdi.Invoice();
        using var body = CfdiBody(bytes);
        using var accepted = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var report = await fixture.CompleteImportAsync(client, accepted);
        var uploadId = report.GetProperty("uploadId").GetGuid();
        var file = report.GetProperty("files")[0];
        var invoice = file.GetProperty("cfdiId").GetGuid();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role = 'viewer' WHERE company_id = @company AND user_id = @Id", new { company, user.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var read = await client.GetAsync($"/companies/{company}/cfdi/{invoice}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = await read.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1939.27m, json.GetProperty("total").GetDecimal());
        using var persisted = await client.GetAsync($"/companies/{company}/imports/{uploadId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, persisted.StatusCode);
        var stored = await persisted.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("imported", stored.GetProperty("files")[0].GetProperty("status").GetString());
        using var original = await client.GetAsync($"/companies/{company}/cfdi/{invoice}/xml", TestContext.Current.CancellationToken);
        Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        using var forbiddenBody = CfdiBody(SyntheticCfdi.Invoice());
        using var forbidden = await client.PostAsync($"/companies/{company}/imports/cfdi", forbiddenBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Bank_upload_lists_supported_banks_persists_counts_and_returns_statement_bytes()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "accountant");
        using var client = await fixture.LoginAsync(user.Email, company);
        using var banks = await client.GetAsync($"/companies/{company}/banks", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, banks.StatusCode);
        Assert.Contains("reference", await banks.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SYNTHETIC REF:0001,0.10,0,0.10\n");
        using var body = new MultipartFormDataContent
        {
            { new StringContent("reference"), "bank" }, { new StringContent("Synthetic-1234"), "accountReference" },
            { new ByteArrayContent(bytes), "files", "synthetic.csv" }
        };
        using var uploaded = await client.PostAsync($"/companies/{company}/imports/bank-statement", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        var report = await fixture.CompleteImportAsync(client, uploaded);
        var uploadId = report.GetProperty("uploadId").GetGuid();
        var fileId = report.GetProperty("files")[0].GetProperty("fileId").GetGuid();
        using var read = await client.GetAsync($"/companies/{company}/imports/{uploadId}", TestContext.Current.CancellationToken);
        var stored = await read.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, stored.GetProperty("files")[0].GetProperty("importedMovements").GetInt32());
        using var original = await client.GetAsync($"/companies/{company}/imports/files/{fileId}/original", TestContext.Current.CancellationToken);
        Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role = 'viewer' WHERE company_id = @company AND user_id = @Id", new { company, user.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var deniedBody = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", "synthetic.csv" } };
        using var denied = await client.PostAsync($"/companies/{company}/imports/bank-statement", deniedBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Oversized_file_and_hostile_XML_reject_only_those_files_and_keep_hashes_without_bytes()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "accountant");
        var valid = SyntheticCfdi.Invoice();
        using var app = fixture.App.WithWebHostBuilder(builder => builder.UseSetting("Ingestion:MaxFileBytes", valid.Length.ToString(CultureInfo.InvariantCulture)));
        using var client = app.CreateClient();
        using var signedIn = await fixture.LoginAsync(user.Email, company);
        client.DefaultRequestHeaders.Authorization = signedIn.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        using var body = CfdiBody(new byte[valid.Length + 1], Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a SYSTEM 'http://127.0.0.1:1/synthetic'>]><x>&a;</x>"), valid);
        using var response = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var report = await fixture.CompleteImportAsync(client, response, app.Services);
        Assert.Equal(["rejected", "rejected", "imported"], report.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("status").GetString()));
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(2, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM import_file WHERE company_id = @company AND status = 'rejected' AND original IS NULL AND octet_length(sha256) = 32", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Ingestion:MaxFiles", "1", false, HttpStatusCode.BadRequest)]
    [InlineData("Ingestion:MaxUploadBytes", "1024", false, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("Ingestion:MaxUploadBytes", "1024", true, HttpStatusCode.RequestEntityTooLarge)]
    public async Task Aggregate_limits_reject_entire_upload_before_import_even_without_content_length(string setting, string limit, bool streaming, HttpStatusCode expected)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "accountant");
        using var app = fixture.App.WithWebHostBuilder(builder => builder.UseSetting(setting, limit));
        using var client = app.CreateClient();
        using var signedIn = await fixture.LoginAsync(user.Email, company);
        client.DefaultRequestHeaders.Authorization = signedIn.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        using var multipart = CfdiBody(SyntheticCfdi.Invoice(), SyntheticCfdi.Invoice());
        using HttpContent body = streaming ? new UnmeasuredContent(await multipart.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), multipart.Headers.ContentType!.ToString()) : multipart;
        using var response = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM import_file WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    private sealed class UnmeasuredContent : HttpContent
    {
        private readonly byte[] bytes;
        public UnmeasuredContent(byte[] bytes, string contentType)
        {
            this.bytes = bytes;
            Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        private async Task WriteAsync(Stream stream)
        {
            await stream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        }
    }
}
