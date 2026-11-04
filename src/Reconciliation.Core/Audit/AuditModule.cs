using Reconciliation.Core.Common;

namespace Reconciliation.Core.Audit;

/// <summary>Boundary of the "Audit" module: append-only history, verification and company-scoped reads.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "audit";
}
