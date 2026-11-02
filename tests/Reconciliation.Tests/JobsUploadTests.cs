using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class JobsUploadTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact(DisplayName = "Start an import")]
    public async Task Upload_returns_before_parsing_and_job_progress_is_visible_without_exposing_pending_input()
    {
        var seeded = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(seeded.User.Email, seeded.CompanyId);
        using var body = IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice());
        using var response = await client.PostAsync($"/companies/{seeded.CompanyId}/imports/cfdi", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var jobId = receipt.GetProperty("jobId").GetGuid();
        var uploadId = receipt.GetProperty("uploadId").GetGuid();
        var queued = await client.GetFromJsonAsync<JsonElement>($"/companies/{seeded.CompanyId}/jobs/{jobId}", TestContext.Current.CancellationToken);
        Assert.Equal("queued", queued.GetProperty("state").GetString());
        Assert.Equal(0, queued.GetProperty("progressDone").GetInt32());
        var report = await client.GetFromJsonAsync<JsonElement>($"/companies/{seeded.CompanyId}/imports/{uploadId}", TestContext.Current.CancellationToken);
        Assert.Equal("queued", report.GetProperty("files")[0].GetProperty("status").GetString());
        Assert.DoesNotContain("pending", report.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending", queued.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var fileId = report.GetProperty("files")[0].GetProperty("fileId").GetGuid();
        using var original = await client.GetAsync($"/companies/{seeded.CompanyId}/imports/files/{fileId}/original", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
        var store = fixture.App.Services.GetRequiredService<JobStore>();
        var job = (await store.ClaimAsync("synthetic-visible", 60, TestContext.Current.CancellationToken))!;
        Assert.Equal(jobId, job.Id);
        var running = await client.GetFromJsonAsync<JsonElement>($"/companies/{seeded.CompanyId}/jobs/{jobId}", TestContext.Current.CancellationToken);
        Assert.Equal("running", running.GetProperty("state").GetString());
        var handler = fixture.App.Services.GetServices<IJobHandler>().Single(handler => handler.Type == job.Type);
        await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteAsync(job.CompanyId, (connection, transaction, cancellationToken) => handler.HandleAsync(job, connection, transaction, cancellationToken), TestContext.Current.CancellationToken);
        Assert.True(await store.FinishAsync(job, "synthetic-visible", null, null, TimeSpan.Zero, TestContext.Current.CancellationToken));
        var succeeded = await client.GetFromJsonAsync<JsonElement>($"/companies/{seeded.CompanyId}/jobs/{jobId}", TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", succeeded.GetProperty("state").GetString());
        Assert.Equal(1, succeeded.GetProperty("progressDone").GetInt32());
        using var repeatedBody = IngestionHttpTests.CfdiBody(await originalBytesAsync());
        using var repeated = await client.PostAsync($"/companies/{seeded.CompanyId}/imports/cfdi", repeatedBody, TestContext.Current.CancellationToken);
        var duplicateReport = await fixture.CompleteImportAsync(client, repeated);
        Assert.Equal("already_imported", duplicateReport.GetProperty("files")[0].GetProperty("status").GetString());
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM cfdi WHERE company_id=@CompanyId", new { seeded.CompanyId }));

        async Task<byte[]> originalBytesAsync()
        {
            using var acceptedOriginal = await client.GetAsync($"/companies/{seeded.CompanyId}/imports/files/{fileId}/original", TestContext.Current.CancellationToken);
            acceptedOriginal.EnsureSuccessStatusCode();
            return await acceptedOriginal.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
    }
}
