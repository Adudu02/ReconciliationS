using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class JobsHttpTests(ApiFixture fixture)
{
    [Fact(DisplayName = "Exhausted job")]
    public async Task Failed_history_is_visible_to_admin_and_only_admin_can_retry_with_audit()
    {
        var seeded = await fixture.SeedCompanyWithAdminAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var jobId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO job(company_id,type,state,payload,attempts,max_attempts,last_error,created_by)
            VALUES(@CompanyId,'status_check','failed','{"synthetic":true}',1,1,'Synthetic timeout',@Id) RETURNING id
            """, new { seeded.CompanyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job_attempt(company_id,job_id,attempt_no,started_at,finished_at,outcome,error_kind,error)
            VALUES(@CompanyId,@jobId,1,now(),now(),'failed','transient','Synthetic timeout')
            """, new { seeded.CompanyId, jobId }, cancellationToken: TestContext.Current.CancellationToken));
        using var admin = await fixture.LoginAsync(seeded.User.Email, seeded.CompanyId);
        using var failed = await admin.GetAsync($"/companies/{seeded.CompanyId}/jobs?status=failed", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var list = await failed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var job = Assert.Single(list.EnumerateArray());
        Assert.Equal(jobId, job.GetProperty("id").GetGuid());
        Assert.Equal("Synthetic timeout", job.GetProperty("lastError").GetString());
        Assert.Equal("transient", job.GetProperty("history")[0].GetProperty("errorKind").GetString());
        Assert.Contains("synthetic", job.GetProperty("payload").GetString()!, StringComparison.Ordinal);
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role='accountant' WHERE company_id=@CompanyId AND user_id=@Id", new { seeded.CompanyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var refused = await admin.PostAsync($"/companies/{seeded.CompanyId}/jobs/{jobId}/retry", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role='viewer' WHERE company_id=@CompanyId AND user_id=@Id", new { seeded.CompanyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var read = await admin.GetAsync($"/companies/{seeded.CompanyId}/jobs/{jobId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var deniedList = await admin.GetAsync($"/companies/{seeded.CompanyId}/jobs?status=failed", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role='administrator' WHERE company_id=@CompanyId AND user_id=@Id", new { seeded.CompanyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var retried = await admin.PostAsync($"/companies/{seeded.CompanyId}/jobs/{jobId}/retry", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.Equal("queued", await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT state FROM job WHERE id=@jobId", new { jobId }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM job_attempt WHERE job_id=@jobId", new { jobId }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM audit_entry WHERE company_id=@CompanyId AND action='job.retried' AND new_values->>'jobId'=@job AND items @> jsonb_build_array(jsonb_build_object('kind','job','id',@job))", new { seeded.CompanyId, job = jobId.ToString() }, cancellationToken: TestContext.Current.CancellationToken)));
        using var repeated = await admin.PostAsync($"/companies/{seeded.CompanyId}/jobs/{jobId}/retry", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
    }

    [Fact]
    public async Task Archived_company_jobs_are_readable_but_retry_is_refused()
    {
        var seeded = await fixture.SeedCompanyWithAdminAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var jobId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO job(company_id,type,state,created_by) VALUES(@CompanyId,'status_check','failed',@Id) RETURNING id", new { seeded.CompanyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var admin = await fixture.LoginAsync(seeded.User.Email, seeded.CompanyId);
        using var archive = await admin.PostAsync($"/companies/{seeded.CompanyId}/archive", null, TestContext.Current.CancellationToken);
        Assert.True(archive.IsSuccessStatusCode);
        using var read = await admin.GetAsync($"/companies/{seeded.CompanyId}/jobs/{jobId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var denied = await admin.PostAsync($"/companies/{seeded.CompanyId}/jobs/{jobId}/retry", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }
}
