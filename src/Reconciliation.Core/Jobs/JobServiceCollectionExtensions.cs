using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Core.Jobs;

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(JobOptions.SectionName).Get<JobOptions>() ?? new JobOptions();
        options.Validate();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<JobStore>();
        services.AddSingleton<JobDispatcher>();
        services.AddSingleton<ImportJobService>();
        services.AddSingleton<IJobHandler, CfdiImportJobHandler>();
        services.AddSingleton<IJobHandler, StatementImportJobHandler>();
        services.AddSingleton(new JobBackoff(options, RandomNumberGenerator.GetInt32));
        return services;
    }
}
