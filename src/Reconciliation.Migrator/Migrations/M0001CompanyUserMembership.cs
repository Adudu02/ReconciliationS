using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(1, "Company, user and membership")]
public sealed class M0001CompanyUserMembership : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE TYPE membership_role AS ENUM ('administrator', 'accountant', 'viewer');

        CREATE TABLE company (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          rfc text NOT NULL CHECK (rfc ~ '^[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}$'),
          name text NOT NULL,
          date_tolerance_days integer NOT NULL DEFAULT 3 CHECK (date_tolerance_days >= 0),
          amount_tolerance numeric(18,2) NOT NULL DEFAULT 0.01 CHECK (amount_tolerance >= 0),
          complement_next_biz_day boolean NOT NULL DEFAULT false,
          created_at timestamptz NOT NULL DEFAULT now(),
          archived_at timestamptz
        );

        CREATE TABLE app_user (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
          email text NOT NULL,
          password_hash text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE UNIQUE INDEX app_user_email_key ON app_user (lower(email));

        CREATE TABLE membership (
          company_id uuid NOT NULL REFERENCES company (id),
          user_id uuid NOT NULL REFERENCES app_user (id),
          role membership_role NOT NULL,
          PRIMARY KEY (company_id, user_id)
        );
        CREATE INDEX membership_user_idx ON membership (user_id);
        """);

    public override void Down() => Execute.Sql("""
        DROP TABLE membership;
        DROP TABLE app_user;
        DROP TABLE company;
        DROP TYPE membership_role;
        """);
}
