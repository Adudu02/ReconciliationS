using Microsoft.AspNetCore.Diagnostics;
using Reconciliation.Core.Common;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Tenancy;
using SessionOptions = Reconciliation.Core.Tenancy.SessionOptions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReconciliationDatabase(builder.Configuration);

builder.Services.AddTenancyAuthentication(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddIngestion(builder.Configuration);

var app = builder.Build();
_ = app.Services.GetRequiredService<SessionOptions>();
_ = app.Services.GetRequiredService<DummyPasswordHash>();
app.UseExceptionHandler(handler => handler.Run(async http =>
{
    var error = http.Features.Get<IExceptionHandlerFeature>()?.Error;
    var archived = error is CompanyReadOnlyException || error?.InnerException is CompanyReadOnlyException;
    var status = archived ? StatusCodes.Status409Conflict : error is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
    var extensions = new Dictionary<string, object?>();
    if (error is UploadFailedException upload) extensions["uploadId"] = upload.UploadId;
    await Results.Problem(statusCode: status, title: archived ? "Archived company is read-only." : "The request could not be completed.", extensions: extensions).ExecuteAsync(http);
}));
app.UseAuthentication();
app.UseAuthorization();
app.UseLoginProtection();
app.MapAuthentication();
app.MapCompanies();
app.MapCompanyManagement();
app.MapIngestion();
app.MapOpenApi("/openapi.json").AllowAnonymous();
app.MapHealth();
app.Run();

/// <summary>Makes the entry point visible to WebApplicationFactory in tests.</summary>
public partial class Program;
