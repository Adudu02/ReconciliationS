# Build log

A running record of what has been added to the project, what each piece is for, and what C# or tooling idea
it teaches. Read it top to bottom once, then update it with every pull request.

Versión en español: [BUILD-LOG-ES.md](BUILD-LOG-ES.md).

Source of truth for the work items: `openspec/changes/mvp-reconciliation-core/tasks.md` (85 tasks in 11 groups).
This log explains them; the status map below is generated from them.

## 1. Status at a glance

![Build status map](progress/status-map.png)

**26 of 85 tasks done. Group 3 is complete; group 2 has only 2.4 pending for real link decisions in group 8.**
Regenerate the picture after ticking tasks: `python3 tools/progress_diagram.py` (add `--lang es` for the Spanish map).

### What the types mean

| Type | What it is | Example here |
|---|---|---|
| **Host** | A program that keeps running and answers requests or does background work | API, worker, SAT stub |
| **Module** | A code area inside the shared library, owning one business responsibility | Matching, Audit |
| **Database** | Tables, rules and security that live inside PostgreSQL | `company`, row-level security |
| **Tool** | A program you run once to do a job, then it exits | Migrator, test-data generators |
| **Pipeline** | Automation that builds, tests or starts things | Docker Compose, CI |
| **Docs / Tests** | Written decisions, or code that checks the other code | ADRs, xUnit tests |

## 2. What exists today

| Piece | Type | What it is used for | Where |
|---|---|---|---|
| Solution file | Build | Groups the six projects so one `dotnet build` builds everything | `Reconciliation.slnx` |
| Shared build rules | Build | Every project gets the same settings: .NET 10, nullable checks on, warnings are errors | `Directory.Build.props`, `.editorconfig`, `global.json` |
| **Core library** | Module area | The code both hosts share. Holds the seven module boundaries and the common database and health code | `src/Reconciliation.Core` |
| **API host** | Host | The web API users will call. Now serves authentication, companies, members, settings, archival, health and OpenAPI | `src/Reconciliation.Api` |
| **Worker host** | Host | Runs background jobs (imports, matching, SAT checks). Today it starts, reports health and idles | `src/Reconciliation.Worker` |
| **Migrator** | Tool | Applies or reverts database changes (`up`, `down`) | `src/Reconciliation.Migrator` |
| **SAT stub** | Host | A fake of the SAT status service so development needs no real account. Today only a placeholder | `src/Reconciliation.SatStub` |
| **PostgreSQL 17** | Database | Stores all data. Identity and tenancy tables plus a minimal `audit_entry`, with forced tenant RLS | `docker-compose.yml`, migrations `M0001` to `M0004` |
| Docker Compose | Pipeline | Starts database, migrator, API, worker and stub with one command and waits until all are healthy | `docker-compose.yml`, `Dockerfile` |
| CI pipeline | Pipeline | On every push: format check, build, tests, migrations up/down/up, and a full stack start | `.github/workflows/ci.yml` |
| ADRs | Docs | Short records of big decisions and the alternatives rejected | `docs/adr/` |
| Requirements and plan | Docs | BRS, SRS (English and Spanish), diagrams, and the OpenSpec change that defines the work | `docs/`, `openspec/` |
| Tests | Tests | 104 xUnit cases: 101 passing, 3 explicitly pending; real PostgreSQL tenancy and ingestion tests | `tests/Reconciliation.Tests` |
| Progress map | Tool | Draws the status picture from `tasks.md` | `tools/progress_diagram.py` |

## 3. Pull request log

Newest first. Each entry says what changed, why, and what you can learn from it.

### PR-3: Domain model and ingestion (tasks 3.1–3.8, including 3.4b–3.4f)

**Purpose:** turn synthetic CFDI and reference bank CSV into isolated, exact, retained accounting records.
**How:** red-test commits precede implementation commits; all persistence scenarios use the real PostgreSQL
Testcontainers fixture and actual migrator. Parsers are also tested directly for malformed and hostile input.
**Verified:** build with zero warnings/errors, `dotnet test --solution Reconciliation.slnx` (101 passed, 3 skipped),
format verification, Compose build/start with all services healthy, and migrations up/down/up on a disposable
PostgreSQL database. Both progress maps were regenerated. No push was performed.

#### Model, exact money and RLS (3.2, 3.8)
- **Purpose:** keep rows isolated, amounts exact, and imports free from duplicates.
- **How:** M0004 adds `import_file`, `cfdi`, `cfdi_relation`, `cfdi_payment`, `cfdi_payment_doc`, `bank_account` and
  `bank_movement`. Each has company_id, forced RLS and a policy. Composite foreign keys prevent a child pointing
  to another tenant. UUID uniqueness is per company; movement uniqueness includes account, date, description,
  credit/debit and balance. Money uses decimal / numeric(18,2); exchange rates use numeric(20,10).
- **What I learned:** database constraints are the final guard for duplicate and cross-tenant relationships.
  Both C# and PostgreSQL sum 0.10 + 0.20 to exactly 0.30.

#### Safe parsing and declared fiscal values (3.3, 3.4, 3.4b–3.4e)
- **Purpose:** accept income, credit notes and payment complement 2.0 without reading external resources or
  silently changing the declared accounting values.
- **How:** XML prohibits DTDs and external resolution, with a bounded scan before building a document. Header
  totals are checked exactly, including discounts and federal/local transfers and retentions; taxes are not
  recomputed from rates. The synthetic retention sample imports at 1,939.27. Concept amounts with six decimals
  remain accepted and are not recomputed or separately stored. Header/payment money with more than two declared
  decimal places is rejected, even for trailing zeroes; the MVP treats other currencies as two decimals too.
  Unsupported complement versions are rejected. Each related line checks previous - paid = remaining; continuity
  is checked between consecutive installments present within the upload. Inconsistency is a stored review flag,
  never a rejection. Earlier missing installments and pre-imported invoices are not required. Rejected/unrelated
  files cannot influence valid files' continuity checks. SAT relation codes (including unknown ones), global
  information/generic RFC flags, issue and stamp times are retained. Unknown relation codes are stored for later
  interpretation; ingestion never applies them to payable amounts.
- **What I learned:** validation of declared values differs from recomputing fiscal rules. Local timestamps must
  have no timezone and at most six fractional digits; finer precision is rejected so PostgreSQL cannot silently
  change it. A deliberately inconsistent concept value is accepted when the declared header arithmetic is valid.

#### Reports, originals and upload limits (3.4f, 3.5)
- **Purpose:** explain each file's outcome and return accepted originals byte for byte.
- **How:** one import_file row per file shares an upload_id; there is no batch table. Each row stores status,
  reason and SHA-256; rejected files keep the hash but no bytes. Duplicate UUID reports refer to the original
  file and never replace its data. A transaction advisory lock makes concurrent UUID uploads idempotent, backed
  by the unique constraint. Accepted originals have a database immutability trigger and no DELETE privilege/path.
  RetentionYears defaults to 10, is validated at startup (minimum 5, conservative maximum 100), and sets retained_until.
- **Limits decided with Claude:** MaxFileBytes=10 MiB, MaxUploadBytes=20 MiB, MaxFiles=20, MaxXmlNodes=100000 and
  MaxXmlDepth=64. All are configurable under Ingestion. Aggregate byte/file-count limits reject the whole upload
  before persistence (413/400). A per-file size breach or invalid/hostile XML rejects only that file. Multipart
  parsing counts bytes while reading, including requests without Content-Length; oversized file bytes are drained
  into a hash without retaining the payload. Accepted buffering stays within the aggregate bound. The aggregate
  limit counts multipart overhead too. Request cancellation flows through reads and database operations.
- **What I learned:** a mixed upload needs a per-file transaction; a failed file must not roll back valid files.
  The full hash can be retained without keeping rejected content. Full audit hash chaining remains group 5 work.

#### Reference statements (3.1, 3.6, 3.7)
- **Purpose:** provide one documented bank format and a seam for later real-bank parsers.
- **How:** docs/bank-csv-reference-format.md defines the exact header, UTF-8, ISO dates, cent precision, quoting
  and synthetic examples; those examples run through IBankStatementParser. The platform CSV reader handles quoted
  commas and CRLF. Malformed lines reject the entire statement with a line number and create no partial account
  or movements. Unknown banks list the supported identifier, reference. bank=reference plus accountReference
  creates/reuses an account by company/bank/label; there is no account CRUD endpoint. accountReference is an ASCII
  label of 1–40 letters/digits/spaces/-/_, never a full real account number (use a suitable label/last four digits).
  RASTREO takes precedence over REF; absent tokens are null. Overlapping statements count new and duplicate lines,
  while equal lines with different balances remain distinct. Original statement bytes/hashes are retained too.
- **What I learned:** an existing platform CSV parser avoids hand-written quote handling. The conservative empty
  statement behavior is rejection; there must be at least one movement and exactly one positive credit/debit per line.

#### HTTP and closure of tenancy scenarios
- **Purpose:** expose real ingestion with accountant imports and viewer reads.
- **How:** all routes below require current membership and X-Company-Id; only administrators/accountants import:
  POST /companies/{id}/imports/cfdi and /imports/bank-statement; GET /companies/{id}/imports/{uploadId},
  /cfdi/{invoiceId}, /cfdi/{invoiceId}/xml, /imports/files/{fileId}/original and /banks. File parts are named files.
  Reports preserve per-file status, reason, resource ids and statement counts. The isolation guard discovers all
  routes, seeds real positive baselines, tries B's resource ids under A's valid context, and compares all B tenant
  tables before/after attempts. “Cross-company attempt in the test suite” now proves 404 for a real B invoice;
  “Delete attempt” proves 405 and unchanged invoice/original. Tasks 2.6 and 2.9 are therefore complete.
- **What I learned:** a 404 only proves isolation when a successful owner request first proves the resource exists.

**Still skipped:** “Viewer cannot decide” and “Different roles” need real links in group 8; “Tolerance changed”
needs a reconciliation run in group 6. Task 2.4 stays unticked. Imports are synchronous until group 4 jobs.

### PR-2: Tenancy and access (group 2; six tasks complete, three pending)

**Purpose:** isolate each company below the application layer, authenticate callers, and let administrators
manage company access and settings. All identifiers and credentials used for tests are synthetic.
**How:** each slice committed its red tests before implementation. Testcontainers runs the actual migrator
against PostgreSQL 17; the application connects as `cfdi_app`, without superuser or BYPASSRLS.
**Verified:** `dotnet build` (zero warnings/errors), `dotnet test --solution Reconciliation.slnx` (40 passed,
5 explicitly skipped), `dotnet format --verify-no-changes`, Compose healthy after build, and migrator up/down/up
on a separate disposable database. Full rollback to version zero and reapplication also succeeded.

#### Tenant transactions and forced RLS (2.1, 2.2)
- **Purpose:** a forgotten WHERE must not return or change another company's rows.
- **How:** `TenantContext.ExecuteAsync` sets a parameterized transaction-local company setting. RLS is enabled
  and forced on `company`, `membership` and the minimal `audit_entry`. Missing context yields no rows and is
  logged. API and worker use the restricted login; migrations use the owner.
- **What I learned:** `SET LOCAL` ends with the transaction, which matters when pooled connections are reused.
  RLS and authenticated membership checks solve different parts of access control; both are necessary.

#### Credentials and company roles (2.3; 2.4 remains pending)
- **Purpose:** identify callers and limit each action to the selected company's current role.
- **How:** Identity PasswordHasher stores salted PBKDF2 credentials; login issues a signed, expiring JWT.
  A fallback policy requires authentication. Only health, OpenAPI and login are anonymous. `X-Company-Id`
  must match the route and membership; policies query PostgreSQL per request. A test-only endpoint proves
  the accountant policy; the real admin-only member-management endpoint proves refusal and allowed writes.
- **What I learned:** keeping membership roles out of tokens avoids stale permissions after a role change.
  `WebApplicationFactory` can add a policy probe without shipping placeholder domain endpoints.

#### Company provisioning, settings and archival (2.7, 2.8; 2.9 remains pending)
- **Purpose:** provision a company safely, preserve identity and records, and explain changes.
- **How:** the migrator optionally seeds an operator from `OPERATOR_EMAIL` / `OPERATOR_PASSWORD`. An operator
  creates a company and first administrator; RFC is format-validated and immutable in PostgreSQL. New passwords
  require 12 characters; existing users keep their password, with the same response shape. Settings changes
  store previous/new values and the actor in the same transaction. Archival preserves rows; there is no DELETE
  route. A denied audit insert proves that settings roll back. The migrator image needs the ASP.NET runtime
  because the seed uses PasswordHasher; the first Compose check exposed and verified that packaging fix.
- **What I learned:** a transaction is the useful boundary for an action and its audit entry. The minimal audit
  table depends on group 5 for hash chaining, rejection triggers and the rest of the audit contract.
- **Demo shortcut:** a password in the creation request keeps this demo small; production would use invite/reset
  links and managed secrets. `.env.example` contains placeholders, not operator credentials.

#### Isolation regression suite and ADR (2.5; 2.6 remains pending)
- **Purpose:** make a later table or endpoint fail the checks until its isolation is covered.
- **How:** `information_schema` discovers tenant tables; the guard requires enabled/forced RLS and a policy.
  `EndpointDataSource` discovers routes; every protected route needs a successful baseline, anonymous refusal,
  and cross-company/non-operator checks. Company B's rows are compared before/after the attempts. Deliberately
  dropping `membership_isolation` produced four failures, including the explicit missing-policy guard; restoring
  it returned the suite to green. ADR-003 compares RLS, schema-per-company and database-per-company.
- **What I learned:** dropping a policy causes default-deny rather than a leak, so endpoint refusal alone cannot
  prove a policy is present; the structural policy guard is essential.

**Explicitly pending:** tasks 2.4, 2.6 and 2.9 stay unticked. Five skipped tests retain the exact scenario names:
“Viewer cannot decide” and “Different roles” need real links in group 8; “Cross-company attempt in the test suite”
and “Delete attempt” need imported CFDI in group 3; “Tolerance changed” needs a reconciliation run in group 6.
Persistence/audit of settings is verified now, but the future run behavior is not claimed. No task is ticked on a skip.

### PR-1: Foundations (tasks 1.1 to 1.5), commit `a10c3ab`

**Goal:** a project that builds, starts and is checked automatically, before any business logic exists.
**Verified:** build clean, 4 of 4 tests, format check clean, all four services healthy in Docker, migration
up/down/up on a real database, and the GitHub Actions run `41122200072` succeeded.

#### 3.1 Solution and shared build settings (task 1.1)
- **Purpose:** one place defines how every project is built, so nobody has to repeat settings or can forget them.
- **How:** `Directory.Build.props` is picked up automatically by every `.csproj` underneath it.
  `TreatWarningsAsErrors` plus `AnalysisLevel` make the compiler's code analysers block sloppy code.
- **Learn:** a *solution* (`.slnx`) groups *projects*; a *project* (`.csproj`) builds one assembly (a `.dll` or program).
  Two analysers already taught us something: renaming the `Shared` namespace to `Common` (reserved word in
  VB), and using `[LoggerMessage]` instead of string logging (faster, checked at compile time).

#### 3.2 Core library: modules and common code (task 1.1)
- **Purpose:** the business rules will live here, in seven folders that match the design (Ingestion, Matching,
  Review, Audit, Tenancy, Sat, Jobs). The API and the worker both reference it, so logic is written once.
- **How:** each module has a small marker class implementing `IModule` (`src/Reconciliation.Core/Common/IModule.cs`).
  The rule from the design is that modules talk through interfaces and never through each other's tables.
- **Learn:** an `interface` is a contract without code; a *class library* is code with no `Main`, reused by programs.

#### 3.3 Health check and database access (task 1.2)
- **Purpose:** a standard way to ask "is the program alive and can it reach the database?". Docker, CI and later
  a load balancer all use it.
- **How:** `IDatabaseHealth` (interface) with `NpgsqlDatabaseHealth` (implementation) runs `SELECT 1`.
  `MapHealth()` publishes `GET /health` and answers 200 with `{"status":"ok","database":"up"}` or 503.
- **Learn:**
  - *Dependency injection:* `AddReconciliationDatabase` registers services once; ASP.NET hands them to whoever
    asks in a constructor. Tests swap the real check for a fake.
  - *Extension methods* (`this IServiceCollection services`) add methods to a type you do not own.
  - *Primary constructors* (`class NpgsqlDatabaseHealth(NpgsqlDataSource dataSource)`) and *records*
    (`record HealthResponse(...)`) remove boilerplate.
  - `await using`, `async`/`await` and `CancellationToken` for non-blocking database calls.
  - A *minimal API* route is just `app.MapGet("/health", ...)` (`HealthEndpoint.cs`).

#### 3.4 API host (task 1.2)
- **Purpose:** the entry point for users. Everything the REST API will do is added to this program.
- **How:** `src/Reconciliation.Api/Program.cs` is five lines using *top-level statements*: build, register,
  map endpoints, run. `public partial class Program;` lets tests start the whole app in memory.
- **Learn:** `WebApplication.CreateBuilder` / `Build` / `Run` is the standard ASP.NET Core life cycle.

#### 3.5 Worker host (task 1.2)
- **Purpose:** long-running background work that must not block web requests. It can also be started several
  times to process jobs in parallel.
- **How:** `JobLoop` is a `BackgroundService`: the host calls `ExecuteAsync` when it starts and cancels the token
  when it stops. Today the loop only logs and waits; group 4 replaces it with the real job queue.
- **Learn:** hosted services; `partial` methods with the `[LoggerMessage]` *source generator* (the compiler writes
  the logging code for you).

#### 3.6 Database migrations (task 1.5)
- **Purpose:** database changes are versioned code, so every environment gets the same schema and any change
  can be undone.
- **How:** `M0001CompanyUserMembership` has `Up()` (create) and `Down()` (drop). The Migrator tool runs
  `up` or `down` through FluentMigrator, which records applied versions in a `VersionInfo` table.
- **Schema notes:** the RFC is validated by a database `CHECK`; money-like settings use `numeric(18,2)`,
  never floating point; `membership` links a user to a company with a role.
- **Learn:** *raw string literals* (`"""..."""`) hold multi-line SQL; a program returns an *exit code*
  (0 ok, non-zero failure) so scripts and CI can react; migrations must never be edited once shared.

#### 3.7 SAT stub (task 1.2, placeholder)
- **Purpose:** a stand-in for the real SAT service so the status check can be built and tested offline.
- **How:** an empty ASP.NET app with `/health` and a 501 for everything else. Task 7.2 turns it into a real fake.

#### 3.8 Docker image and Compose (task 1.2)
- **Purpose:** one command gives anyone the whole system, identical on every machine.
- **How:** one `Dockerfile` with several *targets* (`api`, `worker`, `migrator`, `sat-stub`). Compose starts
  PostgreSQL, waits until it is healthy, runs the migrator once, then starts the rest. Containers run as a
  non-root user.
- **Learn:** *multi-stage builds* (big SDK image to build, small runtime image to run); `depends_on` with
  `service_healthy` and `service_completed_successfully` describe start-up order.

#### 3.9 Tests (task 1.1 / 1.2)
- **Purpose:** prove the health endpoint and the configuration errors behave, now and after every change.
- **How:** xUnit `[Fact]` methods; `WebApplicationFactory<Program>` runs the real API in memory with a fake
  database check (`HealthEndpointTests.cs`).
- **Learn:** a *fixture* (`IClassFixture`) shares expensive setup between tests; fakes keep tests fast.

#### 3.10 CI and ADRs (tasks 1.3, 1.4)
- **Purpose:** CI runs the same checks on every push so mistakes are found within minutes. The ADRs explain *why*
  the structure and the stack were chosen (modular monolith with a worker; C# and PostgreSQL).
- **Learn:** a pipeline is just the commands you would run by hand, written down. Reading ADR-002 shows the
  trade-offs between TypeScript, Java, Go, PHP, Ruby and Elixir that led to C#.

**Findings worth remembering from this PR**
- A dependency of FluentMigrator had a known vulnerability. Because warnings are errors the build stopped, and the
  fix was to pin a patched version (`System.Security.Cryptography.Pkcs` 10.0.14) instead of silencing the warning.
- The first scaffold was TypeScript. It was discarded when the stack changed to C#; nothing depended on it.

## 4. Next work: group 4, Jobs

Move the synchronous ingestion services behind the PostgreSQL job queue, preserving their tenant transaction,
per-file results and idempotency. Task 2.4 remains pending for real link decisions in group 8; the settings/run
check remains pending for group 6. The progress map intentionally keeps group 2 in progress.

## 5. The rest of the road

| Group | Adds | Main types |
|---|---|---|
| 3. Ingestion | Reading CFDI XML and bank CSV safely, with idempotent import and the document and balance checks | Module, Database |
| 4. Jobs | PostgreSQL job queue: workers claim jobs without clashes, retries, recovery from crashes | Module, Host, Database |
| 5. Audit | Append-only, hash-chained log of every action, with a verification function | Module, Database |
| 6. Matching | The rule pipeline that links movements to invoices, with reasons, credit notes, cancellations and due dates | Module |
| 7. SAT status | The SAT client with stub, cache, retries and replay, then the real service | Module, Host |
| 8. Review + API | Review queue, decisions, summaries, OpenAPI document and a minimal UI | Module, API |
| 9. Test data | Anonymiser, statement generator, edge cases, demo seed and leak scan | Tool |
| 10. Observability | Structured logs, traces and metrics, failure-injection tests, runbook | Pipeline, Docs |
| 11. Delivery | Cloud-design ADR, architecture views, README tour, recorded walkthrough | Docs |

## 6. How to keep this log current

1. Finish a pull request and tick its tasks in `tasks.md` (`- [x]`).
2. Run `python3 tools/progress_diagram.py` and `python3 tools/progress_diagram.py --lang es`, and commit the new `docs/progress/status-map*` files.
3. Add a "PR-n" entry in section 3 using the same shape: goal, one block per piece with Purpose, How, Learn.
4. Move the "Next pull request" table in section 4 to the following group.
5. The pull request template (`.github/pull_request_template.md`) asks for exactly this information, so the
   description can be pasted into the log.

## 7. Small glossary

- **Host:** a program that keeps running (web server, background worker).
- **Dependency injection (DI):** the framework creates the objects your class needs and passes them in; easy to
  replace with fakes in tests.
- **Migration:** a versioned database change with `Up` and `Down`.
- **Row-level security (RLS):** a PostgreSQL feature that filters rows by policy, enforced by the database.
- **Idempotent:** doing the same thing twice has the same result as doing it once (uploading the same file twice
  creates one record).
- **ADR:** architecture decision record; a short note on a decision and the alternatives rejected.
- **Healthy:** the container's health check passed, so dependent services may start.
