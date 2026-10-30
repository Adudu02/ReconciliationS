using Reconciliation.Core.Common;

namespace Reconciliation.Core.Audit;

/// <summary>Boundary of the "Audit" module. Filled in by later tasks.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "audit";
}
