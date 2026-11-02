using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(7, "Tenant job queue and restricted cross-company claiming")]
public sealed class M0007Jobs : Migration
{
    public override void Up() => Execute.Sql($$"""
        CREATE ROLE cfdi_queue_owner NOLOGIN NOSUPERUSER BYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
        GRANT USAGE ON SCHEMA public TO cfdi_queue_owner;
        CREATE TABLE job (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id),
          type text NOT NULL CHECK(type IN ('import_cfdi','import_statement','reconciliation_run','status_check')),
          state text NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','running','succeeded','failed')),
          payload jsonb NOT NULL DEFAULT '{}', input_ref uuid,
          attempts integer NOT NULL DEFAULT 0 CHECK(attempts >= 0), max_attempts integer NOT NULL DEFAULT 5 CHECK(max_attempts > 0),
          run_after timestamptz NOT NULL DEFAULT clock_timestamp(), locked_by text, locked_until timestamptz,
          progress_done integer NOT NULL DEFAULT 0 CHECK(progress_done >= 0), progress_total integer NOT NULL DEFAULT 0 CHECK(progress_total >= progress_done),
          last_error text, created_by uuid NOT NULL REFERENCES app_user(id), created_at timestamptz NOT NULL DEFAULT clock_timestamp(), finished_at timestamptz,
          UNIQUE(company_id,id)
        );
        CREATE TABLE job_attempt (
          company_id uuid NOT NULL, job_id uuid NOT NULL, attempt_no integer NOT NULL CHECK(attempt_no > 0),
          started_at timestamptz NOT NULL, finished_at timestamptz, outcome text NOT NULL CHECK(outcome IN ('running','succeeded','failed','lease_expired')),
          error_kind text CHECK(error_kind IN ('transient','permanent')), error text,
          PRIMARY KEY(company_id,job_id,attempt_no), FOREIGN KEY(company_id,job_id) REFERENCES job(company_id,id)
        );
        CREATE INDEX job_due_idx ON job(run_after,created_at,id) WHERE state='queued';
        CREATE INDEX job_company_state_idx ON job(company_id,state,created_at);
        CREATE INDEX job_expired_idx ON job(locked_until) WHERE state='running';
        CREATE UNIQUE INDEX job_one_reconciliation_idx ON job(company_id) WHERE type='reconciliation_run' AND state IN ('queued','running');
        ALTER TABLE job ENABLE ROW LEVEL SECURITY;
        ALTER TABLE job FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON job USING(company_id={{TenantSql.CompanyId}}) WITH CHECK(company_id={{TenantSql.CompanyId}});
        ALTER TABLE job_attempt ENABLE ROW LEVEL SECURITY;
        ALTER TABLE job_attempt FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON job_attempt USING(company_id={{TenantSql.CompanyId}}) WITH CHECK(company_id={{TenantSql.CompanyId}});
        GRANT SELECT,INSERT,UPDATE ON job,job_attempt TO cfdi_queue_owner;
        GRANT SELECT,INSERT ON job TO cfdi_app;
        GRANT SELECT ON job_attempt TO cfdi_app;
        GRANT UPDATE(state,max_attempts,run_after,finished_at,last_error) ON job TO cfdi_app;
        CREATE FUNCTION claim_next_job(worker_id text, lease_seconds integer, p_now timestamptz DEFAULT clock_timestamp())
        RETURNS SETOF job LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
        DECLARE claimed public.job;
        BEGIN
          IF worker_id IS NULL OR length(worker_id)=0 OR lease_seconds < 1 OR lease_seconds > 86400 THEN
            RAISE EXCEPTION 'Invalid worker or lease';
          END IF;
          SELECT * INTO claimed FROM public.job WHERE state='queued' AND run_after<=p_now AND attempts<max_attempts
            ORDER BY run_after,created_at,id FOR UPDATE SKIP LOCKED LIMIT 1;
          IF NOT FOUND THEN RETURN; END IF;
          UPDATE public.job SET state='running',attempts=attempts+1,locked_by=worker_id,
            locked_until=p_now+make_interval(secs=>lease_seconds),finished_at=NULL WHERE id=claimed.id RETURNING * INTO claimed;
          INSERT INTO public.job_attempt(company_id,job_id,attempt_no,started_at,outcome)
            VALUES(claimed.company_id,claimed.id,claimed.attempts,p_now,'running');
          RETURN NEXT claimed;
        END $$;
        CREATE FUNCTION finish_job(p_id uuid,p_worker text,p_attempt integer,p_error_kind text,p_error text,p_retry_at timestamptz,
          p_now timestamptz DEFAULT clock_timestamp()) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
          SET search_path=pg_catalog,public,pg_temp AS $$
        DECLARE claimed public.job; next_state text;
        BEGIN
          IF (p_error_kind IS NULL) <> (p_error IS NULL) OR p_error_kind NOT IN ('transient','permanent') THEN
            RAISE EXCEPTION 'Invalid failure classification';
          END IF;
          SELECT * INTO claimed FROM public.job WHERE id=p_id AND state='running' AND locked_by=p_worker
            AND attempts=p_attempt AND locked_until>p_now FOR UPDATE;
          IF NOT FOUND THEN RETURN false; END IF;
          next_state := CASE WHEN p_error_kind IS NULL THEN 'succeeded'
            WHEN p_error_kind='transient' AND claimed.attempts<claimed.max_attempts THEN 'queued' ELSE 'failed' END;
          UPDATE public.job_attempt SET outcome=CASE WHEN p_error_kind IS NULL THEN 'succeeded' ELSE 'failed' END,
            finished_at=p_now,error_kind=p_error_kind,error=p_error
            WHERE company_id=claimed.company_id AND job_id=claimed.id AND attempt_no=claimed.attempts;
          UPDATE public.job SET state=next_state,last_error=p_error,locked_by=NULL,locked_until=NULL,
            run_after=CASE WHEN next_state='queued' THEN p_retry_at ELSE run_after END,
            finished_at=CASE WHEN next_state IN ('succeeded','failed') THEN p_now ELSE NULL END,
            progress_done=CASE WHEN next_state='succeeded' THEN progress_total ELSE progress_done END WHERE id=claimed.id;
          RETURN true;
        END $$;
        ALTER FUNCTION finish_job(uuid,text,integer,text,text,timestamptz,timestamptz) OWNER TO cfdi_queue_owner;
        REVOKE ALL ON FUNCTION finish_job(uuid,text,integer,text,text,timestamptz,timestamptz) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION finish_job(uuid,text,integer,text,text,timestamptz,timestamptz) TO cfdi_app;
        ALTER FUNCTION claim_next_job(text,integer,timestamptz) OWNER TO cfdi_queue_owner;
        REVOKE ALL ON FUNCTION claim_next_job(text,integer,timestamptz) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION claim_next_job(text,integer,timestamptz) TO cfdi_app;
        """);

    public override void Down() => Execute.Sql("""
        DROP FUNCTION finish_job(uuid,text,integer,text,text,timestamptz,timestamptz);
        DROP FUNCTION claim_next_job(text,integer,timestamptz);
        DROP TABLE job_attempt,job;
        REVOKE USAGE ON SCHEMA public FROM cfdi_queue_owner;
        DROP ROLE cfdi_queue_owner;
        """);
}
