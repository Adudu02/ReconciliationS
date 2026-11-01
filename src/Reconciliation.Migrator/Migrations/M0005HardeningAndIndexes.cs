using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(5, "Bound settings and index tenant ingestion lookups")]
public sealed class M0005HardeningAndIndexes : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE company ADD CONSTRAINT company_date_tolerance_max CHECK(date_tolerance_days <= 366);
        CREATE INDEX cfdi_import_file_idx ON cfdi(company_id, import_file_id);
        CREATE INDEX bank_movement_import_file_idx ON bank_movement(company_id, import_file_id);
        CREATE INDEX cfdi_payment_doc_payment_idx ON cfdi_payment_doc(company_id, payment_id);
        CREATE INDEX cfdi_payment_doc_invoice_idx ON cfdi_payment_doc(company_id, invoice_uuid);
        CREATE INDEX import_file_original_idx ON import_file(company_id, original_file_id);
        ALTER TABLE import_file DROP CONSTRAINT import_file_status_check;
        ALTER TABLE import_file ADD CONSTRAINT import_file_status_check CHECK(status IN ('imported','already_imported','rejected','importing'));
        CREATE OR REPLACE FUNCTION preserve_import_original() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW.original IS DISTINCT FROM OLD.original AND NOT
            (OLD.status='importing' AND NEW.status='imported' AND OLD.original IS NULL AND NEW.original IS NOT NULL) THEN
            RAISE EXCEPTION 'Only an importing file may finalize its original';
          END IF;
          IF OLD.original IS NOT NULL AND (NEW.sha256 IS DISTINCT FROM OLD.sha256 OR NEW.retained_until IS NULL OR NEW.retained_until < OLD.retained_until) THEN
            RAISE EXCEPTION 'Accepted hashes are immutable; retention cannot be shortened';
          END IF;
          RETURN NEW;
        END $$;
        """);

    public override void Down() => Execute.Sql("""
        DROP INDEX import_file_original_idx, cfdi_payment_doc_invoice_idx, cfdi_payment_doc_payment_idx,
          bank_movement_import_file_idx, cfdi_import_file_idx;
        ALTER TABLE company DROP CONSTRAINT company_date_tolerance_max;
        ALTER TABLE import_file DROP CONSTRAINT import_file_status_check;
        ALTER TABLE import_file ADD CONSTRAINT import_file_status_check CHECK(status IN ('imported','already_imported','rejected'));
        CREATE OR REPLACE FUNCTION preserve_import_original() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF OLD.original IS NOT NULL AND (NEW.original IS DISTINCT FROM OLD.original OR NEW.sha256 IS DISTINCT FROM OLD.sha256
            OR NEW.retained_until IS NULL OR NEW.retained_until < OLD.retained_until) THEN
            RAISE EXCEPTION 'Accepted originals and their hashes are immutable; retention cannot be shortened';
          END IF;
          RETURN NEW;
        END $$;
        """);
}
