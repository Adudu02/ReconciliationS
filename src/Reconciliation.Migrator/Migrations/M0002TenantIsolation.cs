using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(2, "Force tenant row-level security")]
public sealed class M0002TenantIsolation : Migration
{
    public override void Up() => Execute.Sql($$"""
        DO $$ BEGIN
          IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'cfdi_app') THEN
            CREATE ROLE cfdi_app NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
          END IF;
        END $$;
        GRANT USAGE ON SCHEMA public TO cfdi_app;
        GRANT SELECT, INSERT, UPDATE ON company, app_user, membership TO cfdi_app;
        ALTER TABLE company ENABLE ROW LEVEL SECURITY;
        ALTER TABLE company FORCE ROW LEVEL SECURITY;
        CREATE POLICY company_isolation ON company
          USING (id = {{TenantSql.CompanyId}})
          WITH CHECK (id = {{TenantSql.CompanyId}});
        ALTER TABLE membership ENABLE ROW LEVEL SECURITY;
        ALTER TABLE membership FORCE ROW LEVEL SECURITY;
        CREATE POLICY membership_isolation ON membership
          USING (company_id = {{TenantSql.CompanyId}})
          WITH CHECK (company_id = {{TenantSql.CompanyId}});
        """);

    public override void Down() => Execute.Sql($$"""
        DROP POLICY membership_isolation ON membership;
        ALTER TABLE membership NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE membership DISABLE ROW LEVEL SECURITY;
        DROP POLICY company_isolation ON company;
        ALTER TABLE company NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE company DISABLE ROW LEVEL SECURITY;
        REVOKE ALL ON company, app_user, membership FROM cfdi_app;
        """);
}
