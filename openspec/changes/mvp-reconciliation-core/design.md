## Context

Greenfield repository, one developer, 1–2 weeks, runs locally with Docker Compose and is shown through a recorded walkthrough (see proposal.md). The audience is reviewers for senior backend, cloud and architecture roles, so every significant choice below also becomes a short ADR with the alternatives that were rejected. Choices marked **(my call)** were made by the author of this document, not confirmed by the project owner, and can be changed before implementation starts.

## Goals / Non-Goals

**Goals:**
- A thin vertical slice that is correct, tested and explainable: ingest → match → review → audit, with tenant isolation, idempotent asynchronous processing and a swappable SAT client.
- Few moving parts, so the whole system starts with one command and fits in a reviewer's head.
- Decisions that show trade-off reasoning, including a documented (not deployed) target cloud design.

**Non-Goals:**
- Public-cloud deployment, Kubernetes, formal load testing, multi-region or high-availability setups (covered only by an ADR).
- Microservices, event sourcing, machine-learning matching.
- A polished front end.

## Decisions

### D1. Modular monolith with a separate worker process **(my call)**
One codebase organised by module (ingestion, matching, review, audit, tenancy, sat, jobs) producing two entry points: the HTTP API and the worker. Modules talk through explicit interfaces, never through each other's tables.
- *Why:* one developer and two weeks; boundaries are still visible and testable; the worker scales out independently, which is what the job-processing spec needs.
- *Alternatives:* microservices (operational cost with no benefit at this size); single process with in-process jobs (cannot demonstrate worker failure and scale-out).

### D2. C# on .NET 10 with ASP.NET Core, PostgreSQL **(owner's choice; replaces the brief's TypeScript / NestJS proposal)**
- *Why:* the project owner chose C# to build on and to learn; `decimal` gives exact money, ASP.NET Core and the generic host give the API and the worker from one codebase, and OpenTelemetry support is first class. PostgreSQL gives row-level security, exact `NUMERIC` money, transactional queues and strong constraints. Stack details are in ADR-002.
- *Alternatives:* TypeScript with NestJS (the brief's proposal; no native decimal, fewer CFDI libraries); Java with Spring Boot (strong enterprise signal, heavier for this scope); Go (more code for the rules engine); PHP, Ruby and Elixir (explored for learning, weaker fit with a 1-2 week scope); document stores (weak for N:M links and integrity).

### D3. Tenant isolation with PostgreSQL row-level security
Every tenant table carries `company_id`. The application connects with a role that cannot bypass row-level security; each request or job starts a transaction that sets the company context, and policies filter on it. No context means no rows. Isolation tests run through every endpoint.
- *Why:* satisfies the requirement that a forgotten filter cannot leak data; strongest signal for the portfolio.
- *Alternatives:* schema per company (migrations and connection handling multiply); database per company (heavy); application-level filters only (does not meet the spec).

### D4. Queue on PostgreSQL **(my call; open question 6 of the BRS was unanswered)**
Jobs live in a PostgreSQL table claimed with `FOR UPDATE SKIP LOCKED`, written in the same transaction as the domain change that created them. Visibility timeouts return jobs of dead workers to the queue; attempts, backoff and a failed-jobs list are columns and states, not extra infrastructure.
- *Why:* no new infrastructure; transactional enqueue removes a whole class of lost-job bugs; adequate for the demo's volume.
- *Alternatives:* Redis-based queue (extra service, no transactional enqueue); RabbitMQ or SQS (stronger decoupling, more to run). The ADR describes when to move to SQS or a broker in a cloud deployment.

### D5. Data model
Diagrams: `docs/diagrams/database/database-erd.png` (tables and relationships) and `database-states.png` (state rules).
Core tables: `company` (RFC, settings), `app_user`, `membership(role)`, `bank_account`, `bank_movement` (optional reference and classification), `import_file` (original files with hash), `cfdi` (UUID, type, direction, RFCs, issue time as declared, stamp time, currency, subtotal, discount, taxes, retentions, total, payment method, global flag, SAT status), `cfdi_relation` (SAT relation code between two CFDI), `cfdi_payment` (one row per payment of a complement: date, form, currency, exchange rate, amount, operation number), `cfdi_payment_doc` (one row per related invoice: installment, previous balance, paid, remaining, currency), `link` (movement, invoice, optional payment CFDI, amount applied, movement amount and rate, state, origin, criteria, confidence, decision user and time), `job`, `sat_status_cache` (status, cancellability, cancellation status, EFOS value, expiry) and `audit_entry`.
- Uniqueness gives idempotency: `(company_id, uuid)` for CFDI, a natural-key hash of account, date, description, amount and balance for movements, and one `link` per (movement, invoice) pair so that a rejection is remembered.
- Item states (reconciled, suggested, pending) are **derived** from links and amounts, not stored, so they cannot drift from the links.
- Money is `NUMERIC(18,2)` in storage and integer minor units in code; never floating point.
- Dates declared in a CFDI (issue time, payment date) are stored as declared, in local time without a time zone.

### D6. Matching as an explainable rule pipeline
Stage 1, deterministic rules (payment complement references the invoice, amount equal, date within tolerance) produce reconciled links. Stage 2, additive heuristics (amount equal or within a few cents, date proximity, RFC found in the description, invoice number or UUID fragment in the reference text, a tracking key equal to the complement operation number, an amount equal to the total before retentions) produce suggested links with the list of criteria that fired and a confidence level (high, medium, low) derived from the points. Stage 3 evaluates partial payments and N:M allocation. User decisions are stored and take precedence; reruns are idempotent.
- *Why:* every result can be explained to an accountant and an auditor (anti-goal in the BRS); rules are unit and property testable.
- *Alternatives:* one opaque numeric score (hard to explain); learned model (no data, not explainable).

### D7. Append-only, hash-chained audit log
`audit_entry` rows are inserted by the application role only (no update or delete privilege, plus a trigger that rejects both). Each entry stores the hash of its content and of the previous entry of the same company; a verification operation walks the chain. The entry is written in the same transaction as the action it records.
- *Alternatives:* external log store (loses atomicity with the action); event sourcing (too heavy for the slice).

### D8. SAT client as a port with three adapters
A single interface with adapters for the local stub, the real SOAP service and record/replay. The stub runs as its own small container that speaks the real SOAP contract and exposes a control interface to force outcomes. Replay reads responses recorded locally and keyed to the anonymised records. Caching, retry with backoff, rate limiting and the "status unknown" degradation sit in the port's shared layer, so all adapters behave the same.
- *Why:* phase 1 needs no account; switching to the real service is configuration only (BR-31).

### D9. Safe XML handling
CFDI are parsed with a parser configured with DTD and external entity processing disabled, size and count limits at the upload boundary, and tests using the hostile files produced by the test-data tooling.

### D10. Observability
Structured JSON logs with request and job identifiers; OpenTelemetry traces across API, queue and worker so one import can be followed end to end; metrics for job states, queue depth and SAT checks. Compose includes a collector and a trace and metrics viewer.

### D11. API and UI
REST API with a generated OpenAPI document and versioned paths; token-based authentication with slow password hashing. A minimal single-page UI (review queue, invoice summary, overview) consumes only that API; if time runs short the API with its interactive documentation is the demonstration surface.

Endpoint catalogue (paths are proposals, frozen in the OpenAPI document of task 8.4; no `DELETE` exists for imported data):
- Public: `GET /health`, `GET /openapi.json`.
- Authentication: `POST /auth/tokens`.
- Platform operator: `POST /companies` (RFC and first administrator).
- Administrator: `GET` and `PATCH /companies/{id}/settings`, `/users`, `/memberships`, `GET /jobs?status=failed`, `POST /jobs/{id}/retry`.
- Accountant: `GET /banks`, `POST /companies/{id}/cfdi-uploads`, `POST /companies/{id}/statements`, `GET /jobs/{id}`, `POST /companies/{id}/reconciliation-runs`, `GET /review-queue`, `POST /links/{id}/decisions` (confirm, reject, change), `PATCH /movements/{id}` (classify), `POST /cfdi/{uuid}/sat-check`.
- Any role, read only: `GET /cfdi` (filters: type, direction, complement status), `GET /cfdi/{uuid}`, `GET /cfdi/{uuid}/summary`, `GET /cfdi/{uuid}/xml`, `GET /movements`, `GET /overview`, `GET /audit-entries`, `GET /audit/verification`.

### D12. Testing and CI **(my call on the CI host: GitHub Actions)**
Unit tests; property-based tests for matching invariants (re-running changes nothing, sums are conserved, a rejected link never reappears); integration tests against a real PostgreSQL started by the test run; cross-company isolation tests on every endpoint; failure-injection tests (killed worker, SAT timeout); one end-to-end scenario for the worked example. CI builds, runs all tests, runs the leak scan and builds container images.

### D13. Target cloud design is documentation only
An ADR describes how the same services would run on a managed cloud (container service, managed PostgreSQL, SQS or equivalent, secrets manager, telemetry backends), what each change would cost in complexity and money, and why it was not deployed for this project.

### D14. Fiscal rules are applied to declared values
The system validates the arithmetic of the declared header amounts (total equals subtotal minus discount plus taxes minus retentions) and the balance chain of payment complements, but never recomputes taxes from rates and never validates retention rates, because rounding rules and rates change. A CFDI must involve the company's RFC; its direction (issued or received) is derived from it. Only issued income CFDI are matched in this version.
- *Why:* the SAT validates the stamped document; the platform's job is to reconcile what was declared, not to re-audit it.
- *Alternatives:* recomputing taxes (fragile around rounding); trusting totals blindly (accepts corrupt files).

### D15. Retention of originals
Original XML and statement files are kept unchanged with their hash for a configurable period of at least five years; there is no delete endpoint for imported data and companies are archived, not deleted. Accounting records must be kept at least five years under the tax code (ten under the commercial code), so the safe default is ten.
- *Why:* the XML is the legal document and the statement is accounting evidence.

### D16. Deadlines and cancellation are information, not enforcement
The complement due date is the fifth calendar day of the month after the payment, with a company setting to move it to the next business day (off by default). Cancellation is a process (in process, request rejected, deadline expired) and a CFDI stays current until the service says it is cancelled. The platform does not issue or cancel invoices, so it does not enforce the cancellation deadline.

## Architecture views

Source files are in `diagrams/` (SVG, with PNG renders used in the SRS): `containers.svg` shows the Compose containers and their connections; `sequence.svg` follows one upload through API, PostgreSQL, worker and SAT client to the review queue. Task 11.2 checks them against the running system.

## Risks / Trade-offs

- [Two weeks is tight for nine capabilities] → build the vertical slice first (ingest, match, review, audit); tenancy and job processing are built in from the start because they are hard to retrofit; SAT real adapter, UI polish and stretch goals go last.
- [PostgreSQL queue does not scale like a broker] → acceptable for the demo; the ADR states the limit and the migration path.
- [Row-level security adds per-transaction overhead and a risk of forgetting the context] → one transaction wrapper used by API and worker, and tests that fail when context is absent.
- [Heuristic matching may suggest wrong links] → only deterministic rules confirm automatically; everything else needs a human; reasons are always shown.
- [No real bank sample available] → the first parser targets a documented reference format; the parser interface isolates bank specifics, so a real bank adds one parser and changes nothing else.
- [Real SAT behaviour for payment CFDI is untested] → the stub follows the documented contract; the real adapter is phase 2 and its first task is a test with one real payment CFDI.
- [Anonymisation could leak or break SAT correlation] → mapping outside the repository, leak scan on every commit, and record/replay keyed to the anonymised records.

- [Several fiscal rules were verified only in secondary sources] → each one is isolated in a setting or a non-blocking warning, and the points to confirm are listed under Open Questions.
- [Uploading a CFDI to the wrong company] → rejected when neither RFC is the company's.

## Open Questions

- Which real bank's CSV is added after the reference format, once a sample is available (an extra parser only; no change to specs or approach).
- Exact tolerance defaults for date proximity and amount differences (tuned with the generated data).
- Choice of UI framework if the single-page UI is kept (does not affect the specs).

- Cancellation deadline: reported as the month of the annual return of the issue year (from 2026); informational only, to confirm with an accountant.
- Exchange rate in payment complements: an accountant's informal answer is the rate of the payment date published in the DOF; the platform stores the declared rate, to confirm.
- Cash payments: the 2,000.00 limit and the fuel rule (cash fuel is never deductible) come from secondary sources; only the limit is implemented as a warning.
- Whether the complement due date moves to the next business day when the fifth is not one: off by default, to confirm.
- Local taxes: supported in the total formula; confirm whether real files need them.
- Matching received invoices with debit movements: deferred, the model keeps the direction.
