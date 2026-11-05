using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(10, "Matching payable and derived review information")]
public sealed class M0010MatchingRest : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE INDEX cfdi_relation_related_idx ON cfdi_relation(company_id,related_uuid,relation_code,cfdi_id);
        CREATE FUNCTION cfdi_payable() RETURNS TABLE(company_id uuid,id uuid,credit_notes numeric,payable numeric)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          WITH notes AS (
            SELECT invoice.company_id,invoice.id,sum(note.total) AS credited
            FROM public.cfdi_relation r
            JOIN public.cfdi note ON note.company_id=r.company_id AND note.id=r.cfdi_id
            JOIN public.cfdi invoice ON invoice.company_id=r.company_id AND invoice.uuid=r.related_uuid
            WHERE r.relation_code='01' AND note.type='E' AND note.direction='issued' AND invoice.direction='issued'
              AND note.currency=invoice.currency AND note.sat_status<>'cancelled'
            GROUP BY invoice.company_id,invoice.id
          )
          SELECT c.company_id,c.id,coalesce(n.credited,0),greatest(c.total-coalesce(n.credited,0),0)
          FROM public.cfdi c LEFT JOIN notes n ON n.company_id=c.company_id AND n.id=c.id WHERE c.type='I'
        $$;
        CREATE OR REPLACE FUNCTION cfdi_state() RETURNS TABLE(company_id uuid,id uuid,state text,reconciled_amount numeric,
          suggested_amount numeric,pending_amount numeric,uncovered_amount numeric,paid_percentage numeric)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          WITH applied AS (
            SELECT company_id,cfdi_id,
              coalesce(sum(amount_applied) FILTER(WHERE state='reconciled'),0) AS paid,
              coalesce(sum(amount_applied) FILTER(WHERE state='suggested'),0) AS suggested
            FROM public.link GROUP BY company_id,cfdi_id
          )
          SELECT p.company_id,p.id,
            CASE WHEN p.payable>0 AND coalesce(a.paid,0)>=p.payable THEN 'reconciled'
              WHEN coalesce(a.suggested,0)>0 THEN 'suggested' ELSE 'pending' END,
            coalesce(a.paid,0),coalesce(a.suggested,0),greatest(p.payable-coalesce(a.paid,0),0),
            greatest(p.payable-coalesce(a.paid,0)-coalesce(a.suggested,0),0),
            CASE WHEN p.payable>0 THEN coalesce(a.paid,0)*100/p.payable ELSE 0 END
          FROM public.cfdi_payable() p LEFT JOIN applied a ON a.company_id=p.company_id AND a.cfdi_id=p.id
        $$;
        CREATE FUNCTION cfdi_review_reasons(p_today date)
        RETURNS TABLE(company_id uuid,id uuid,reasons jsonb,requires_review boolean)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          SELECT p.company_id,p.id,
            CASE WHEN p.credit_notes>0 AND p.payable=0 THEN jsonb_build_array(jsonb_build_object('code','fully credited','requiresReview',false)) ELSE '[]'::jsonb END
            || CASE WHEN c.is_global OR upper(c.receiver_rfc)='XAXX010101000' THEN jsonb_build_array(jsonb_build_object('code','global invoice','requiresReview',false)) ELSE '[]'::jsonb END,
            false
          FROM public.cfdi_payable() p JOIN public.cfdi c ON c.company_id=p.company_id AND c.id=p.id
        $$;
        REVOKE ALL ON FUNCTION cfdi_payable(),cfdi_review_reasons(date) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION cfdi_payable(),cfdi_review_reasons(date) TO cfdi_app;
        """);

    public override void Down() => Execute.Sql("""
        DROP FUNCTION cfdi_review_reasons(date);
        CREATE OR REPLACE FUNCTION cfdi_state() RETURNS TABLE(company_id uuid,id uuid,state text,reconciled_amount numeric,
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
          FROM public.cfdi c LEFT JOIN applied a ON a.company_id=c.company_id AND a.cfdi_id=c.id WHERE c.type='I'
        $$;
        DROP FUNCTION cfdi_payable();
        DROP INDEX cfdi_relation_related_idx;
        """);
}
