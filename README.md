# CFDI and bank statement reconciliation platform

Portfolio demo (MVP core). A multi-company backend that ingests CFDI 4.0 XML and bank statement CSV files,
proposes which bank movement pays which invoice, sends doubtful matches to human review and keeps a
tamper-evident audit log. Results have **no fiscal or legal validity**; use anonymised or synthetic data only.

Stack: C# on .NET 10, ASP.NET Core, PostgreSQL 17 (see `docs/adr/ADR-002-stack-choice.md`).
Planning documents: `docs/` (index in `docs/README.md`: build log, ADRs, requirements, references, diagrams) and `openspec/changes/mvp-reconciliation-core/` (source of truth).
Progress and what each piece is for: [`docs/build-log/BUILD-LOG.md`](docs/build-log/BUILD-LOG.md) (English) and [`docs/build-log/BUILD-LOG-ES.md`](docs/build-log/BUILD-LOG-ES.md) (Español); status map: `docs/diagrams/progress/status-map.png`.

## Layout

| Path                              | Contents                                                                  |
| --------------------------------- | ------------------------------------------------------------------------- |
| `src/Reconciliation.Core`         | The seven modules: Ingestion, Matching, Review, Audit, Tenancy, Sat, Jobs |
| `src/Reconciliation.Api`          | HTTP API host (ASP.NET Core minimal APIs)                                 |
| `src/Reconciliation.Worker`       | Background worker host (same library, separate process)                   |
| `src/Reconciliation.Migrator`     | Database migrations (FluentMigrator), `up` and `down`                     |
| `src/Reconciliation.SatStub`      | Local SAT status stub (its own container)                                 |
| `tests/Reconciliation.Tests`      | xUnit tests                                                               |
| `tools`                           | Test-data tooling (anonymiser, generators, leak scan)                     |
| `docs`                            | Documentation index: build log, ADRs, requirements, references, diagrams  |

## Run everything (Docker)

```bash
JWT_SIGNING_KEY="$(openssl rand -hex 32)" docker compose up --build --wait
docker compose ps                        # every service healthy
docker compose down                      # add -v to drop the database volume
```

Compose starts PostgreSQL, runs the migrations once, then starts the API (http://127.0.0.1:8080/health), the
worker (health endpoint on 3001 inside the Compose network only) and the SAT stub (8081). Published ports are
bound to 127.0.0.1.

## Develop locally

Install the .NET 10 SDK (Fedora: `sudo dnf install dotnet-sdk-10.0`) and start a database
(`docker compose up -d postgres`).

```bash
# Owner connection is only for migrations. These credentials are synthetic local defaults.
export ConnectionStrings__Default="Host=localhost;Port=5432;Username=cfdi;Password=cfdi;Database=cfdi"
export APP_DB_PASSWORD="synthetic-local-app-password"
# Optional: set OPERATOR_EMAIL and OPERATOR_PASSWORD before migrating to create the first operator.
dotnet build                                             # warnings are errors
dotnet format --verify-no-changes                        # style check
dotnet test --solution Reconciliation.slnx
dotnet run --project src/Reconciliation.Migrator -- up   # or `down` to revert one migration
export ConnectionStrings__Default="Host=localhost;Port=5432;Username=cfdi_app;Password=$APP_DB_PASSWORD;Database=cfdi"
export Jwt__SigningKey="$(openssl rand -hex 32)"
dotnet run --project src/Reconciliation.Api
```

No SDK on the machine? Run any command inside the official image, for example:

```bash
docker run --rm --network host -e TESTCONTAINERS_HOST_OVERRIDE=127.0.0.1 \
  -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD:$PWD" -w "$PWD" \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --solution Reconciliation.slnx --solution Reconciliation.slnx
```

## Tenancy API

`POST /auth/tokens` accepts `{email, password}` and returns `{accessToken}`. Send that token as
`Authorization: Bearer <token>` and choose the company with `X-Company-Id`. The header must match the route id
and a current membership. Health and `/openapi.json` remain anonymous.

A bootstrapped platform operator can `POST /companies` with `{rfc, name, adminEmail, adminPassword}`. The password
must have 12 characters for a new account; an existing account keeps its password. Any member can read
`GET /companies/{companyId}`, `/companies/{companyId}/members`, and `/companies/{companyId}/settings`. Administrators can post a
`{userId, role}` to the members route, patch settings, and `POST /companies/{companyId}/archive`. There are no DELETE routes.

The initial audit table records company creation, settings and archival atomically. Group 5 adds the full audit
contract. Three domain-dependent tenancy scenarios remain explicitly skipped; task 2.4 stays open. Real invoice isolation
and delete-retention checks now pass, completing 2.6 and 2.9.
Compose uses synthetic local credentials; replace them and use invite/reset links for a production deployment.
See [ADR-003](docs/adr/ADR-003-tenant-isolation-rls.md).

The implementation uses `CompanyScope` for company transactions, `CompanyRoleHandler` for membership policies,
`CompanyAdminEndpoints` for administration, and `ImportFileStore` for retained uploads. The bilingual build logs
include the PR-5 rename map so earlier code names remain traceable.

## Ingestion API

Accountants/administrators can upload `multipart/form-data` with file parts named `files` to
`POST /companies/{companyId}/imports/cfdi`. Bank statements use `POST /companies/{companyId}/imports/bank-statement`,
with `bank=reference` and an `accountReference` synthetic label (for example `Synthetic-1234`).
See [the CSV reference](docs/reference/bank-csv-reference-format.md) for the header and tested synthetic examples.

Uploads return `{uploadId, files}` with each file's `fileId`, `status`, `reason`, `cfdiId` where applicable,
and new/duplicate statement movement counts. Any member can read:

- `GET /companies/{companyId}/imports/{uploadId}` — persisted per-file report.
- `GET /companies/{companyId}/cfdi/{invoiceId}` — declared invoice header (invoiceId is the stored record id).
- `GET /companies/{companyId}/cfdi/{invoiceId}/xml` — unchanged original XML.
- `GET /companies/{companyId}/imports/files/{fileId}/original` — unchanged original file, including a statement.
- `GET /companies/{companyId}/banks` — supported parser identifiers.

Use the same bearer token and matching `X-Company-Id` header as the tenancy API. An invalid file gets a rejected
report without blocking valid files. Aggregate upload size/count breaches reject the request before persistence.
Defaults: 10 MiB per file, 20 MiB aggregate including multipart overhead, 20 files, 100000 XML nodes plus attributes, depth 64,
and ten years of original retention. Configure via `Ingestion__MaxFileBytes`, `Ingestion__MaxUploadBytes`,
`Ingestion__MaxFiles`, `Ingestion__MaxXmlNodes`, `Ingestion__MaxXmlDepth`, `Ingestion__RetentionYears` (minimum five).
Statements default to 100000 lines and 500 description characters, configurable through
`Ingestion__MaxStatementLines` and `Ingestion__MaxDescriptionLength`. Archived companies remain readable; imports,
settings and membership changes are refused. Downloads use attachment and nosniff headers.
Login uses fixed windows per normalized email (10/minute) and remote IP (30/minute), returning 429 with Retry-After.
Configure `LoginRateLimit__EmailPermitLimit`, `LoginRateLimit__IpPermitLimit` and `LoginRateLimit__WindowSeconds`.
The per-email limit can throttle a victim under attack; tune and monitor these defaults for deployment.
Imports are synchronous until group 4. No DELETE endpoint exists for imported records or originals.

## Decisions

Architecture decision records are in `docs/adr/`.
