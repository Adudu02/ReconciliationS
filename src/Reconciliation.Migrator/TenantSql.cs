namespace Reconciliation.Migrator;

internal static class TenantSql
{
    public const string CompanyId = "nullif(current_setting('app.company_id', true), '')::uuid";
    public static string InDynamicSql => CompanyId.Replace("'", "''", StringComparison.Ordinal);
}
