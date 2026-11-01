using Reconciliation.Core.Common;

namespace Reconciliation.Core.Jobs;

/// <summary>Boundary of the "Jobs" module. Filled in by later tasks.</summary>
// Planned module: implemented by its corresponding OpenSpec group.
public sealed class JobsModule : IModule
{
    public string Name => "jobs";
}
