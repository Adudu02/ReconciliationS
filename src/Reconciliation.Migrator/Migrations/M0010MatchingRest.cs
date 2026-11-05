using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(10, "Matching payable and derived review information")]
public sealed class M0010MatchingRest : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE link ADD COLUMN review_reason text;
        ALTER TABLE cfdi ADD COLUMN cancellation_status text;
        GRANT UPDATE(sat_status,cancellation_status) ON cfdi TO cfdi_app;
        CREATE INDEX cfdi_relation_related_idx ON cfdi_relation(company_id,related_uuid,relation_code,cfdi_id);
        CREATE INDEX link_payment_claim_idx ON link(company_id,payment_ref,created_at,id) WHERE payment_ref IS NOT NULL;
        CREATE INDEX cfdi_payment_doc_coverage_idx ON cfdi_payment_doc(company_id,payment_id,invoice_uuid);
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
              coalesce(sum(amount_applied) FILTER(WHERE state='suggested' AND coalesce(review_reason,'') NOT IN ('cancelled invoice','cancelled payment complement')),0) AS suggested
            FROM public.link GROUP BY company_id,cfdi_id
          )
          SELECT p.company_id,p.id,
            CASE WHEN c.sat_status='cancelled' THEN 'pending' WHEN p.payable>0 AND coalesce(a.paid,0)>=p.payable THEN 'reconciled'
              WHEN coalesce(a.suggested,0)>0 THEN 'suggested' ELSE 'pending' END,
            coalesce(a.paid,0),coalesce(a.suggested,0),CASE WHEN c.sat_status='cancelled' THEN 0 ELSE greatest(p.payable-coalesce(a.paid,0),0) END,
            CASE WHEN c.sat_status='cancelled' THEN 0 ELSE greatest(p.payable-coalesce(a.paid,0)-coalesce(a.suggested,0),0) END,
            CASE WHEN p.payable>0 THEN coalesce(a.paid,0)*100/p.payable ELSE 0 END
          FROM public.cfdi_payable() p JOIN public.cfdi c ON c.company_id=p.company_id AND c.id=p.id LEFT JOIN applied a ON a.company_id=p.company_id AND a.cfdi_id=p.id
        $$;
        CREATE OR REPLACE FUNCTION movement_state() RETURNS TABLE(company_id uuid,id uuid,state text,reconciled_amount numeric,
          suggested_amount numeric,pending_amount numeric,uncovered_amount numeric,paid_percentage numeric)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          WITH applied AS (
            SELECT company_id,movement_id,
              coalesce(sum(amount_applied) FILTER(WHERE state='reconciled'),0) AS paid,
              coalesce(sum(amount_applied) FILTER(WHERE state='suggested' AND coalesce(review_reason,'') NOT IN ('cancelled invoice','cancelled payment complement')),0) AS suggested,
              bool_or(state='suggested' AND review_reason IN ('cancelled invoice','cancelled payment complement')) AS held
            FROM public.link GROUP BY company_id,movement_id
          )
          SELECT m.company_id,m.id,
            CASE WHEN coalesce(a.paid,0)>=m.credit+m.debit THEN 'reconciled'
              WHEN coalesce(a.suggested,0)>0 OR coalesce(a.held,false) THEN 'suggested' ELSE 'pending' END,
            coalesce(a.paid,0),coalesce(a.suggested,0),greatest(m.credit+m.debit-coalesce(a.paid,0),0),
            greatest(m.credit+m.debit-coalesce(a.paid,0)-coalesce(a.suggested,0),0),
            coalesce(a.paid,0)*100/(m.credit+m.debit)
          FROM public.bank_movement m LEFT JOIN applied a ON a.company_id=m.company_id AND a.movement_id=m.id
        $$;
        CREATE FUNCTION cfdi_review_reasons(p_today date)
        RETURNS TABLE(company_id uuid,id uuid,reasons jsonb,requires_review boolean,complement_due_date date,complement_overdue boolean)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          WITH payment_claims AS (
            SELECT DISTINCT ON(company_id,payment_ref) company_id,payment_ref,movement_id
            FROM public.link WHERE state='reconciled' AND payment_ref IS NOT NULL
            ORDER BY company_id,payment_ref,created_at,id
          ), missing_deadlines AS (
            SELECT l.company_id,l.cfdi_id,
              min(f.fifth + CASE WHEN company.complement_next_biz_day AND extract(isodow FROM f.fifth) IN (6,7)
                THEN 8-extract(isodow FROM f.fifth)::integer ELSE 0 END) AS due_date
            FROM public.link l
            JOIN public.bank_movement m ON m.company_id=l.company_id AND m.id=l.movement_id
            JOIN public.cfdi invoice ON invoice.company_id=l.company_id AND invoice.id=l.cfdi_id
            JOIN public.company company ON company.id=l.company_id
            CROSS JOIN LATERAL (SELECT (date_trunc('month',m.movement_date::timestamp)+interval '1 month 4 days')::date AS fifth) f
            WHERE l.state='reconciled' AND invoice.payment_method='PPD' AND invoice.sat_status<>'cancelled'
              AND NOT EXISTS (
                SELECT FROM payment_claims claim
                JOIN public.cfdi_payment payment ON payment.company_id=claim.company_id AND payment.id=claim.payment_ref
                JOIN public.cfdi complement ON complement.company_id=payment.company_id AND complement.id=payment.cfdi_id
                JOIN public.cfdi_payment_doc doc ON doc.company_id=payment.company_id AND doc.payment_id=payment.id
                WHERE claim.company_id=l.company_id AND claim.payment_ref=l.payment_ref AND claim.movement_id=l.movement_id
                  AND payment.cfdi_id=l.payment_cfdi_id AND complement.type='P' AND complement.sat_status<>'cancelled'
                  AND doc.invoice_uuid=invoice.uuid
              )
            GROUP BY l.company_id,l.cfdi_id
          ), payment_flags AS (
            SELECT l.company_id,l.cfdi_id,bool_or(l.review_reason='cancelled payment complement' OR c.sat_status='cancelled') AS cancelled_payment
            FROM public.link l LEFT JOIN public.cfdi c ON c.company_id=l.company_id AND c.id=l.payment_cfdi_id
            WHERE l.state IN ('suggested','reconciled') GROUP BY l.company_id,l.cfdi_id
          ), note_flags AS (
            SELECT invoice.company_id,invoice.id,
              bool_or(note.sat_status='cancelled') AS cancelled_note,
              bool_or(note.currency<>invoice.currency) AS currency_mismatch
            FROM public.cfdi_relation r
            JOIN public.cfdi note ON note.company_id=r.company_id AND note.id=r.cfdi_id
            JOIN public.cfdi invoice ON invoice.company_id=r.company_id AND invoice.uuid=r.related_uuid
            WHERE r.relation_code='01' AND note.type='E' AND note.direction='issued' AND invoice.direction='issued'
            GROUP BY invoice.company_id,invoice.id
          )
          SELECT p.company_id,p.id,
            CASE WHEN p.credit_notes>0 AND p.payable=0 THEN jsonb_build_array(jsonb_build_object('code','fully credited','requiresReview',false)) ELSE '[]'::jsonb END
            || CASE WHEN c.is_global OR upper(c.receiver_rfc)='XAXX010101000' THEN jsonb_build_array(jsonb_build_object('code','global invoice','requiresReview',false)) ELSE '[]'::jsonb END
            || CASE WHEN s.reconciled_amount>p.payable THEN jsonb_build_array(jsonb_build_object('code','overpaid','requiresReview',true)) ELSE '[]'::jsonb END
            || CASE WHEN coalesce(n.cancelled_note,false) THEN jsonb_build_array(jsonb_build_object('code','cancelled credit note','requiresReview',true)) ELSE '[]'::jsonb END
            || CASE WHEN coalesce(n.currency_mismatch,false) THEN jsonb_build_array(jsonb_build_object('code','credit note in a different currency','requiresReview',true)) ELSE '[]'::jsonb END
            || CASE WHEN c.sat_status='cancelled' THEN jsonb_build_array(jsonb_build_object('code','cancelled invoice','requiresReview',true)) ELSE '[]'::jsonb END
            || CASE WHEN coalesce(pf.cancelled_payment,false) THEN jsonb_build_array(jsonb_build_object('code','cancelled payment complement','requiresReview',true)) ELSE '[]'::jsonb END
            || CASE WHEN d.due_date<p_today THEN jsonb_build_array(jsonb_build_object('code','complement overdue','requiresReview',true)) ELSE '[]'::jsonb END,
            s.reconciled_amount>p.payable OR coalesce(n.cancelled_note,false) OR coalesce(n.currency_mismatch,false) OR coalesce(d.due_date<p_today,false) OR c.sat_status='cancelled' OR coalesce(pf.cancelled_payment,false),
            d.due_date,coalesce(d.due_date<p_today,false)
          FROM public.cfdi_payable() p JOIN public.cfdi c ON c.company_id=p.company_id AND c.id=p.id
          JOIN public.cfdi_state() s ON s.company_id=p.company_id AND s.id=p.id
          LEFT JOIN note_flags n ON n.company_id=p.company_id AND n.id=p.id
          LEFT JOIN missing_deadlines d ON d.company_id=p.company_id AND d.cfdi_id=p.id
          LEFT JOIN payment_flags pf ON pf.company_id=p.company_id AND pf.cfdi_id=p.id
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
        CREATE OR REPLACE FUNCTION movement_state() RETURNS TABLE(company_id uuid,id uuid,state text,reconciled_amount numeric,
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
        REVOKE UPDATE(sat_status,cancellation_status) ON cfdi FROM cfdi_app;
        ALTER TABLE cfdi DROP COLUMN cancellation_status;
        ALTER TABLE link DROP COLUMN review_reason;
        DROP FUNCTION cfdi_payable();
        DROP INDEX cfdi_relation_related_idx,link_payment_claim_idx,cfdi_payment_doc_coverage_idx;
        """);
}
