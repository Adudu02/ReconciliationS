using Reconciliation.Core.Common;
using Reconciliation.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReconciliationDatabase(builder.Configuration);
builder.Services.AddHostedService<JobLoop>();

var app = builder.Build();
app.MapHealth();
app.Run();
