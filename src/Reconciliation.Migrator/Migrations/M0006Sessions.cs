using FluentMigrator;

namespace Reconciliation.Migrator.Migrations;

[Migration(6, "User sessions rotating refresh credentials and authentication events")]
public sealed class M0006Sessions : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE TABLE auth_session (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), user_id uuid NOT NULL REFERENCES app_user(id),
          created_at timestamptz NOT NULL, last_used_at timestamptz NOT NULL, absolute_expires_at timestamptz NOT NULL,
          revoked_at timestamptz, revoked_reason text,
          CHECK(absolute_expires_at > created_at), CHECK(last_used_at >= created_at)
        );
        CREATE INDEX auth_session_user_idx ON auth_session(user_id);
        CREATE TABLE refresh_token (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), session_id uuid NOT NULL REFERENCES auth_session(id),
          token_hash bytea NOT NULL UNIQUE CHECK(octet_length(token_hash)=32), issued_at timestamptz NOT NULL,
          expires_at timestamptz NOT NULL, rotated_at timestamptz, replaced_by uuid REFERENCES refresh_token(id),
          CHECK(expires_at > issued_at)
        );
        CREATE INDEX refresh_token_session_idx ON refresh_token(session_id);
        CREATE TABLE auth_event (
          id uuid PRIMARY KEY DEFAULT gen_random_uuid(), user_id uuid NOT NULL REFERENCES app_user(id),
          session_id uuid NOT NULL REFERENCES auth_session(id),
          event text NOT NULL CHECK(event IN ('login','refresh','reuse_detected','logout','password_changed')),
          remote_ip inet, created_at timestamptz NOT NULL
        );
        GRANT SELECT, INSERT ON auth_session, refresh_token, auth_event TO cfdi_app;
        GRANT UPDATE(last_used_at, revoked_at, revoked_reason) ON auth_session TO cfdi_app;
        GRANT UPDATE(rotated_at, replaced_by) ON refresh_token TO cfdi_app;
        GRANT UPDATE(password_hash) ON app_user TO cfdi_app;
        """);

    public override void Down() => Execute.Sql("""
        REVOKE UPDATE(password_hash) ON app_user FROM cfdi_app;
        DROP TABLE auth_event, refresh_token, auth_session;
        """);
}
