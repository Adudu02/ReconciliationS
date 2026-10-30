## Why

Accountants at Mexican small businesses and accounting firms reconcile CFDI invoices against bank statements by hand, which is slow and error-prone (partial payments, payment complements, credit notes and cancelled invoices are easy to miss). This project is a 1–2 week portfolio demonstration for senior backend, cloud and architecture roles: it delivers the smallest vertical slice of a reconciliation platform that shows rule-heavy domain modelling, tenant isolation, idempotent asynchronous processing, auditability and operability. The business requirements are fixed in the BRS (v0.7); this change turns them into buildable specifications.

## What Changes

- Introduce a multi-company backend that ingests CFDI 4.0 XML and CSV statements in a documented reference format (real banks added later as extra parsers) idempotently and asynchronously.
- Introduce a matching engine that links bank movements to CFDI, including partial payments, payment complements and credit notes, and classifies every item as reconciled, suggested (to review) or pending.
- Introduce a review queue and per-invoice summaries, exposed through a documented API and a minimal UI.
- Introduce an append-only audit log of imports and match decisions.
- Introduce database-level tenant isolation with roles.
- Introduce a SAT status client behind an interface, with a local stub (phase 1) that mimics the real SOAP contract; the real service is connected later by configuration only (phase 2).
- Introduce tooling for test data: an anonymiser for real CFDI, a generator for matching bank statements and edge cases, and a demo seed.
- Introduce the operational baseline: one-command Docker Compose environment, CI, observability and a short runbook.
- Apply the Mexican fiscal rules that affect reconciliation: net totals with discounts and retentions, payment-complement balances and due dates, cancellation as a process, issued and received direction, and retention of original files (design D14 to D16).
- No existing behaviour is modified; the project starts from an empty repository (**no breaking changes**).

Stretch goals, deliberately deferred: Excel export, PDF statements, additional banks, Project 2 and Project 3 integration, public-cloud deployment, formal load testing.

## Capabilities

### New Capabilities
- `cfdi-ingestion`: upload and parse CFDI 4.0 XML (income, payment complement, credit note) securely and idempotently by UUID.
- `bank-statement-ingestion`: upload and parse one bank's CSV statements behind an extensible parser interface; idempotent by statement line.
- `reconciliation-matching`: rules and confidence model linking movements to CFDI (N:M), with partial payments, complements and credit notes, and item states.
- `review-and-summaries`: review queue for doubtful matches, user decisions (confirm, reject, change), per-invoice paid/pending summaries, and the minimal UI and API.
- `audit-log`: append-only, tamper-evident record of imports, matches and overrides with user and timestamp.
- `tenancy-and-access`: multi-company isolation enforced in the database, and role-based access.
- `sat-status-check`: SAT client interface, local stub that reproduces the real contract and can force outcomes, record/replay of real responses, caching, retries and graceful degradation.
- `job-processing`: asynchronous imports and reconciliation runs with retries, backoff, idempotent handlers and no loss or duplication on failure.
- `test-data-tooling`: anonymisation of real CFDI, generation of matching bank statements and edge cases, and a demo seed.

### Modified Capabilities
<!-- None: the repository has no existing specs. -->

## Impact

- New codebase (stack: C# on .NET 10 with ASP.NET Core, PostgreSQL with row-level security, a queue chosen by ADR, Docker Compose); no existing code is touched.
- External dependency: the SAT CFDI status service (public SOAP, no credentials), used only in phase 2; phase 1 runs entirely against the local stub.
- Data: real CFDI are never committed; only anonymised files enter the repository. Original files kept by the platform are retained for at least five years and are never deleted through the API.
- Documentation deliverables: README, architecture views, ADRs, runbook, OpenAPI document.
