using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(13, "Restricted user membership directory")]
public sealed class M0013CompanyDirectory : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE ROLE cfdi_directory_owner NOLOGIN NOSUPERUSER BYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
        GRANT USAGE ON SCHEMA public TO cfdi_directory_owner;
        GRANT SELECT(id,rfc,name,archived_at) ON company TO cfdi_directory_owner;
        GRANT SELECT(user_id,company_id,role) ON membership TO cfdi_directory_owner;
        CREATE FUNCTION user_companies(p_user_id uuid)
        RETURNS TABLE(id uuid,rfc text,name text,archived_at timestamptz,role text)
        LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
        BEGIN
          IF p_user_id IS NULL OR current_setting('app.user_id',true) IS DISTINCT FROM p_user_id::text THEN
            RAISE EXCEPTION 'Authenticated user context required' USING ERRCODE='42501';
          END IF;
          RETURN QUERY SELECT c.id,c.rfc,c.name,c.archived_at,m.role::text
            FROM public.membership m JOIN public.company c ON c.id=m.company_id
            WHERE m.user_id=p_user_id ORDER BY c.name,c.id;
        END $$;
        ALTER FUNCTION user_companies(uuid) OWNER TO cfdi_directory_owner;
        REVOKE ALL ON FUNCTION user_companies(uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION user_companies(uuid) TO cfdi_app;
        """);

    public override void Down() => Execute.Sql("""
        DROP FUNCTION user_companies(uuid);
        REVOKE SELECT(id,rfc,name,archived_at) ON company FROM cfdi_directory_owner;
        REVOKE SELECT(user_id,company_id,role) ON membership FROM cfdi_directory_owner;
        REVOKE USAGE ON SCHEMA public FROM cfdi_directory_owner;
        DROP ROLE cfdi_directory_owner;
        """);
}
