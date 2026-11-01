# ADR-003: Tenant isolation with PostgreSQL row-level security

Status: accepted.

## Context

Several companies share one installation. A missing company filter in SQL must not expose another company's
rows. The demo uses PostgreSQL 17, explicit SQL through Npgsql/Dapper, one API and a separate worker.
Authentication identifies a user; membership assigns that user a different role in each company.

## Decision

Use a shared schema with row-level security (RLS). `company` is scoped by its `id`; every other tenant table
carries `company_id`. Global identity (`app_user`) is outside tenant data. Only login, operator checks and
account provisioning access that identity store; there is no public user-list endpoint.

- `cfdi_app` is separate from the migration owner, without superuser or BYPASSRLS. The API and worker connect
  as this restricted role. Migrations run with the privileged owner connection.
- Every tenant table enables and uses `FORCE ROW LEVEL SECURITY`, with both `USING` and `WITH CHECK` matching
  the current company. Policies apply to reads, inserts and updates. No application DELETE privilege is granted.
- `CompanyScope.ExecuteAsync` opens a transaction and calls parameterized `set_config('app.company_id', value,
  true)`, equivalent to `SET LOCAL`. Commit, rollback and disposal discard that context before connection reuse.
  Absent context yields no visible rows and the wrapper logs the attempt. Jobs will use the same wrapper.
- Each company request supplies `X-Company-Id`. Authorization checks the authenticated user's current membership
  in PostgreSQL and requires the route's company id to agree with the header. Roles are evaluated per request,
  rather than copied into a token that becomes stale when a membership changes.
- The isolation suite discovers tenant tables through `information_schema` and verifies enabled/forced RLS plus
  policies. It discovers routes through `EndpointDataSource`, verifies authentication and requires a successful
  baseline and cross-company case for every data route. New tables/routes fail these guards until covered.

RLS protects against omitted SQL filters. It does not protect against a compromised application that deliberately
selects arbitrary company contexts, a privileged database administrator, or SQL injection. Parameterized SQL,
authenticated membership checks and keeping owner credentials out of runtime hosts remain necessary.

## Alternatives compared

| Option | Benefit | Cost and reason rejected for this demo |
|---|---|---|
| Shared schema with row-level security | One migration path, one connection pool; the database enforces a missing-filter boundary | Context and permissions must be correct; policies need tests on every new table |
| schema-per-company | Separate table namespaces; easier per-company logical export | Each migration must reach every schema; search paths and connection pooling add routing hazards |
| database-per-company | Stronger operational separation, independent backup/restore and scaling | Database creation, migrations, pools and monitoring multiply; too costly for this local multi-company demo |
| Application filters alone | Least database configuration | A forgotten WHERE leaks data, so it fails the requirement |

Reconsider database-per-company when a customer needs independent recovery, stronger operational separation or
resource isolation. Schema-per-company does not remove the need to validate which company the caller may select.

## Provisioning and operation

The migrator's optional `OPERATOR_EMAIL` / `OPERATOR_PASSWORD` seed inserts an operator outside company memberships.
It does not overwrite existing passwords or promote an existing non-operator. `APP_DB_PASSWORD` configures the
application role's login; `Jwt__SigningKey` configures the API's token signing key. `.env.example` contains only
placeholders. Compose's explicitly synthetic defaults are for local demonstration; replace them for any shared use.

`POST /companies` accepts `rfc`, `name`, `adminEmail`, `adminPassword`. A new administrator's password must have at
least 12 characters and is stored with Identity's salted PBKDF2 PasswordHasher. Existing users keep their password;
only a membership is attached, and the response shape is identical. Password-in-request is a demo shortcut:
production would use invite/reset links, secret management and a managed identity provider or equivalent controls.

RFC format is checked in the API and database, and a trigger prevents later changes. Settings and archive actions
are audited in the same transaction as the action. The minimal audit table has tenant RLS and SELECT/INSERT
application privileges; group 5 adds rejection triggers, hash chaining, verification and the broader audit contract.
The migration down path removes this minimal table and its entries; use rollback on disposable development data,
or back up before reverting a populated installation. The shared database role intentionally survives rollback.

## Verification and remaining work

Testcontainers runs the real migrations against PostgreSQL. Tests prove absent-context reads, unfiltered reads,
write refusal, role restrictions, pooled connection cleanup, credential handling and transactional audit rollback.
Deliberately dropping `membership_isolation` made the suite fail; restoring the policy returned it to green.

Group 3 now supplies real invoice reads and retained imported originals: tasks 2.6 and 2.9 are complete and their
scenario tests are active. Task 2.4 remains open for real link decisions in group 8; the settings/run scenario awaits
group 6. The accountant policy probe still lives only in the test host. Ingestion endpoints and M0004 tenant tables
are covered by the route/table guards; no placeholder link or reconciliation endpoints ship.
