using Reconciliation.Core.Common;

namespace Reconciliation.Core.Ingestion;

/// <summary>Boundary of the "Ingestion" module. Filled in by later tasks.</summary>
public sealed class IngestionModule : IModule
{
    public string Name => "ingestion";
}
