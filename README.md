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
worker (health endpoint on 3001 inside the Compose network only) and the SAT SOAP stub (8081, internal only).
Only API 8080 and PostgreSQL 5432 are published. Both bind to 127.0.0.1 by default;
`API_BIND` can override the API bind address, while PostgreSQL stays bound to 127.0.0.1.

## Demo on two laptops

Laptop A runs the Docker Compose stack; laptop B is a browser client on the same LAN.
The API serves the Blazor WebAssembly UI, which calls the API on the same origin.
No client executable or UI configuration is needed on laptop B.

On laptop A, install Docker Compose (with `up --wait` support), Bash, `ip` (iproute2),
and OpenSSL, then run from the repository root:

```bash
./scripts/demo-lan.sh
```

Open the printed `http://<LAN_IP>:8080` URL in a browser on laptop B to use the UI
as an admin or user with the appropriate account. The script also prints the health
URL (`http://<LAN_IP>:8080/health`). It detects the primary IPv4 address using the
host route; if a VPN or multiple interfaces select the wrong address, run
`LAN_IP=192.168.1.20 ./scripts/demo-lan.sh` with laptop A's LAN address.

The launcher exports `API_BIND=0.0.0.0` and sets `Session__AllowedOrigins__0` to the
printed browser origin. PostgreSQL stays private on `127.0.0.1:5432`; the worker and
SAT stub remain internal to Compose. Only the API is exposed to the LAN.

Plain HTTP is acceptable for this demo on a trusted LAN with synthetic data, but
traffic and credentials are unencrypted. Keep `Session__BrowserRefreshCookie=false`
(the default): the browser refresh-cookie flow requires HTTPS, and its allowed
origin setting must match the browser origin exactly. Use HTTPS for deployment.

If firewalld blocks access, laptop A may need `sudo firewall-cmd --add-port=8080/tcp`.
The script prints this as a note and never runs sudo. Both laptops must be able to
reach each other; guest Wi-Fi client isolation can prevent access.

When `JWT_SIGNING_KEY` is unset, the script generates a key once and reuses it from
ignored `.env.demo` (permissions `600`). It never prints secrets. An exported
`JWT_SIGNING_KEY` takes precedence. Keep `.env.demo` private and retain it between
runs; the existing Compose default for `APP_DB_PASSWORD` is unchanged.

Stop the demo on laptop A with:

```bash
./scripts/demo-lan.sh down
```

Stopping preserves the database volumes and `.env.demo` for the next demo.

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

The hash-chained audit log records company creation, settings, membership changes and archival atomically.
See the audit API below. Real viewer/accountant decision scenarios now pass, completing 2.4. Invoice isolation
and delete-retention checks also pass (2.6 and 2.9).
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
these writes. The reconciliation handler executes the matching pipeline described below. The SAT status handler runs short tenant
read/write transactions around a connection-free HTTP phase, with caching, bounded retry and lease fencing (ADR-008).

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

## Audit API

Every company member (viewer, accountant, administrator) can read audit entries, including archived companies.
The spec's auditor is the viewer role. Authenticate with Bearer and a matching X-Company-Id; absent and inaccessible
companies produce the same empty 404. These routes expose no mutation methods.

- `GET /companies/{companyId}/audit-entries` accepts inclusive UTC `from` / `to`, `actor` (user UUID or `system`),
  `action`, `item` (any typed reference id), optional `itemKind`, `afterSeq` and `limit` (default 50, range 1–200).
  It returns `{entries, nextAfterSeq}` in ascending `seq` order. Send `nextAfterSeq` as the next request's `afterSeq`;
  null means no further page at that moment. Entries include occurredAt, actorType, actor, action, items, detail and hashes.
- `GET /companies/{companyId}/audit/verification` returns `{consistent, entries, firstInconsistentSeq, reason}`.
  entries is the total on success or the verified prefix count on inconsistency. Verification streams O(n) entries.

The following examples use synthetic identifiers and a token already obtained from the login flow:

```bash
COMPANY_ID=00000000-0000-0000-0000-000000000001
SYNTHETIC_CFDI_UUID=00000000-0000-0000-0000-000000000002
curl -fsS --get "http://127.0.0.1:8080/companies/$COMPANY_ID/audit-entries" \
  -H "Authorization: Bearer $ACCESS_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  --data-urlencode "item=$SYNTHETIC_CFDI_UUID" --data-urlencode 'itemKind=cfdi' \
  --data-urlencode 'from=2026-01-01T00:00:00Z' --data-urlencode 'limit=50' --data-urlencode 'afterSeq=0'
curl -fsS "http://127.0.0.1:8080/companies/$COMPANY_ID/audit/verification" \
  -H "Authorization: Bearer $ACCESS_TOKEN" -H "X-Company-Id: $COMPANY_ID"
```

Each audit append shares its action's transaction and locks only that company's chain head. Stored canonical bytes
make the SHA-256 chain independently checkable; see [ADR-006](docs/adr/ADR-006-append-only-audit-log.md) for the fixed
vector, restricted function owner, downgrade behavior and limits. The internal refusal-recording seam uses a separate
transaction after a refused mutation rolls back; there is no audit-edit API. Session events stay in auth_event.
Item containment narrows by company index and then filters O(n) company entries: PostgreSQL FORCE RLS prevents
runtime use of the proposed non-LEAKPROOF JSON GIN predicate, so the unused GIN index is not installed. Opt-in
`RUN_AUDIT_BENCHMARKS=1` measures 20k/100k entry lookup and verification using the SDK container (add
`-e RUN_AUDIT_BENCHMARKS=1` to the Docker example and `--filter FullyQualifiedName~AuditBenchmarks` to dotnet test).
Task 5.3 is complete: user link decisions, imports, matching, classification and SAT changes are audited. Member removal is not implemented.

## Matching core and movement classification

`POST /companies/{companyId}/reconciliation-runs` runs matching 6A/6B in the worker. It returns 202 with a jobId, or 200
with the existing active run for that company. Poll the job until succeeded before reading derived movement states.
Only exact issued payment-complement evidence auto-reconciles. Other eligible evidence creates a suggestion with
criteria, confidence and explanation; high confidence never confirms a heuristic. A gross target adds both federal and
local retained taxes to net payable after valid credit notes; its persisted reason/audit lists the two components separately. In the synthetic worked example,
invoice **11600** receives two **5800** payments: the first reconciles with its complement, the second is suggested from
the remaining payable. [ADR-007](docs/adr/ADR-007-explainable-rule-pipeline.md) fixes the rubric and bounded-index design.

All member roles can `GET /companies/{companyId}/movements?state=pending` (also suggested/reconciled) and optionally filter
`classification=loan|partner_contribution|tax_payment|late_invoicing|other`. Pages return `movements`, `nextAfterDate` and
`nextAfterId`; use both `afterDate` and `afterId` from the preceding page. Order is ascending movement date then id,
limit defaults to 50 and must be 1–200. A movement exposes its derived state, classification/note, reason,
reconciledAmount, suggestedAmount, pendingAmount and uncoveredAmount. pendingAmount subtracts confirmed amounts;
uncoveredAmount also subtracts suggestions. The reference-only net-retention example covers 1939.27 as a suggestion,
so uncoveredAmount is zero while pendingAmount remains 1939.27 until confirmation or a complement.

Accountants and administrators can `PATCH /companies/{companyId}/movements/{movementId}` with an explicit classification
and optional note (maximum 2000 characters). Classification excludes the movement from the pending list and matching;
its state remains derived. Explicit null clears classification/note and reopens matching. Every actual change is audited
as movement.classification_changed in the same transaction. Archived companies remain readable but reject these writes.
Company settings reject amountTolerance above **1.00**; changing a tolerance affects the next run and keeps existing
suggestions. User-origin decisions/rejections remain protected. Later deterministic evidence can upgrade an automatic
suggestion; link.upgraded/link.closed entries retain its previous values in the audit chain.

Synthetic curl examples, after logging in and importing synthetic data (TOKEN, COMPANY and MOVEMENT_ID come from your
local API responses; JOB_ID is returned by the run request):

```bash
curl -sS -X POST "http://localhost:8080/companies/$COMPANY/reconciliation-runs" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "http://localhost:8080/companies/$COMPANY/jobs/$JOB_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "http://localhost:8080/companies/$COMPANY/movements?state=pending&limit=50" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS -X PATCH "http://localhost:8080/companies/$COMPANY/movements/$MOVEMENT_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY" -H 'Content-Type: application/json' \
  -d '{"classification":"loan","note":"Synthetic partner loan"}'
curl -sS "http://localhost:8080/companies/$COMPANY/movements?classification=loan&limit=50" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS -X PATCH "http://localhost:8080/companies/$COMPANY/movements/$MOVEMENT_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY" -H 'Content-Type: application/json' \
  -d '{"classification":null}'
# Replace both cursor values with nextAfterDate/nextAfterId from the preceding response.
curl -sS "http://localhost:8080/companies/$COMPANY/movements?limit=50&afterDate=2026-10-10&afterId=$MOVEMENT_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
```

The pure-engine benchmark is opt-in: pass `-e RUN_BENCHMARKS=1` to the README SDK container and filter tests with
`--filter 'FullyQualifiedName~MatchingBenchmarks'`. It measures 20,000 unique amounts and dense 5,000-by-5,000 inputs;
PR-9 in the build log records warmup, samples and medians. CI runs capacity/idempotency/rejection/order properties and a
small-K brute-force oracle without an environment flag. PR-10 records the 6B before/after medians. User link decisions
are implemented in 8A; standalone manual N:M creation and reopen remain future work. FX stretch 6.11 is deferred until document-level exchange data is available.

## Matching fiscal reads and cancellation review

All member roles can read `GET /companies/{companyId}/cfdi/{invoiceId}`. An income CFDI includes a `matching` object
with derived amounts and review information; other CFDI types return `matching: null`. Reads remain available for
archived companies and use the same membership/not-found and tenant guards as other company routes.

| Field | Meaning |
|---|---|
| `creditNotes`, `payable` | Valid issued same-currency code-01 note totals and max(original total minus notes, 0) |
| `state`, `reconciledAmount`, `suggestedAmount`, `paidPercentage` | Derived coverage; cancellation-marked links count in neither monetary sum |
| `pendingAmount`, `uncoveredAmount` | Net payable minus confirmed coverage; then minus valid suggestions, both floored at zero |
| `reasons`, `requiresReview` | Array of `{code, requiresReview}` plus aggregate problem flag |
| `complementDueDate`, `complementOverdue` | Earliest missing PPD payment deadline and whether injected UTC today is later |
| `satStatus`, `cancellationStatus` | Stored status and optional informational cancellation process text |

The 11,600 invoice with a 1,160 credit note has payable 10,440; an explicitly complemented 10,440 payment reconciles it.
A fully credited invoice stays pending with zero due and informational `fully credited`. Global invoices receive no
automatic proposals and keep informational `global invoice`, including when manual links reconcile them. Overpaid,
cancelled invoice/payment complement/credit note, credit note in a different currency and complement overdue are problem
reasons. Queue consumers should use `requiresReview`; pending-list consumers should use `pendingAmount > 0`.

PPD deadlines are the fifth of the next declared payment month, optionally moved from a weekend to Monday. Holidays
are not modeled; UTC today can differ from the Mexican local calendar near midnight. PUE has no deadline. Coverage
requires both link references, a valid type-P complement/payment row and an invoice document; an unlinked complement
or complement id without payment_ref does not cover a payment. A payment_ref is claimed by the movement of its earliest
link (created_at, then id); all that movement's valid invoice allocations are covered, but another movement cannot reuse it.

The domain operation `SatStatusChanges.ApplySatStatusChangeAsync` accepts the caller's company connection/transaction,
CFDI id, SAT status, cancellation status, injected time and cancellation token. The status_check job calls it inside
its fenced write transaction; clients queue a consultation instead of setting status directly. Cancellation in process keeps links. Cancelled evidence
moves active links to review, preserves provenance and releases capacity while holding ordinary matching for that
movement. Direct relation-04 replacements for the same customer/currency may be suggested, never confirmed. The worker
also discovers replacements imported later. Transitive replacements and foreign exchange conversion are not implemented.
Gross after a credit note keeps original retentions without prorating, a documented approximation in ADR-007.

Synthetic read examples (INVOICE_ID comes from the synthetic import report):

```bash
curl -sS "http://localhost:8080/companies/$COMPANY/cfdi/$INVOICE_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "http://localhost:8080/companies/$COMPANY/cfdi/$INVOICE_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY" | jq '.matching | {payable, pendingAmount, reasons, requiresReview, complementDueDate, complementOverdue}'
```

M0010 is reversible on populated databases. Stop API/workers for a rollback: Down removes free-text cancellation_status
and review_reason metadata while preserving documents, links and audit evidence; Up reconstructs suggested-link
cancellation markers from retained SAT status. FX 6.11 stays unticked: TipoCambioP alone lacks document-level EquivalenciaDR
and the exact dual-currency multi-document allocation contract.

## SAT status checks (offline first)

`POST /companies/{companyId}/cfdi/{uuid}/sat-check` returns `202 {jobId}` for accountants/administrators. The path uses
the CFDI UUID, while the existing read `/cfdi/{id}` uses its database id. Viewers cannot enqueue checks, unknown or
foreign UUIDs return the usual company-safe not-found behavior, and archived companies refuse writes. Newly imported
CFDI enqueue checks in the same per-file transaction; duplicate UUID imports do not. Matching never waits for SAT.

Synthetic examples (use the token/company from the tenancy example and the UUID/id from an import):

```bash
curl -sS -X POST "http://localhost:8080/companies/$COMPANY/cfdi/$CFDI_UUID/sat-check" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "http://localhost:8080/companies/$COMPANY/jobs/$JOB_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "http://localhost:8080/companies/$COMPANY/cfdi/$INVOICE_ID" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY" | jq '.sat'
# Optional key: administrator only; use a different synthetic UUID when recording.
curl -sS -X POST "http://localhost:8080/companies/$COMPANY/cfdi/$CFDI_UUID/sat-check" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY" -H 'Content-Type: application/json' \
  -d '{"anonymizedUuid":"00000000-0000-4000-8000-000000000751"}'
```

The `.sat` read exposes state, cancellability, cancellationStatus, efosRaw, checkedAt, expiresAt, lastError, attempts,
source and raw mapped fields. Legacy/unobserved rows have no check time. EFOS warnings require an actual successful
answer and a nonempty raw value other than `200`; NULL/empty does not warn. In-process/deadline-expired cancellation
statuses keep the CFDI current. Cancelled status invokes the 6B cascade and adds review reasons; repeated identical
answers refresh the cache without audit noise. `cfdi_review_reasons` adds `{code:"efos warning",requiresReview:true}`.

Configuration is validated at startup. Compose maps the `SAT_*` entries in `.env.example` to `Sat:*`; for `dotnet run`
use the matching `Sat__*` environment names. Mode and endpoint come only from trusted configuration.

| Setting | Default / meaning |
|---|---|
| `SAT_MODE` / `Sat:Mode` | `stub`; `real` and `replay` use identical shared status policy |
| `SAT_ENDPOINT` / `Sat:Endpoint` | empty selects the internal stub, or the documented public HTTPS endpoint in real mode |
| `SAT_CURRENT_CACHE_TTL` | `1.00:00:00` (24h); cancelled is permanent, expiresAt NULL |
| `SAT_NOT_FOUND_CACHE_TTL` | `01:00:00` (1h); unknown is recheckable |
| `SAT_REQUEST_TIMEOUT` / `SAT_MAX_ATTEMPTS` | `00:00:10` / `3`; bounded backoff+jitter on timeouts/connections/5xx |
| `SAT_MAX_REQUESTS_PER_SECOND` | `2`, per process; no burst after clock jumps |
| `SAT_JOB_BUDGET` / `SAT_MAX_FOLLOW_UP_GENERATIONS` | `00:01:00` / `20`; at most 100 ids per job |
| `SAT_RECORD_RESPONSES` | `false`; only stub/real can record |
| `Sat:RecordingDirectory` | `.local/sat-recordings`; Compose persists it in the `sat-recordings` named volume |

Budget-only remainder gets one atomic follow-up and the parent succeeds. Real transient failures commit unknown/error
metadata and retry the original job with normal bounded backoff; cached successes are skipped. A permanent 601 records
a per-CFDI error without changing its status or failing/retrying that job; a new explicit check can try again. No database
connection is held during SOAP calls, and a stale lease cannot commit results. FIFO ties use enqueue seq; expired-lease
recovery preserves the original due time, while transient retry scheduling remains unchanged.

Stub controls are local-development-only, available from the Compose network, never host-published:

```bash
docker compose exec -T worker wget -qO- --header='Content-Type: application/json' \
  --post-data='{"uuid":"00000000-0000-4000-8000-000000000701","outcome":"cancelled"}' \
  http://sat-stub:8081/control/outcomes
```

Outcomes: current, cancelled, not_found, invalid_expression, efos_flagged, cancel_in_process, deadline_expired, timeout,
server_error, slow (optional delayMs). DELETE /control/outcomes resets overrides; GET /health checks readiness. A forced
outcome does not bypass a valid cache: use a new synthetic document or wait for its configured TTL.

Replay uses the optional anonymizedUuid, otherwise the imported anonymous UUID itself; it makes no HTTP call and
missing recordings become unavailable/unknown. For a synthetic demo, copy the sample recording into the shared volume
and select `SAT_MODE=replay` before restarting API/worker. Automatic import checks find it when the imported anonymous
CFDI UUID is `00000000-0000-4000-8000-000000000751`:

```bash
docker compose cp samples/sat-recordings/00000000-0000-4000-8000-000000000751.json \
  worker:/app/.local/sat-recordings/00000000-0000-4000-8000-000000000751.json
```

Recording requires an explicit different anonymous key; automatic checks without one still work and skip recording
with one sanitized warning per process. Files contain only response fields/check time. The privacy guard rejects echoed
UUIDs, RFCs or formatted totals; explicit RecordAsync throws, automatic recording skips. Never commit real recordings;
`.local` is git-ignored and excluded from Docker builds. Real-mode recording emits a startup warning. Group 9 supplies
the real-to-anonymised mapping. Recording off discards keys; replay allows equality because it writes nothing.

See [ADR-008](docs/adr/ADR-008-sat-client-port-and-adapters.md) for the single mapping table, exact printed expression,
three-phase transactions, privacy and alternatives. **Phase 2 / task 7.6 remains unticked:** public SOAP configuration is
implemented but unvalidated against a real anonymised payment CFDI/live SAT response. Padded totals, exact unknown
spellings, empty EFOS and payment-CFDI consultation need that evidence. Tests/CI make no real SAT call. M0011 Down
removes cache observations and seq, restores the old queue function, and preserves CFDI/link/audit data; stop API/worker
during rollback. Reapplying backfills only cancelled CFDI as permanent `source=backfill`, explicitly not a fresh answer.

## Review API and summaries (8A)

Any company member may read the review queue, invoice list/summary and overview. Accountants and administrators
may decide on suggested links; archived companies remain read-only. These examples use synthetic ids and a token
from the tenancy API. `BASE`, `COMPANY`, `LINK` and `INVOICE` refer to your disposable demo installation.

```bash
BASE=http://localhost:8080
# Inspect confidence/criteria/problem reasons, then follow all three nextAfter* cursor fields for the next page.
curl -sS "$BASE/companies/$COMPANY/review-queue?limit=50" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"

# Confirm one suggestion. Other types are reject and change; change accepts newCfdiId and/or amountApplied.
curl -sS -X POST "$BASE/companies/$COMPANY/links/$LINK/decisions" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY" -H 'Content-Type: application/json' \
  -d '{"type":"confirm","note":"Synthetic demo confirmation"}'

curl -sS "$BASE/companies/$COMPANY/cfdi/$INVOICE/summary" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "$BASE/companies/$COMPANY/cfdi?type=I&direction=issued&complementStatus=overdue&limit=50" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS "$BASE/companies/$COMPANY/overview" \
  -H "Authorization: Bearer $TOKEN" -H "X-Company-Id: $COMPANY"
curl -sS -D - "$BASE/openapi.json" # anonymous; info.version and x-api-version are 1.0.0
```

The decision body is `{type,newCfdiId?,amountApplied?,paymentCfdiId?,paymentRef?,note?}`. Supply both payment references
or neither; a complement must be non-cancelled type P with a payment document referencing the target invoice, and
its payment row cannot already belong to another reconciled movement. Notes are limited to 500 characters and stay
in decision detail. The response returns one decision and every affected link with recomputed states. A competing
or repeated decision receives 409 with the current link state. Confirm/reject add one audit entry; change records
old closure and new creation as two entries with one decision row. Change may exceed invoice payable (overpaid
reason), while movement capacity remains strict. Reopen and standalone manual N:M creation remain future work.

Summary fields include `total`, `creditNotes`, `netPayable`, `paid`, `paidPercentage`, `pending`, `uncovered`,
`state`, direction/payment method/SAT/cancellation/EFOS, typed reasons and active `links`. Each link includes its
movement brief, applied amount/state/confidence, payment CFDI/ref, complement status/due date and cash warning.
An invoice of 11,600.00 with confirmed 5,800.00 and suggested 5,800.00 shows 50% paid, 5,800.00 pending and zero
uncovered, with both movements. Only confirmed PPD payments generate missing-complement due dates. The missing
filter includes overdue unreceived complements; overdue narrows to passed deadlines. List cursors use
`afterIssuedAt` and `afterId`; queue cursors use `afterRank`, `afterDate`, `afterId`. Supply complete cursors;
maximum page size is 200. Concurrent writes can remove entries between pages. Pending invoices require
pendingAmount > 0; informational fully credited/global reasons alone do not enter the review queue.

Overview state counts/amounts add to imported type I invoice totals and all movement totals. Its totals note
includes classified movements and zero-payable invoices in their derived states; amounts use imported gross
invoice totals and credit plus debit. Alerts count complement overdue, cancellation in process, EFOS and global.
The overview and OpenAPI repeat: results have no fiscal or legal validity. No UI is included in 8A; task 8.5 is 8B.
The current paths remain unchanged; a breaking future contract introduces `/v2` alongside the old API.

M0012 uses active-only null-safe pair uniqueness to retain changed-link history. Down refuses if decisions exist
or historical duplicate pairs prevent restoring the old key. Stop API/workers and explicitly export/clean up
such data before an operator downgrade; Down never alters audit history. See [ADR-009](docs/adr/ADR-009-review-decisions-and-read-model.md).
The opt-in `RUN_REVIEW_BENCHMARKS=1` test measures real queue/overview requests at 20k invoices/20k movements and
writes `review-benchmark.json` beside test binaries, including actual SQL EXPLAIN ANALYZE as cfdi_app with RLS.

Browser sessions can opt in without changing JSON clients: set `Session__BrowserRefreshCookie=true`
and `Session__AllowedOrigins__0` to the exact browser origin (for local Compose, `http://localhost:8080`).
The Web UI sends `X-Refresh-Transport: cookie` for login, refresh and logout. Each requires an allowed
Origin, returns no JSON refresh token, and uses the existing rotation service. Access tokens stay
in memory; reload silently renews with the HttpOnly, SameSite=Strict, Path=/auth refresh cookie.
Legacy `Session__RefreshCookie=true` wins globally; leave it false to preserve JSON clients.
Cookies are Secure on HTTPS. Plain HTTP localhost is a local development convenience; production
must use HTTPS. The browser adds Origin automatically; scripts using cookie transport must supply it.

## Web UI

The API container serves the standalone Blazor WebAssembly app at **http://localhost:8080** (or the
free port selected in your Compose override). The shell is anonymous; every company data call uses the
same documented API with a bearer token. It contains no tenant data or secrets. The UI is Spanish (Mexico) by default, with an English switch.
Results have no fiscal or legal validity; the notice appears on login, overview and the footer.

Enable the browser transport in your environment before starting the stack:

```bash
export Session__BrowserRefreshCookie=true
export Session__AllowedOrigins__0=http://localhost:8080
# Set JWT_SIGNING_KEY through the environment, as in the Compose instructions above.
docker compose up --build --wait
# When finished:
docker compose down -v
```

Use an existing member's email/password to sign in. Bootstrap the first operator and create a company/member
through the documented API as described above; an operator with no memberships sees an empty company picker.
Select a company; the header shows its name and role. Import CFDI XML, switch the import type for a bank CSV,
then open Overview and run reconciliation. Polling shows progress until success/failure and the import page
shows its per-file report. Open Review queue to confirm/reject/change a suggestion. Invoice list filters
include state and missing/overdue complements; summaries show confirmed paid percentage, pending, payment
CFDI, due dates, warnings and reasons. Movements supports classification for accountants/administrators.
Viewers and archived companies are read-only. There is no administration, audit or SAT-management page.

Access tokens stay **in memory**; never localStorage, sessionStorage or URLs. Reload silently renews through
an HttpOnly, SameSite=Strict, Path=/auth cookie, then asks for company selection. Browser auth calls send
`X-Refresh-Transport: cookie`; Origin must exactly match the allow-list, including scheme and port.
Use localhost consistently. Production requires HTTPS; local HTTP is only a development convenience.
Legacy `Session__RefreshCookie=true` still wins globally; keep it false to retain JSON-token API clients.
A 401 gets one coordinated refresh/retry; failure returns to login. Separate tabs do not share access tokens.

The UI sends `Accept: application/json, application/problem+json`. On errors the API then returns RFC 7807
problem details; requests with no Accept or `*/*` retain legacy bodies byte-for-byte. OpenAPI documents both
media types for 400/401/403/404/409. Request/response schemas are generated with the existing framework;
`Reconciliation.Contracts` contains DTOs only and the typed client uses source-generated JSON metadata.

The shell has a strict Content-Security-Policy, nosniff and no-referrer. index.html is no-cache; only
fingerprinted framework assets are immutable. Data APIs and `/auth`, `/openapi.json`, `/health` cannot become
an HTML fallback. No UI-only data endpoint or database access is added. GET /companies lists only the caller's
memberships through M0013's restricted directory function (see ADR-010).

Browser upload limits match default ingestion bounds: 20 files, 10 MiB each, 20 MiB total; API configuration
can be stricter. The solution and CI build/test the Web app; Docker publishes it into the API's wwwroot.
For the UI use the published Compose image; running only the API project from source does not publish WASM.
The payload is trimmed without AOT/workload installation. Polling backs off from 1 to 5 seconds; SSE is deferred.

The in-process worked-example test uses the same ApiClient/view-models as the pages. A repeatable real-browser
check is `tools/ui_smoke.mjs`, using installed `/usr/bin/google-chrome` and Node's built-in WebSocket:

```bash
# Disposable stack only, synthetic operator credentials through environment; never CI or real data.
export UI_SMOKE_DISPOSABLE=1
export UI_SMOKE_URL=http://localhost:8080
export UI_SMOKE_OPERATOR_EMAIL=synthetic-ui-operator@example.invalid
# Set UI_SMOKE_OPERATOR_PASSWORD to that disposable operator's synthetic password in your environment.
node tools/ui_smoke.mjs
```

It creates a synthetic company via the API and drives browser login, company selection, both imports, run,
queue, one confirm and the 50% / 5,800.00-pending summary. It checks reload renewal, light/dark layouts and
anonymous shell/authenticated data boundaries, then removes its Chrome process/profile. The caller must tear
down the disposable stack with `docker compose down -v`. Three synthetic 1440×900 screenshots are under
`docs/build-log/images/pr13-*.png`. No Playwright/Selenium dependency is used.
