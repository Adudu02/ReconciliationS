using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Reconciliation.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddTransient<Reconciliation.Web.ViewModels.WorkspaceViewModel>();
await builder.Build().RunAsync();
