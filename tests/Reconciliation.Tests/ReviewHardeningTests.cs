using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Tenancy;
using Reconciliation.Migrator;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class ReviewHardeningTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("a%00.xml")]
    [InlineData("lone%ED%A0%80.xml")]
    [InlineData(null)]
    public async Task Hostile_filename_is_sanitized_and_does_not_lose_an_earlier_file(string? encodedName)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        using var content = IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice());
        var hostile = new ByteArrayContent(SyntheticCfdi.Invoice());
        encodedName ??= new string('a', 300) + ".xml";
        hostile.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"files\"; filename*=UTF-8''" + encodedName);
        content.Add(hostile);
        using var response = await client.PostAsync($"/companies/{company}/imports/cfdi", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ImportReport>(TestContext.Current.CancellationToken);
        Assert.Equal(2, report!.Files.Count);
        Assert.All(report.Files, file => Assert.Equal("imported", file.Status));
        Assert.DoesNotContain(report.Files[1].Name, char.IsControl);
        Assert.InRange(report.Files[1].Name.Length, 1, 255);
    }

    [Fact]
    public async Task Mid_upload_database_failure_returns_upload_id_for_already_committed_files()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("""
            CREATE FUNCTION reject_review_file() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic fault'; END $$;
            CREATE TRIGGER reject_review_file BEFORE INSERT ON import_file FOR EACH ROW
              WHEN (NEW.filename = 'synthetic-1.xml') EXECUTE FUNCTION reject_review_file();
            """, cancellationToken: TestContext.Current.CancellationToken));
        try
        {
            using var body = IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice(), SyntheticCfdi.Invoice());
            using var response = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var uploadId = error.GetProperty("uploadId").GetGuid();
            using var report = await client.GetAsync($"/companies/{company}/imports/{uploadId}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
            var stored = await report.Content.ReadFromJsonAsync<ImportReport>(TestContext.Current.CancellationToken);
            Assert.Single(stored!.Files);
            Assert.DoesNotContain("PostgresException", error.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await owner.ExecuteAsync(new CommandDefinition("DROP TRIGGER reject_review_file ON import_file; DROP FUNCTION reject_review_file()", cancellationToken: CancellationToken.None));
        }
    }

    [Fact]
    public async Task XML_attributes_count_toward_complexity_and_only_reject_the_hostile_file()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var attributes = string.Join(' ', Enumerable.Range(0, 2000).Select(index => $"a{index}=\"x\""));
        var concepts = string.Concat(Enumerable.Repeat($"<node {attributes}/>", 1000));
        var valid = Encoding.UTF8.GetString(SyntheticCfdi.Invoice());
        var hostile = Encoding.UTF8.GetBytes(valid.Replace("</cfdi:Conceptos>", concepts + "</cfdi:Conceptos>", StringComparison.Ordinal));
        using var app = fixture.App.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ingestion:MaxFileBytes", "33554432");
            builder.UseSetting("Ingestion:MaxUploadBytes", "50331648");
        });
        var result = await app.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("hostile.xml", hostile), UploadedFile.FromBytes("valid.xml", SyntheticCfdi.Invoice())], TestContext.Current.CancellationToken);
        Assert.Equal("rejected", result.Files[0].Status);
        Assert.Contains("node", result.Files[0].Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("imported", result.Files[1].Status);
    }

    [Fact]
    public async Task Archived_company_allows_reads_but_refuses_all_data_changes()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        using var archived = await client.PostAsync($"/companies/{company}/archive", null, TestContext.Current.CancellationToken);
        archived.EnsureSuccessStatusCode();
        using var settings = await client.PatchAsJsonAsync($"/companies/{company}/settings", new { dateToleranceDays = 5 }, TestContext.Current.CancellationToken);
        using var member = await client.PostAsJsonAsync($"/companies/{company}/members", new { userId = user.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        using var body = IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice());
        using var imported = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, settings.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, member.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, imported.StatusCode);
        using var read = await client.GetAsync($"/companies/{company}/settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Concurrent_admin_demotions_cannot_remove_the_last_administrator()
    {
        var a = await fixture.SeedUserAsync();
        var b = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(a.Id);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("INSERT INTO membership(company_id,user_id,role) VALUES (@company,@Id,'administrator')", new { company, b.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var clientA = await fixture.LoginAsync(a.Email, company);
        using var clientB = await fixture.LoginAsync(b.Email, company);
        var results = await Task.WhenAll(clientA.PostAsJsonAsync($"/companies/{company}/members", new { userId = a.Id, role = "viewer" }, TestContext.Current.CancellationToken),
            clientB.PostAsJsonAsync($"/companies/{company}/members", new { userId = b.Id, role = "viewer" }, TestContext.Current.CancellationToken));
        try
        {
            Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM membership WHERE company_id=@company AND role='administrator'", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        }
        finally { foreach (var response in results) response.Dispose(); }
    }

    [Fact]
    public async Task Original_downloads_are_attachments_and_cannot_be_content_sniffed()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic.xml", SyntheticCfdi.Invoice())], TestContext.Current.CancellationToken);
        using var client = await fixture.LoginAsync(user.Email, company);
        foreach (var path in new[] { $"/companies/{company}/cfdi/{report.Files[0].CfdiId}/xml", $"/companies/{company}/imports/files/{report.Files[0].FileId}/original" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [Theory]
    [InlineData("Ingestion:MaxStatementLines", "1", "2026-10-10,A,1,0,1\n2026-10-11,B,1,0,2\n")]
    [InlineData("Ingestion:MaxDescriptionLength", "3", "2026-10-10,TOO LONG,1,0,1\n")]
    public async Task Statement_resource_limits_reject_the_whole_file(string setting, string limit, string csv)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var app = fixture.App.WithWebHostBuilder(builder => builder.UseSetting(setting, limit));
        var result = await app.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-1234",
            [UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n" + csv))], TestContext.Current.CancellationToken);
        ImportAssertions.AssertSingleStatus(result, "rejected");
    }

    [Fact]
    public async Task Date_tolerance_is_bounded_in_the_API_and_database()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        using var response = await client.PatchAsJsonAsync($"/companies/{company}/settings", new { dateToleranceDays = 367 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("UPDATE company SET date_tolerance_days=367 WHERE id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public void Public_demo_signing_key_is_rejected_at_startup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:SigningKey"] = "synthetic-local-signing-key-at-least-32-bytes" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTenancyAuthentication(config));
    }

    [Fact]
    public async Task Unknown_and_known_emails_use_one_verification_and_no_per_request_hash()
    {
        var user = await fixture.SeedUserAsync();
        var hasher = new CountingHasher();
        using var app = fixture.App.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPasswordHasher<string>>();
            services.AddSingleton<IPasswordHasher<string>>(hasher);
        }));
        using var client = app.CreateClient();
        Assert.Equal(1, hasher.HashCalls);
        foreach (var email in new[] { user.Email, "unknown@example.invalid" })
        {
            using var response = await client.PostAsJsonAsync("/auth/tokens", new { email, password = "wrong-synthetic-password" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(1, hasher.HashCalls);
        Assert.Equal(2, hasher.VerificationCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_rate_limit_enforces_email_and_IP_partitions_with_retry_after(bool emailLimit)
    {
        using var app = fixture.App.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LoginRateLimit:EmailPermitLimit", emailLimit ? "2" : "100");
            builder.UseSetting("LoginRateLimit:IpPermitLimit", emailLimit ? "100" : "2");
        });
        using var client = app.CreateClient();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var email = emailLimit ? (attempt == 0 ? "RATE@example.invalid" : "rate@example.invalid") : $"rate{attempt}@example.invalid";
            using var response = await client.PostAsJsonAsync("/auth/tokens", new { email, password = "wrong-synthetic-password" }, TestContext.Current.CancellationToken);
            Assert.Equal(attempt < 2 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (attempt == 2) Assert.NotNull(response.Headers.RetryAfter);
        }
    }

    [Fact]
    public async Task Production_OpenAPI_remains_anonymous()
    {
        using var app = fixture.App.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/openapi.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Existing_operator_bootstrap_logs_that_the_account_was_not_modified()
    {
        var email = $"operator-log-{Guid.NewGuid():N}@example.invalid";
        await OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, email, ApiFixture.Password, TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            await OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, email, ApiFixture.Password, TestContext.Current.CancellationToken);
        }
        finally { Console.SetOut(previous); }
        Assert.Contains("operator already exists, not modified", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class CountingHasher : IPasswordHasher<string>
    {
        private readonly PasswordHasher<string> actual = new();
        public int HashCalls { get; private set; }
        public int VerificationCalls { get; private set; }
        public string HashPassword(string user, string password) { HashCalls++; return actual.HashPassword(user, password); }
        public PasswordVerificationResult VerifyHashedPassword(string user, string hashedPassword, string providedPassword)
        { VerificationCalls++; return actual.VerifyHashedPassword(user, hashedPassword, providedPassword); }
    }
}
