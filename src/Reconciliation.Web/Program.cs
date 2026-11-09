using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Reconciliation.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<Reconciliation.Web.Localization.Localizer>();
builder.Services.AddScoped<Reconciliation.Web.Localization.Labels>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<Reconciliation.Web.ViewModels.JobProgressViewModel>();
builder.Services.AddTransient<Reconciliation.Web.ViewModels.ImportViewModel>();
builder.Services.AddTransient<Reconciliation.Web.ViewModels.MovementsViewModel>();
builder.Services.AddTransient<Reconciliation.Web.ViewModels.WorkspaceViewModel>();
builder.Services.AddTransient<Reconciliation.Web.ViewModels.ReviewQueueViewModel>();
builder.Services.AddTransient<Reconciliation.Web.ViewModels.InvoiceViewModel>();
await builder.Build().RunAsync();
