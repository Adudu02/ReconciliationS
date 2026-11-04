using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(8, "Append-only tenant audit chains")]
public sealed class M0008AuditChain : Migration
{
    public override void Up() => Execute.Sql($$"""
        CREATE ROLE cfdi_audit_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
        GRANT USAGE ON SCHEMA public TO cfdi_audit_owner;
        ALTER TABLE audit_entry ALTER COLUMN actor_id DROP NOT NULL;
        ALTER TABLE audit_entry ADD COLUMN company_seq bigint, ADD COLUMN prev_hash bytea, ADD COLUMN hash bytea,
          ADD COLUMN actor_type text NOT NULL DEFAULT 'user', ADD COLUMN items jsonb NOT NULL DEFAULT '[]',
          ADD COLUMN detail jsonb, ADD COLUMN occurred_at timestamptz, ADD COLUMN canonical text;
        UPDATE audit_entry SET detail=coalesce(new_values->'_audit_v8'->'detail',jsonb_build_object('old',old_values,'new',new_values)),
          actor_type=coalesce(new_values->'_audit_v8'->>'actorType','user'),occurred_at=created_at,
          items=coalesce(new_values->'_audit_v8'->'items',(SELECT coalesce(jsonb_agg(jsonb_build_object('kind',ref.kind,'id',ref.id)),'[]')
            FROM (VALUES ('import_file',new_values->>'fileId'),('job',new_values->>'jobId'),('user',new_values->>'userId')) ref(kind,id)
            WHERE ref.id IS NOT NULL));
        ALTER TABLE audit_entry DROP COLUMN old_values, DROP COLUMN new_values, DROP COLUMN created_at;
        ALTER TABLE audit_entry ADD COLUMN old_values jsonb GENERATED ALWAYS AS (nullif(detail->'old','null'::jsonb)) STORED,
          ADD COLUMN new_values jsonb GENERATED ALWAYS AS (nullif(detail->'new','null'::jsonb)) STORED,
          ADD COLUMN created_at timestamptz GENERATED ALWAYS AS (occurred_at) STORED;
        CREATE TABLE audit_chain_head (
          company_id uuid PRIMARY KEY REFERENCES company(id), last_seq bigint NOT NULL CHECK(last_seq>=0),
          last_hash bytea NOT NULL CHECK(octet_length(last_hash)=32)
        );
        ALTER TABLE audit_chain_head ENABLE ROW LEVEL SECURITY;
        ALTER TABLE audit_chain_head FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON audit_chain_head USING(company_id={{TenantSql.CompanyId}}) WITH CHECK(company_id={{TenantSql.CompanyId}});
        CREATE FUNCTION audit_canonical(p_company uuid,p_seq bigint,p_time timestamptz,p_type text,p_actor uuid,p_action text,p_items jsonb,p_detail jsonb)
        RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,public,pg_temp AS $$
          SELECT jsonb_build_array(p_company::text,p_seq::text,((extract(epoch FROM p_time)*1000000)::bigint)::text,
            p_type,coalesce(p_actor::text,'system'),p_action,p_items,p_detail)::text
        $$;
        REVOKE ALL ON FUNCTION audit_canonical(uuid,bigint,timestamptz,text,uuid,text,jsonb,jsonb) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION audit_canonical(uuid,bigint,timestamptz,text,uuid,text,jsonb,jsonb) TO cfdi_app,cfdi_audit_owner;
        DO $$
        DECLARE entry record; previous_company uuid; sequence_no bigint:=0; previous_hash bytea:=decode(repeat('00',32),'hex'); canonical_text text; entry_hash bytea;
        BEGIN
          FOR entry IN SELECT * FROM audit_entry ORDER BY company_id,occurred_at,id LOOP
            IF entry.company_id IS DISTINCT FROM previous_company THEN
              sequence_no:=0; previous_hash:=decode(repeat('00',32),'hex'); previous_company:=entry.company_id;
            END IF;
            sequence_no:=sequence_no+1;
            canonical_text:=public.audit_canonical(entry.company_id,sequence_no,entry.occurred_at,entry.actor_type,entry.actor_id,entry.action,entry.items,entry.detail);
            entry_hash:=sha256(convert_to(canonical_text,'UTF8') || previous_hash);
            UPDATE audit_entry SET company_seq=sequence_no,prev_hash=previous_hash,hash=entry_hash,canonical=canonical_text WHERE id=entry.id;
            INSERT INTO audit_chain_head VALUES(entry.company_id,sequence_no,entry_hash)
              ON CONFLICT(company_id) DO UPDATE SET last_seq=excluded.last_seq,last_hash=excluded.last_hash;
            previous_hash:=entry_hash;
          END LOOP;
        END $$;
        ALTER TABLE audit_entry ALTER COLUMN company_seq SET NOT NULL, ALTER COLUMN prev_hash SET NOT NULL,
          ALTER COLUMN hash SET NOT NULL, ALTER COLUMN detail SET NOT NULL, ALTER COLUMN occurred_at SET NOT NULL, ALTER COLUMN canonical SET NOT NULL;
        ALTER TABLE audit_entry ADD CONSTRAINT audit_actor CHECK(actor_type IN ('user','system') AND (actor_type='user')=(actor_id IS NOT NULL)),
          ADD CONSTRAINT audit_sequence CHECK(company_seq>0),
          ADD CONSTRAINT audit_hash_lengths CHECK(octet_length(prev_hash)=32 AND octet_length(hash)=32),
          ADD CONSTRAINT audit_items_array CHECK(jsonb_typeof(items)='array'),
          ADD CONSTRAINT audit_detail_object CHECK(jsonb_typeof(detail)='object'),
          ADD CONSTRAINT audit_system_source CHECK(actor_type<>'system' OR detail ? 'jobId' OR detail ? 'source'),
          ADD CONSTRAINT audit_company_sequence UNIQUE(company_id,company_seq);
        CREATE INDEX audit_chronological_idx ON audit_entry(company_id,occurred_at,id);
        GRANT SELECT,INSERT ON audit_entry TO cfdi_audit_owner;
        GRANT SELECT,INSERT,UPDATE ON audit_chain_head TO cfdi_audit_owner;
        GRANT SELECT ON audit_chain_head TO cfdi_app;
        REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON audit_entry FROM cfdi_app;
        CREATE FUNCTION append_audit_entry(p_company uuid,p_type text,p_actor uuid,p_action text,p_items jsonb,p_detail jsonb)
        RETURNS public.audit_entry LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
        DECLARE head public.audit_chain_head; entry public.audit_entry; happened_at timestamptz; canonical_text text;
        BEGIN
          IF p_company IS DISTINCT FROM nullif(current_setting('app.company_id',true),'')::uuid OR p_company IS NULL THEN
            RAISE EXCEPTION 'Audit company context mismatch' USING ERRCODE='42501';
          END IF;
          IF jsonb_typeof(p_items) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'Audit items must be typed references' USING ERRCODE='23514'; END IF;
          IF EXISTS(SELECT FROM jsonb_array_elements(p_items) ref WHERE jsonb_typeof(ref) IS DISTINCT FROM 'object'
            OR jsonb_typeof(ref->'kind') IS DISTINCT FROM 'string' OR jsonb_typeof(ref->'id') IS DISTINCT FROM 'string'
            OR ref->>'kind'='' OR ref->>'id'='') THEN
            RAISE EXCEPTION 'Audit items must have string kind and id' USING ERRCODE='23514';
          END IF;
          INSERT INTO public.audit_chain_head VALUES(p_company,0,decode(repeat('00',32),'hex')) ON CONFLICT(company_id) DO NOTHING;
          SELECT * INTO STRICT head FROM public.audit_chain_head WHERE company_id=p_company FOR UPDATE;
          happened_at:=clock_timestamp();
          canonical_text:=public.audit_canonical(p_company,head.last_seq+1,happened_at,p_type,p_actor,p_action,p_items,p_detail);
          INSERT INTO public.audit_entry(company_id,company_seq,prev_hash,hash,occurred_at,actor_type,actor_id,action,items,detail,canonical)
            VALUES(p_company,head.last_seq+1,head.last_hash,sha256(convert_to(canonical_text,'UTF8') || head.last_hash),happened_at,p_type,p_actor,p_action,p_items,p_detail,canonical_text)
            RETURNING * INTO entry;
          UPDATE public.audit_chain_head SET last_seq=entry.company_seq,last_hash=entry.hash WHERE company_id=p_company;
          RETURN entry;
        END $$;
        ALTER FUNCTION append_audit_entry(uuid,text,uuid,text,jsonb,jsonb) OWNER TO cfdi_audit_owner;
        REVOKE ALL ON FUNCTION append_audit_entry(uuid,text,uuid,text,jsonb,jsonb) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION append_audit_entry(uuid,text,uuid,text,jsonb,jsonb) TO cfdi_app;
        CREATE FUNCTION verify_audit_chain(p_company uuid)
        RETURNS TABLE(consistent boolean,entries bigint,first_inconsistent_seq bigint,reason text)
        LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,public,pg_temp AS $$
        DECLARE entry public.audit_entry; head public.audit_chain_head; counted bigint:=0;
          previous_hash bytea:=decode(repeat('00',32),'hex'); parsed jsonb; problem text;
        BEGIN
          FOR entry IN SELECT * FROM public.audit_entry WHERE company_id=p_company ORDER BY company_seq LOOP
            problem:=NULL;
            IF entry.company_seq<>counted+1 THEN problem:='sequence_gap';
            ELSIF entry.prev_hash<>previous_hash THEN problem:='previous_hash_mismatch';
            ELSE
              BEGIN parsed:=entry.canonical::jsonb;
              EXCEPTION WHEN invalid_text_representation THEN problem:='canonical_invalid'; END;
              IF problem IS NULL AND parsed IS DISTINCT FROM public.audit_canonical(entry.company_id,entry.company_seq,
                entry.occurred_at,entry.actor_type,entry.actor_id,entry.action,entry.items,entry.detail)::jsonb THEN
                problem:='canonical_mismatch';
              END IF;
              IF problem IS NULL AND sha256(convert_to(entry.canonical,'UTF8') || entry.prev_hash)<>entry.hash THEN problem:='hash_mismatch'; END IF;
            END IF;
            IF problem IS NOT NULL THEN RETURN QUERY SELECT false,counted,counted+1,problem; RETURN; END IF;
            counted:=counted+1; previous_hash:=entry.hash;
          END LOOP;
          SELECT * INTO head FROM public.audit_chain_head WHERE company_id=p_company;
          IF (head.company_id IS NULL AND counted>0) OR
            (head.company_id IS NOT NULL AND (head.last_seq<>counted OR head.last_hash<>previous_hash)) THEN
            RETURN QUERY SELECT false,counted,CASE WHEN head.last_seq>counted THEN counted+1 ELSE greatest(counted,1) END,'head_mismatch'::text;
          ELSE RETURN QUERY SELECT true,counted,NULL::bigint,NULL::text;
          END IF;
        END $$;
        REVOKE ALL ON FUNCTION verify_audit_chain(uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION verify_audit_chain(uuid) TO cfdi_app;
        CREATE FUNCTION refuse_audit_mutation() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public,pg_temp AS $$
        BEGIN RAISE EXCEPTION 'Audit entries are append-only'; END $$;
        CREATE TRIGGER audit_entry_immutable BEFORE UPDATE OR DELETE ON audit_entry FOR EACH ROW EXECUTE FUNCTION refuse_audit_mutation();
        CREATE TRIGGER audit_entry_no_truncate BEFORE TRUNCATE ON audit_entry FOR EACH STATEMENT EXECUTE FUNCTION refuse_audit_mutation();
        """);

    public override void Down() => Execute.Sql("""
        DROP TRIGGER audit_entry_immutable ON audit_entry;
        DROP TRIGGER audit_entry_no_truncate ON audit_entry;
        DROP FUNCTION refuse_audit_mutation();
        DROP FUNCTION IF EXISTS verify_audit_chain(uuid);
        DROP FUNCTION append_audit_entry(uuid,text,uuid,text,jsonb,jsonb);
        DROP FUNCTION audit_canonical(uuid,bigint,timestamptz,text,uuid,text,jsonb,jsonb);
        DROP TABLE audit_chain_head;
        DROP INDEX audit_chronological_idx;
        ALTER TABLE audit_entry ALTER COLUMN old_values DROP EXPRESSION, ALTER COLUMN new_values DROP EXPRESSION, ALTER COLUMN created_at DROP EXPRESSION;
        ALTER TABLE audit_entry ALTER COLUMN created_at SET DEFAULT now();
        -- Preserve group-5 metadata for a subsequent Up while keeping legacy new-value fields readable.
        UPDATE audit_entry SET new_values=coalesce(new_values,'{}') || jsonb_build_object('_audit_v8',
          jsonb_build_object('actorType',actor_type,'items',items,'detail',detail));
        ALTER TABLE audit_entry DROP COLUMN company_seq, DROP COLUMN prev_hash, DROP COLUMN hash,
          DROP COLUMN actor_type, DROP COLUMN items, DROP COLUMN detail, DROP COLUMN occurred_at, DROP COLUMN canonical;
        REVOKE ALL ON audit_entry FROM cfdi_audit_owner;
        REVOKE USAGE ON SCHEMA public FROM cfdi_audit_owner;
        DROP ROLE cfdi_audit_owner;
        GRANT INSERT ON audit_entry TO cfdi_app;
        """);
}
