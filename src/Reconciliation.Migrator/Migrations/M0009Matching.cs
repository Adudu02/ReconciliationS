using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(9, "Tenant matching links and derived item states")]
public sealed class M0009Matching : Migration
{
    public override void Up() => Execute.Sql($$"""
        ALTER TABLE company ADD CONSTRAINT company_matching_tolerance CHECK(amount_tolerance<=1.00);
        ALTER TABLE cfdi ADD COLUMN serie text, ADD COLUMN folio text;
        CREATE TABLE link (
          company_id uuid NOT NULL REFERENCES company(id), id uuid NOT NULL DEFAULT gen_random_uuid(),
          movement_id uuid NOT NULL, cfdi_id uuid NOT NULL, payment_cfdi_id uuid, payment_ref uuid,
          amount_applied numeric(18,2) NOT NULL CHECK(amount_applied>0),
          state text NOT NULL CHECK(state IN ('suggested','reconciled','rejected','closed')),
          origin text NOT NULL CHECK(origin IN ('automatic','user')),
          criteria jsonb NOT NULL CHECK(jsonb_typeof(criteria)='array'),
          confidence text CHECK(confidence IN ('high','medium','low')), explanation text NOT NULL CHECK(length(explanation)>0),
          created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
          decided_by uuid REFERENCES app_user(id), decided_at timestamptz, reopened_at timestamptz,
          PRIMARY KEY(company_id,id),
          FOREIGN KEY(company_id,movement_id) REFERENCES bank_movement(company_id,id),
          FOREIGN KEY(company_id,cfdi_id) REFERENCES cfdi(company_id,id),
          FOREIGN KEY(company_id,payment_cfdi_id) REFERENCES cfdi(company_id,id),
          FOREIGN KEY(company_id,payment_ref) REFERENCES cfdi_payment(company_id,id),
          CHECK(payment_ref IS NULL OR payment_cfdi_id IS NOT NULL),
          CHECK((decided_by IS NULL)=(decided_at IS NULL)),
          UNIQUE NULLS NOT DISTINCT(company_id,movement_id,cfdi_id,payment_cfdi_id)
        );
        CREATE INDEX link_invoice_idx ON link(company_id,cfdi_id,state);
        ALTER TABLE link ENABLE ROW LEVEL SECURITY;
        ALTER TABLE link FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON link USING(company_id={{TenantSql.CompanyId}}) WITH CHECK(company_id={{TenantSql.CompanyId}});
        GRANT SELECT,INSERT,UPDATE ON link TO cfdi_app;
        CREATE FUNCTION cfdi_state() RETURNS TABLE(company_id uuid,id uuid,state text,reconciled_amount numeric,
          suggested_amount numeric,pending_amount numeric,uncovered_amount numeric,paid_percentage numeric)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          WITH applied AS (
            SELECT company_id,cfdi_id,
              coalesce(sum(amount_applied) FILTER(WHERE state='reconciled'),0) AS paid,
              coalesce(sum(amount_applied) FILTER(WHERE state='suggested'),0) AS suggested
            FROM public.link GROUP BY company_id,cfdi_id
          )
          SELECT c.company_id,c.id,
            CASE WHEN c.total>0 AND coalesce(a.paid,0)>=c.total THEN 'reconciled'
              WHEN coalesce(a.suggested,0)>0 THEN 'suggested' ELSE 'pending' END,
            coalesce(a.paid,0),coalesce(a.suggested,0),greatest(c.total-coalesce(a.paid,0),0),
            greatest(c.total-coalesce(a.paid,0)-coalesce(a.suggested,0),0),
            CASE WHEN c.total>0 THEN coalesce(a.paid,0)*100/c.total ELSE 0 END
          FROM public.cfdi c LEFT JOIN applied a ON a.company_id=c.company_id AND a.cfdi_id=c.id
          WHERE c.type='I'
        $$;
        CREATE FUNCTION movement_state() RETURNS TABLE(company_id uuid,id uuid,state text,reconciled_amount numeric,
          suggested_amount numeric,pending_amount numeric,uncovered_amount numeric,paid_percentage numeric)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          WITH applied AS (
            SELECT company_id,movement_id,
              coalesce(sum(amount_applied) FILTER(WHERE state='reconciled'),0) AS paid,
              coalesce(sum(amount_applied) FILTER(WHERE state='suggested'),0) AS suggested
            FROM public.link GROUP BY company_id,movement_id
          )
          SELECT m.company_id,m.id,
            CASE WHEN coalesce(a.paid,0)>=m.credit+m.debit THEN 'reconciled'
              WHEN coalesce(a.suggested,0)>0 THEN 'suggested' ELSE 'pending' END,
            coalesce(a.paid,0),coalesce(a.suggested,0),greatest(m.credit+m.debit-coalesce(a.paid,0),0),
            greatest(m.credit+m.debit-coalesce(a.paid,0)-coalesce(a.suggested,0),0),
            coalesce(a.paid,0)*100/(m.credit+m.debit)
          FROM public.bank_movement m LEFT JOIN applied a ON a.company_id=m.company_id AND a.movement_id=m.id
        $$;
        REVOKE ALL ON FUNCTION cfdi_state(),movement_state() FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION cfdi_state(),movement_state() TO cfdi_app;
        """);

    public override void Down() => Execute.Sql("""
        DROP FUNCTION movement_state();
        DROP FUNCTION cfdi_state();
        DROP TABLE link;
        ALTER TABLE company DROP CONSTRAINT company_matching_tolerance;
        ALTER TABLE cfdi DROP COLUMN serie, DROP COLUMN folio;
        """);
}
