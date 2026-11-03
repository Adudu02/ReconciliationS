using Reconciliation.Core.Common;

namespace Reconciliation.Core.Jobs;

/// <summary>Durable tenant jobs, leases, retries and dispatch.</summary>
public sealed class JobsModule : IModule
{
    public string Name => "jobs";
}
