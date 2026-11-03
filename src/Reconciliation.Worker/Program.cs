using Reconciliation.Core.Common;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Jobs;
using Reconciliation.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReconciliationDatabase(builder.Configuration);
builder.Services.AddIngestion(builder.Configuration);
builder.Services.AddJobs(builder.Configuration);
builder.Services.AddHostedService<JobLoop>();

var app = builder.Build();
app.MapHealth();
app.Run();
