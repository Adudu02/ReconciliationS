using Reconciliation.Core.Common;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReconciliationDatabase(builder.Configuration);

var app = builder.Build();
app.MapHealth();
app.Run();

/// <summary>Makes the entry point visible to WebApplicationFactory in tests.</summary>
public partial class Program;
