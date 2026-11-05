using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Reconciliation.Core.Sat;

public static class SatServiceCollectionExtensions
{
    public static IServiceCollection AddSat(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Sat").Get<SatOptions>() ?? new();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddHttpClient("sat", client => client.Timeout = options.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<ISatClient>(provider => new SoapSatClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("sat"),
            options.EndpointUri, options.RequestTimeout, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRequestRateGate>(provider => new RequestRateGate(provider.GetRequiredService<TimeProvider>(), options.MaxRequestsPerSecond));
        services.AddSingleton(provider => new SatStatusLookup(provider.GetRequiredService<ISatClient>(), provider.GetRequiredService<IRequestRateGate>(),
            options, provider.GetRequiredService<TimeProvider>(), RandomNumberGenerator.GetInt32));
        return services;
    }
}
