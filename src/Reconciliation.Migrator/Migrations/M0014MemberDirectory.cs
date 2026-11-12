using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(14, "Restricted company member email directory")]
public sealed class M0014MemberDirectory : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE ROLE cfdi_member_directory_owner NOLOGIN NOSUPERUSER BYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
        GRANT USAGE ON SCHEMA public TO cfdi_member_directory_owner;
        GRANT SELECT(id,email) ON app_user TO cfdi_member_directory_owner;
        GRANT SELECT(company_id,user_id,role) ON membership TO cfdi_member_directory_owner;
        CREATE FUNCTION company_members(p_company_id uuid,p_user_id uuid)
        RETURNS TABLE(user_id uuid,email text,role text)
        LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
        BEGIN
          IF p_company_id IS NULL OR p_user_id IS NULL
            OR current_setting('app.company_id',true) IS DISTINCT FROM p_company_id::text
            OR current_setting('app.user_id',true) IS DISTINCT FROM p_user_id::text
            OR NOT EXISTS(SELECT FROM public.membership m WHERE m.company_id=p_company_id AND m.user_id=p_user_id) THEN
            RAISE EXCEPTION 'Authenticated company membership required' USING ERRCODE='42501';
          END IF;
          RETURN QUERY SELECT u.id,u.email,m.role::text
            FROM public.membership m JOIN public.app_user u ON u.id=m.user_id
            WHERE m.company_id=p_company_id ORDER BY lower(u.email),u.id;
        END $$;
        ALTER FUNCTION company_members(uuid,uuid) OWNER TO cfdi_member_directory_owner;
        REVOKE ALL ON FUNCTION company_members(uuid,uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION company_members(uuid,uuid) TO cfdi_app;
        """);

    public override void Down() => Execute.Sql("""
        DROP FUNCTION company_members(uuid,uuid);
        REVOKE SELECT(id,email) ON app_user FROM cfdi_member_directory_owner;
        REVOKE SELECT(company_id,user_id,role) ON membership FROM cfdi_member_directory_owner;
        REVOKE USAGE ON SCHEMA public FROM cfdi_member_directory_owner;
        DROP ROLE cfdi_member_directory_owner;
        """);
}
