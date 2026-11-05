using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(11, "Tenant SAT status cache and answered response provenance")]
public sealed class M0011SatStatusCache : Migration
{
    public override void Up() => Execute.Sql($$"""
        CREATE TABLE sat_status_cache (
          company_id uuid NOT NULL REFERENCES company(id),uuid uuid NOT NULL,
          state text NOT NULL CHECK(state IN ('current','cancelled','not_found','unknown')),
          cancellability text,cancellation_status text,efos_raw text,
          checked_at timestamptz NOT NULL,expires_at timestamptz,last_error text,
          attempts integer NOT NULL CHECK(attempts>=0),source text NOT NULL CHECK(source IN ('backfill','stub','real','replay')),
          code_raw text,state_raw text,cancellability_raw text,cancellation_status_raw text,
          PRIMARY KEY(company_id,uuid),FOREIGN KEY(company_id,uuid) REFERENCES cfdi(company_id,uuid),
          CHECK((state='cancelled')=(expires_at IS NULL))
        );
        CREATE INDEX sat_status_cache_expiry_idx ON sat_status_cache(company_id,expires_at) WHERE expires_at IS NOT NULL;
        ALTER TABLE sat_status_cache ENABLE ROW LEVEL SECURITY;
        ALTER TABLE sat_status_cache FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON sat_status_cache USING(company_id={{TenantSql.CompanyId}}) WITH CHECK(company_id={{TenantSql.CompanyId}});
        GRANT SELECT,INSERT,UPDATE ON sat_status_cache TO cfdi_app;
        INSERT INTO sat_status_cache(company_id,uuid,state,cancellation_status,checked_at,expires_at,attempts,source)
        SELECT company_id,uuid,'cancelled',cancellation_status,clock_timestamp(),NULL,0,'backfill' FROM cfdi WHERE sat_status='cancelled';
        """);

    public override void Down() => Execute.Sql("DROP TABLE sat_status_cache;");
}
