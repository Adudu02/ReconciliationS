using Reconciliation.Core.Common;
using Reconciliation.Core.Tenancy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReconciliationDatabase(builder.Configuration);

builder.Services.AddTenancyAuthentication(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthentication();
app.MapCompanies();
app.MapOpenApi("/openapi.json").AllowAnonymous();
app.MapHealth();
app.Run();

/// <summary>Makes the entry point visible to WebApplicationFactory in tests.</summary>
public partial class Program;
