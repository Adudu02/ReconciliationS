using FluentMigrator;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Migrator.Migrations;

[Migration(4, "Tenant ingestion model and retained originals")]
public sealed class M0004Ingestion : Migration
{
    public override void Up() => Execute.Sql($$"""
        CREATE TABLE import_file (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id),
          upload_id uuid NOT NULL, filename text NOT NULL, kind text NOT NULL CHECK(kind IN ('cfdi','bank-statement')),
          status text NOT NULL CHECK(status IN ('imported','already_imported','rejected')), reason text,
          original bytea, sha256 bytea NOT NULL CHECK(octet_length(sha256) = 32),
          original_file_id uuid, retained_until timestamptz, imported_movements integer NOT NULL DEFAULT 0,
          duplicate_movements integer NOT NULL DEFAULT 0, created_at timestamptz NOT NULL DEFAULT now(),
          UNIQUE(company_id, id),
          FOREIGN KEY(company_id, original_file_id) REFERENCES import_file(company_id, id),
          CHECK(status <> 'rejected' OR original IS NULL),
          CHECK(status <> 'imported' OR (original IS NOT NULL AND sha256 = sha256(original) AND
            retained_until IS NOT NULL AND retained_until >= created_at + interval '{{IngestionOptions.MinimumRetentionYears}} years'))
        );
        CREATE INDEX import_file_upload_idx ON import_file(company_id, upload_id);
        CREATE TABLE cfdi (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id),
          import_file_id uuid NOT NULL, uuid uuid NOT NULL, type text NOT NULL CHECK(type IN ('I','P','E')),
          direction text NOT NULL CHECK(direction IN ('issued','received')), issuer_rfc text NOT NULL, receiver_rfc text NOT NULL,
          issued_at timestamp NOT NULL, stamped_at timestamp NOT NULL, currency text NOT NULL, payment_method text,
          subtotal numeric(18,2) NOT NULL CHECK(subtotal >= 0), discount numeric(18,2) NOT NULL DEFAULT 0 CHECK(discount >= 0 AND discount <= subtotal),
          transferred_tax numeric(18,2) NOT NULL DEFAULT 0 CHECK(transferred_tax >= 0),
          retained_tax numeric(18,2) NOT NULL DEFAULT 0 CHECK(retained_tax >= 0),
          local_transferred_tax numeric(18,2) NOT NULL DEFAULT 0 CHECK(local_transferred_tax >= 0),
          local_retained_tax numeric(18,2) NOT NULL DEFAULT 0 CHECK(local_retained_tax >= 0),
          total numeric(18,2) NOT NULL CHECK(total >= 0), is_global boolean NOT NULL DEFAULT false,
          review_reason text, sat_status text NOT NULL DEFAULT 'unknown', UNIQUE(company_id, uuid), UNIQUE(company_id, id),
          FOREIGN KEY(company_id, import_file_id) REFERENCES import_file(company_id, id),
          CHECK(total = subtotal - discount + transferred_tax - retained_tax + local_transferred_tax - local_retained_tax)
        );
        CREATE TABLE cfdi_relation (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id),
          cfdi_id uuid NOT NULL, related_uuid uuid NOT NULL, relation_code text NOT NULL,
          FOREIGN KEY(company_id, cfdi_id) REFERENCES cfdi(company_id, id),
          UNIQUE(company_id, cfdi_id, related_uuid, relation_code)
        );
        CREATE TABLE cfdi_payment (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id), cfdi_id uuid NOT NULL,
          payment_index integer NOT NULL CHECK(payment_index >= 0), paid_at timestamp NOT NULL, payment_form text NOT NULL,
          currency text NOT NULL, exchange_rate numeric(20,10) CHECK(exchange_rate > 0), amount numeric(18,2) NOT NULL CHECK(amount >= 0),
          operation_number text, UNIQUE(company_id, id), UNIQUE(company_id, cfdi_id, payment_index),
          FOREIGN KEY(company_id, cfdi_id) REFERENCES cfdi(company_id, id)
        );
        CREATE TABLE cfdi_payment_doc (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id), payment_id uuid NOT NULL,
          invoice_uuid uuid NOT NULL, installment integer NOT NULL CHECK(installment > 0), currency text NOT NULL,
          previous_balance numeric(18,2) NOT NULL CHECK(previous_balance >= 0),
          paid numeric(18,2) NOT NULL CHECK(paid >= 0), remaining_balance numeric(18,2) NOT NULL CHECK(remaining_balance >= 0),
          inconsistent_balance boolean NOT NULL DEFAULT false,
          FOREIGN KEY(company_id, payment_id) REFERENCES cfdi_payment(company_id, id)
        );
        CREATE TABLE bank_account (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id), bank text NOT NULL,
          account_reference text NOT NULL CHECK(account_reference ~ '^[A-Za-z0-9 _-]{1,40}$'),
          UNIQUE(company_id, bank, account_reference), UNIQUE(company_id, id)
        );
        CREATE TABLE bank_movement (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL REFERENCES company(id), bank_account_id uuid NOT NULL,
          import_file_id uuid, movement_date date NOT NULL, description text NOT NULL CHECK(length(description) > 0),
          credit numeric(18,2) NOT NULL CHECK(credit >= 0), debit numeric(18,2) NOT NULL CHECK(debit >= 0),
          balance numeric(18,2) NOT NULL, reference text, classification text,
          CHECK((credit > 0 AND debit = 0) OR (debit > 0 AND credit = 0)),
          FOREIGN KEY(company_id, bank_account_id) REFERENCES bank_account(company_id, id),
          FOREIGN KEY(company_id, import_file_id) REFERENCES import_file(company_id, id), UNIQUE(company_id, id),
          UNIQUE(company_id, bank_account_id, movement_date, description, credit, debit, balance)
        );
        DO $$ DECLARE tenant_table text; BEGIN
          FOREACH tenant_table IN ARRAY ARRAY['import_file','cfdi','cfdi_relation','cfdi_payment','cfdi_payment_doc','bank_account','bank_movement'] LOOP
            EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', tenant_table);
            EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', tenant_table);
            EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (company_id = {{TenantSql.InDynamicSql}}) WITH CHECK (company_id = {{TenantSql.InDynamicSql}})', tenant_table);
            EXECUTE format('GRANT SELECT, INSERT ON %I TO cfdi_app', tenant_table);
          END LOOP;
        END $$;
        GRANT UPDATE(status, reason, original, sha256, original_file_id, retained_until, imported_movements, duplicate_movements) ON import_file TO cfdi_app;
        GRANT UPDATE(review_reason) ON cfdi TO cfdi_app;
        GRANT UPDATE(inconsistent_balance) ON cfdi_payment_doc TO cfdi_app;
        CREATE FUNCTION preserve_import_original() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF OLD.original IS NOT NULL AND (NEW.original IS DISTINCT FROM OLD.original OR NEW.sha256 IS DISTINCT FROM OLD.sha256
            OR NEW.retained_until IS NULL OR NEW.retained_until < OLD.retained_until) THEN
            RAISE EXCEPTION 'Accepted originals and their hashes are immutable; retention cannot be shortened';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER import_original_immutable BEFORE UPDATE ON import_file
          FOR EACH ROW EXECUTE FUNCTION preserve_import_original();
        """);

    public override void Down() => Execute.Sql($$"""
        DROP TABLE bank_movement, bank_account, cfdi_payment_doc, cfdi_payment, cfdi_relation, cfdi, import_file;
        DROP FUNCTION preserve_import_original();
        """);
}
