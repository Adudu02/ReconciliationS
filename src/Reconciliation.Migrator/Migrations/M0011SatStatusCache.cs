using FluentMigrator;
using Reconciliation.Core.Sat;

namespace Reconciliation.Migrator.Migrations;

[Migration(11, "Tenant SAT status cache and answered response provenance")]
public sealed class M0011SatStatusCache : Migration
{
    public override void Up()
    {
        Execute.Sql($$"""
        ALTER TABLE job ADD COLUMN seq bigint GENERATED ALWAYS AS IDENTITY;
        DROP INDEX job_due_idx;
        CREATE INDEX job_due_idx ON job(run_after,created_at,seq) WHERE state='queued';
        CREATE UNIQUE INDEX job_status_followup_idx ON job(company_id,(payload->>'followUpParentId'))
          WHERE type='status_check' AND state IN ('queued','running') AND payload ? 'followUpParentId';
        CREATE TABLE sat_status_cache (
          company_id uuid NOT NULL REFERENCES company(id),uuid uuid NOT NULL,
          state text NOT NULL CHECK(state IN ('current','cancelled','not_found','unknown')),
          cancellability text,cancellation_status text,efos_raw text,
          checked_at timestamptz NOT NULL,expires_at timestamptz,last_error text,
          attempts integer NOT NULL CHECK(attempts>=0),source text NOT NULL CHECK(source IN ('backfill','stub','real','replay')),
          code_raw text,state_raw text,cancellability_raw text,cancellation_status_raw text,last_job_id uuid,
          last_error_kind text CHECK(last_error_kind IN ('permanent','transient')),
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
        ALTER FUNCTION cfdi_review_reasons(date) RENAME TO cfdi_review_reasons_before_sat;
        CREATE FUNCTION cfdi_review_reasons(p_today date)
        RETURNS TABLE(company_id uuid,id uuid,reasons jsonb,requires_review boolean,complement_due_date date,complement_overdue boolean)
        LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
          SELECT c.company_id,c.id,coalesce(r.reasons,'[]'::jsonb) || CASE WHEN f.flagged THEN
            jsonb_build_array(jsonb_build_object('code','efos warning','requiresReview',true)) ELSE '[]'::jsonb END,
            coalesce(r.requires_review,false) OR f.flagged,r.complement_due_date,r.complement_overdue
          FROM public.cfdi c
          LEFT JOIN public.cfdi_review_reasons_before_sat(p_today) r ON r.company_id=c.company_id AND r.id=c.id
          LEFT JOIN public.sat_status_cache s ON s.company_id=c.company_id AND s.uuid=c.uuid
          CROSS JOIN LATERAL (SELECT coalesce(s.code_raw='{{SatContract.SuccessCode}}' AND s.efos_raw<>'' AND s.efos_raw<>'200',false) AS flagged) f
        $$;
        REVOKE ALL ON FUNCTION cfdi_review_reasons(date) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION cfdi_review_reasons(date) TO cfdi_app;
        """);
        Execute.Sql(ClaimFunction(true));
    }

    public override void Down()
    {
        Execute.Sql(ClaimFunction(false));
        Execute.Sql("""
        DROP FUNCTION cfdi_review_reasons(date);
        ALTER FUNCTION cfdi_review_reasons_before_sat(date) RENAME TO cfdi_review_reasons;
        DROP TABLE sat_status_cache;
        DROP INDEX job_status_followup_idx,job_due_idx;
        CREATE INDEX job_due_idx ON job(run_after,created_at,id) WHERE state='queued';
        ALTER TABLE job DROP COLUMN seq;
        """);
    }

    private static string ClaimFunction(bool upgraded) => $$"""
        CREATE OR REPLACE FUNCTION claim_next_job(worker_id text, lease_seconds integer, p_now timestamptz DEFAULT clock_timestamp())
        RETURNS SETOF job LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $function$
        DECLARE claimed public.job; expired public.job;
        BEGIN
          IF worker_id IS NULL OR length(worker_id)=0 OR lease_seconds IS NULL OR lease_seconds < 1 OR lease_seconds > 86400 THEN
            RAISE EXCEPTION 'Invalid worker or lease';
          END IF;
          FOR expired IN SELECT * FROM public.job WHERE state='running' AND locked_until<=p_now
            ORDER BY locked_until FOR UPDATE SKIP LOCKED LIMIT 100 LOOP
            UPDATE public.job_attempt SET outcome='lease_expired',error_kind='transient',error='Worker lease expired',finished_at=p_now
              WHERE company_id=expired.company_id AND job_id=expired.id AND attempt_no=expired.attempts;
            UPDATE public.job SET state=CASE WHEN attempts<max_attempts THEN 'queued' ELSE 'failed' END,
              run_after={{(upgraded ? "LEAST(run_after,p_now)" : "p_now")}},locked_by=NULL,locked_until=NULL,last_error='Worker lease expired',
              finished_at=CASE WHEN attempts>=max_attempts THEN p_now ELSE NULL END WHERE id=expired.id;
          END LOOP;
          SELECT * INTO claimed FROM public.job WHERE state='queued' AND run_after<=p_now AND attempts<max_attempts
            ORDER BY run_after,created_at,{{(upgraded ? "seq" : "id")}} FOR UPDATE SKIP LOCKED LIMIT 1;
          IF NOT FOUND THEN RETURN; END IF;
          UPDATE public.job SET state='running',attempts=attempts+1,locked_by=worker_id,
            locked_until=p_now+make_interval(secs=>lease_seconds),finished_at=NULL WHERE id=claimed.id RETURNING * INTO claimed;
          INSERT INTO public.job_attempt(company_id,job_id,attempt_no,started_at,outcome)
            VALUES(claimed.company_id,claimed.id,claimed.attempts,p_now,'running');
          RETURN NEXT claimed;
        END $function$;
        """;
}
