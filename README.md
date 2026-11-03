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
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --solution Reconciliation.slnx
```

## Tenancy API

`POST /auth/tokens` accepts `{email, password}` and returns `{accessToken, refreshToken, expiresIn}`. Send that token as
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

## Session renewal

Access tokens have a configurable **15-minute** default lifetime (5–30 minutes). Refresh credentials contain 256 random
bits encoded as base64url; PostgreSQL stores only their SHA-256 digest. JWTs carry `sub`, `sid` and `jti`; roles stay in
live company membership. Existing Bearer tokens without `sid` keep working on existing routes until their original
expiry; log in again to obtain a refresh credential.

| Request | Body / authentication | Result |
|---|---|---|
| `POST /auth/tokens` | `{email, password}` | `{accessToken, refreshToken, expiresIn}`; default `expiresIn` is 900 seconds |
| `POST /auth/refresh` | `{refreshToken}`; no access token required | A new access/refresh pair in the same session |
| `POST /auth/logout` | Bearer access token with `sid`; no body | 204; only that session stops renewing |
| `POST /auth/password` | Bearer access token; `{currentPassword, newPassword}` | 204; password changes and every session of that user stops renewing; new password must have at least 12 characters |

Rotation is transactional and locks the session row. Reusing a rotated credential through the inclusive 10-second
grace window returns **409** without changes. Later reuse returns 401 and revokes the whole family. Coordinate renewal
with **single-flight** behavior: on 409, retry using the token stored by the winning request, never repeatedly replay
the old one. Unknown, revoked or expired credentials return 401. Renewal never extends the original absolute cap.
Defaults are 8 hours idle and 30 days absolute. Logout and password change end renewal immediately; already-issued
access tokens remain valid until their configured expiry. Session events are append-only and contain no credentials.

The following CLI flow uses an existing synthetic account. Supply `AUTH_EMAIL` and `AUTH_PASSWORD` privately in the
shell environment first. Responses and credentials stay in shell memory; do not print them, put them in URLs or save
them to files. The last command logs this session out.

<!-- session-cli-start -->
```bash
set -euo pipefail
: "${AUTH_EMAIL:?Set AUTH_EMAIL for an existing synthetic account}"
: "${AUTH_PASSWORD:?Set AUTH_PASSWORD privately}"
API_URL="${API_URL:-http://127.0.0.1:8080}"
SESSION_LOGIN_JSON="$(python3 -c 'import json,os; print(json.dumps({"email":os.environ["AUTH_EMAIL"],"password":os.environ["AUTH_PASSWORD"]}))' | curl -fsS "$API_URL/auth/tokens" -H 'Content-Type: application/json' --data-binary @-)"
ACCESS_TOKEN="$(printf '%s' "$SESSION_LOGIN_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["accessToken"])')"
REFRESH_TOKEN="$(printf '%s' "$SESSION_LOGIN_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["refreshToken"])')"
SESSION_RENEWED_JSON="$(printf '{"refreshToken":"%s"}' "$REFRESH_TOKEN" | curl -fsS "$API_URL/auth/refresh" -H 'Content-Type: application/json' --data-binary @-)"
ACCESS_TOKEN="$(printf '%s' "$SESSION_RENEWED_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["accessToken"])')"
curl -fsS -X POST "$API_URL/auth/logout" -H "Authorization: Bearer $ACCESS_TOKEN"
unset SESSION_LOGIN_JSON SESSION_RENEWED_JSON ACCESS_TOKEN REFRESH_TOKEN
```
<!-- session-cli-end -->

Configure `Session__AccessTokenMinutes`, `Session__IdleMinutes`, `Session__AbsoluteDays`, `Session__ReuseGraceSeconds`
and `RefreshRateLimit__IpPermitLimit` (default 60 requests per `LoginRateLimit__WindowSeconds`, shared by refresh and
logout per remote IP). Invalid session options fail startup. Shared NATs share that IP budget. The password endpoint
requires authentication and the current password; the design's session IP policy applies to refresh and logout.

For browser transport set `Session__RefreshCookie=true` and an Origin allow-list, for example
`Session__AllowedOrigins__0=https://app.example.invalid`. Login and renewal then omit `refreshToken` from JSON and
send a **HttpOnly**, **Secure**, **SameSite=Strict** browser-session cookie with **Path=/auth**. Cookie renewal requires
an exact allowed `Origin`; missing or unlisted Origins receive 403 without rotation. A body credential cannot bypass
cookie mode, and logout clears the cookie. Use HTTPS for browser cookies; the local HTTP Compose CLI demo uses the
default JSON transport. Every data endpoint continues to use Bearer authorization. See
[ADR-004](docs/adr/ADR-004-session-renewal.md) for the trade-offs and the production OIDC recommendation.

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

## Background jobs and asynchronous imports

`POST /companies/{companyId}/imports/cfdi` and `/imports/bank-statement` stream and size-check the multipart request,
store queued files and their job in one tenant transaction, and return **202** with `{jobId, uploadId}` and a Location
header. They do not wait for XML/CSV parsing. Poll `GET /companies/{companyId}/jobs/{jobId}` for
`state` (`queued`, `running`, `succeeded`, `failed`), `progressDone`, `progressTotal` and `history`. Then read the existing
`GET /companies/{companyId}/imports/{uploadId}` report: one result per file, including queued/processing and final
imported/already_imported/rejected statuses. One hostile, oversized or invalid file is rejected independently; aggregate
upload/count limits still reject the entire request before anything is enqueued. Pending bytes are never downloadable.

Viewers can read jobs and reports. Accountants and administrators can import and `POST
/companies/{companyId}/reconciliation-runs`. That route returns a new job with 202, or the existing queued/running
job with 200. Only administrators can `GET /companies/{companyId}/jobs?status=failed` or `POST
/companies/{companyId}/jobs/{jobId}/retry`; retry is audited and retains history/input. Archived companies refuse
these writes. The reconciliation/status handlers explicitly record `not implemented until group 6` / group 7 and
succeed without matching or calling SAT; their registered presence is not a claim those later features exist.

The worker uses PostgreSQL SKIP LOCKED claims, heartbeat leases and capped exponential backoff with jitter. Each
handler uses CompanyScope; cfdi_app still cannot bypass RLS. The queue-only NOLOGIN owner and SECURITY DEFINER
functions are explained in [ADR-005](docs/adr/ADR-005-postgresql-job-queue.md), along with the conservative
100 jobs/minute review budget and outbox migration path. Expired claims consume attempts; failed jobs retain queued
pending bytes until an administrator reruns them successfully. There is no cleanup/delete path. Migration Down refuses
to discard pending queued/processing input; finish or rerun that input first.

Configuration section `Jobs` uses `Jobs__PollIntervalMilliseconds=500`, `Jobs__Concurrency=2`, `Jobs__LeaseSeconds=60`,
`Jobs__MaxAttempts=5`, `Jobs__BackoffBaseSeconds=2`, `Jobs__BackoffCapSeconds=300`. Invalid bounds fail startup.
Compose maps the corresponding `JOBS_*` variables from `.env.example`; `JOBS_MAX_ATTEMPTS` also configures API enqueue.
Structured worker logs include jobId, companyId and type. Health remains on port 3001 inside Compose.

For a manual recovery demonstration, upload synthetic input, observe the job as running, execute
`docker compose kill worker`, then `docker compose up -d worker`. After the lease expires a replacement claims it;
verify a succeeded job with lease_expired history and one set of import effects. The controlled demo and observed
counts are recorded in both PR-7 build logs. Use the SDK container command above for all development checks when no
host SDK is installed.
