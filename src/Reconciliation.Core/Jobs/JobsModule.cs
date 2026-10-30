using Reconciliation.Core.Common;

namespace Reconciliation.Core.Jobs;

/// <summary>Boundary of the "Jobs" module. Filled in by later tasks.</summary>
public sealed class JobsModule : IModule
{
    public string Name => "jobs";
}
