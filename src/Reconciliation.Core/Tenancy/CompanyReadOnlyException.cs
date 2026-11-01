namespace Reconciliation.Core.Tenancy;

public sealed class CompanyReadOnlyException() : InvalidOperationException("Archived company is read-only.");
