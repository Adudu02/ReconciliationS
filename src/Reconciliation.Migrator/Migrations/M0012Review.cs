using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(12, "Review decisions and company read indexes")]
public sealed class M0012Review : Migration
{
    public override void Up() => Execute.Sql($$"""
        ALTER TABLE link DROP CONSTRAINT link_company_id_movement_id_cfdi_id_payment_cfdi_id_key;
        CREATE UNIQUE INDEX link_active_pair_idx ON link(company_id,movement_id,cfdi_id,payment_cfdi_id) NULLS NOT DISTINCT WHERE state IN ('suggested','reconciled');
        CREATE TABLE link_decision (
          company_id uuid NOT NULL REFERENCES company(id),id uuid NOT NULL DEFAULT gen_random_uuid(),
          link_id uuid NOT NULL,type text NOT NULL CHECK(type IN ('confirm','reject','change')),
          decided_by uuid NOT NULL REFERENCES app_user(id),decided_at timestamptz NOT NULL DEFAULT clock_timestamp(),
          detail jsonb NOT NULL CHECK(jsonb_typeof(detail)='object'),PRIMARY KEY(company_id,id),
          FOREIGN KEY(company_id,link_id) REFERENCES link(company_id,id)
        );
        CREATE INDEX link_decision_link_idx ON link_decision(company_id,link_id,decided_at,id);
        ALTER TABLE link_decision ENABLE ROW LEVEL SECURITY;
        ALTER TABLE link_decision FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON link_decision USING(company_id={{TenantSql.CompanyId}}) WITH CHECK(company_id={{TenantSql.CompanyId}});
        GRANT SELECT,INSERT ON link_decision TO cfdi_app;
        CREATE INDEX link_review_queue_idx ON link(company_id,state,confidence,created_at,id);
        CREATE INDEX cfdi_list_idx ON cfdi(company_id,type,direction,issued_at,id);
        """);

    public override void Down() => Execute.Sql("""
        DO $$ BEGIN
          IF EXISTS(SELECT FROM link_decision) THEN
            RAISE EXCEPTION 'M0012 rollback refused: user decisions require explicit manual export/cleanup';
          END IF;
          IF EXISTS(SELECT FROM link GROUP BY company_id,movement_id,cfdi_id,payment_cfdi_id HAVING count(*)>1) THEN
            RAISE EXCEPTION 'M0012 rollback refused: historical duplicate link pairs must be preserved';
          END IF;
        END $$;
        DROP INDEX link_active_pair_idx;
        ALTER TABLE link ADD CONSTRAINT link_company_id_movement_id_cfdi_id_payment_cfdi_id_key
          UNIQUE NULLS NOT DISTINCT(company_id,movement_id,cfdi_id,payment_cfdi_id);
        DROP INDEX cfdi_list_idx,link_review_queue_idx;
        DROP TABLE link_decision;
        """);
}
