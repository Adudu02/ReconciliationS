namespace Reconciliation.Core.Common;

/// <summary>Boundary marker of one module (design D1). Modules talk through interfaces, never through each other's tables.</summary>
public interface IModule
{
    string Name { get; }
}
