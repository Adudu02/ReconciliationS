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
        """);

    public override void Down() => Execute.Sql("""
        DROP INDEX import_file_original_idx, cfdi_payment_doc_invoice_idx, cfdi_payment_doc_payment_idx,
          bank_movement_import_file_idx, cfdi_import_file_idx;
        ALTER TABLE company DROP CONSTRAINT company_date_tolerance_max;
        """);
}
