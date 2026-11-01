using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(3, "Operator provisioning and minimal tenancy audit")]
public sealed class M0003CompanyManagement : Migration
{
    public override void Up() => Execute.Sql($$"""
        ALTER TABLE app_user ADD COLUMN is_operator boolean NOT NULL DEFAULT false;
        REVOKE UPDATE ON app_user FROM cfdi_app;
        CREATE FUNCTION prevent_rfc_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW.rfc IS DISTINCT FROM OLD.rfc THEN RAISE EXCEPTION 'Company RFC is immutable'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER company_rfc_immutable BEFORE UPDATE OF rfc ON company
          FOR EACH ROW EXECUTE FUNCTION prevent_rfc_change();
        CREATE TABLE audit_entry (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          company_id uuid NOT NULL REFERENCES company(id),
          actor_id uuid NOT NULL REFERENCES app_user(id),
          action text NOT NULL,
          old_values jsonb,
          new_values jsonb,
          created_at timestamptz NOT NULL DEFAULT now()
        );
        ALTER TABLE audit_entry ENABLE ROW LEVEL SECURITY;
        ALTER TABLE audit_entry FORCE ROW LEVEL SECURITY;
        CREATE POLICY audit_entry_isolation ON audit_entry
          USING (company_id = {{TenantSql.CompanyId}})
          WITH CHECK (company_id = {{TenantSql.CompanyId}});
        GRANT SELECT, INSERT ON audit_entry TO cfdi_app;
        """);

    public override void Down() => Execute.Sql($$"""
        DROP TABLE audit_entry;
        DROP TRIGGER company_rfc_immutable ON company;
        DROP FUNCTION prevent_rfc_change();
        ALTER TABLE app_user DROP COLUMN is_operator;
        GRANT UPDATE ON app_user TO cfdi_app;
        """);
}
