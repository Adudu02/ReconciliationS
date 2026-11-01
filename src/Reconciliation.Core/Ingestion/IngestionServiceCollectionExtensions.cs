using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Core.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    public static IServiceCollection AddIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions();
        options.Validate();
        services.AddMemoryCache();
        services.AddSingleton(options);
        services.AddSingleton<SafeCfdiXmlReader>();
        services.AddSingleton<CfdiImporter>();
        services.AddSingleton<IBankStatementParser, ReferenceBankStatementParser>();
        services.AddSingleton<BankStatementImporter>();
        return services;
    }
}
