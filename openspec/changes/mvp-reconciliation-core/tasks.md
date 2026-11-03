## 1. Foundations (day 1)

- [x] 1.1 Create the repository structure (API, worker, shared modules, tools, docs) and verify `README` lists how to build and run it
- [x] 1.2 Add Docker Compose with PostgreSQL, API, worker and SAT stub placeholders and verify one command starts them and health checks pass
- [x] 1.3 Add CI that builds, lints and runs tests and produces container images, and verify a pushed commit goes green
- [x] 1.4 Write ADR-001 (modular monolith with separate worker) and ADR-002 (stack choice) and verify both list the rejected alternatives
- [x] 1.5 Add database migrations tooling and the first migration (company, user, membership) and verify migrate up and down succeed on an empty database

## 2. Tenancy and access (days 1–2)

- [x] 2.1 Implement the transaction wrapper that sets the company context for API requests and jobs, and verify a query without context returns no rows
- [x] 2.2 Enable row-level security with policies on every tenant table, using an application role without bypass, and verify a query without a company filter returns only the context company's rows
- [x] 2.3 Implement authentication (slow salted password hashing, token issuance) and verify an anonymous request to a data endpoint is refused
- [ ] 2.4 Implement roles (administrator, accountant, viewer) and per-company memberships, and verify the role scenarios of the tenancy spec pass
  pending: Viewer cannot decide; Different roles (real link decisions) -> group 8.
- [x] 2.5 Write ADR-003 (tenant isolation with row-level security) and verify it compares schema-per-company and database-per-company
- [x] 2.6 Add the cross-company isolation test suite over every data endpoint and verify it fails when a policy is deliberately removed
- [x] 2.7 Implement company creation by a platform operator with a validated, immutable RFC, and verify the operator and non-operator scenarios
- [x] 2.8 Implement company settings changes by administrators with audit of old and new values, and verify an accountant is refused
- [x] 2.9 Block deletion of imported data and archive companies instead, and verify a delete attempt is unavailable

## 3. Domain model and ingestion (days 2–4)

- [x] 3.1 Document the reference CSV format (columns, date and number conventions, reference tokens, sample lines) and verify the sample lines parse in task 3.6
- [x] 3.2 Create migrations for company (RFC, settings), import_file, cfdi, cfdi_relation, cfdi_payment, cfdi_payment_doc, bank_account, bank_movement and verify uniqueness constraints reject duplicates
- [x] 3.3 Implement safe CFDI 4.0 XML parsing (DTD and external entities disabled, size and count limits) and verify the hostile-file tests pass
- [x] 3.4 Implement CFDI import for income, payment complement and credit note with per-file report, and verify the cfdi-ingestion scenarios pass
- [x] 3.4b Parse discount, taxes, retentions and local taxes and verify `samples/synthetic-income-ppd-retention.xml` imports with total 1,939.27, a discount case imports, and an inconsistent total or an excessive discount is rejected
- [x] 3.4c Reject a CFDI that does not involve the company RFC, record issued or received direction, and verify both scenarios
- [x] 3.4d Store payments and related invoice lines of complement 2.0, flag inconsistent balances and reject other versions, and verify the three complement scenarios pass
- [x] 3.4e Store relation codes (01, 03, 04, 07), the global-invoice flag and dates as declared, and verify the substitution, global and midnight scenarios pass
- [x] 3.4f Keep original files with their hash and no delete path, and verify the original is returned byte for byte
- [x] 3.5 Implement idempotent CFDI import by UUID per company and verify uploading the same file twice creates one record
- [x] 3.6 Implement the bank parser interface, the reference-format parser and reference extraction, and verify valid, malformed, unknown-bank and tracking-key scenarios pass
- [x] 3.7 Implement idempotent movement import with overlapping statements and verify the overlap and identical-lines scenarios pass
- [x] 3.8 Verify money handling is exact by adding a test summing 0.10 and 0.20 to 0.30 in storage and code

## 4. Jobs (days 3–4)

- [x] 4.1 Implement the PostgreSQL job queue with transactional enqueue and `SKIP LOCKED` claiming, and verify two workers never process the same job
- [x] 4.2 Implement attempts, backoff, permanent versus transient failure, and the failed-jobs list with re-run, and verify the retry scenarios pass
- [x] 4.3 Implement visibility timeout recovery and verify a worker killed mid-job leads to completion by another worker with effects applied once
- [x] 4.4 Move CFDI and statement imports onto jobs with progress and verify an upload returns a job identifier immediately and reaches succeeded
- [x] 4.5 Write ADR-004 (queue on PostgreSQL) with the migration path to a broker and verify it states the volume limit
- [x] 4.6 Implement the reconciliation run job on demand with the four job types, and verify a second request returns the running job

## 5. Audit log (days 4–5)

- [ ] 5.1 Create audit_entry with insert-only privileges and a trigger that rejects update and delete, and verify an update attempt fails and is itself recorded
- [ ] 5.2 Implement hash chaining per company and the verification operation, and verify an altered row is reported at the correct position
- [ ] 5.3 Record audit entries inside the same transaction as imports, links, decisions and role changes, and verify a rolled-back action leaves no entry
- [ ] 5.4 Implement filtered read access for viewers and auditors and verify company isolation of the log

## 6. Matching engine (days 4–6)

- [ ] 6.1 Implement the link model and derived item states and verify the partial and N:M scenarios pass
- [ ] 6.2 Implement deterministic rules (payment complement, amount, date tolerance) and verify the worked example's first movement becomes reconciled
- [ ] 6.2b Use the net total (after discount and retentions) as payable amount and verify a movement of 1,939.27 fully covers the synthetic sample invoice
- [ ] 6.3 Implement heuristics with criteria, confidence and explanation, and verify the second movement becomes suggested with its reasons
- [ ] 6.4 Implement credit-note handling and verify the credit-note scenario shows nothing pending
- [ ] 6.5 Implement classification of movements without CFDI and verify they leave the pending list
- [ ] 6.6 Make reconciliation idempotent and decision-preserving, and verify the rerun and rejected-link scenarios pass
- [ ] 6.7 Add property-based tests (rerun changes nothing, sums conserved, rejected link never reappears) and verify they run in CI
- [ ] 6.8 Write ADR-005 (explainable rule pipeline) and verify it compares a single score and a learned model
- [ ] 6.9 Match only credit movements with issued income CFDI and verify a debit movement is not proposed
- [ ] 6.10 Add the gross-payment heuristic and the reference-equals-operation-number criterion, and verify both reasons appear
- [ ] 6.11 (stretch if time is short) Record invoice-currency amount, movement amount and declared rate on links, and verify the dollar-invoice scenario
- [ ] 6.12 Compute the complement due date and the overdue state with the weekend setting, and verify both scenarios
- [ ] 6.13 Flag overpaid invoices and verify the overpayment scenario
- [ ] 6.14 Keep global invoices out of RFC matching and verify they show pending with their reason
- [ ] 6.15 Cascade cancellation of invoices and complements to links, handle cancellation in process, and propose replacements (relation 04), and verify the four cancellation scenarios

## 7. SAT status check (days 5–6, real adapter in phase 2)

- [ ] 7.1 Define the SAT client interface with the response model and verify unit tests cover all codes and states
- [ ] 7.2 Implement the SAT stub service speaking the real SOAP contract with a control interface to force outcomes, and verify each outcome including timeout can be forced
- [ ] 7.3 Implement caching, retries with backoff, rate limiting and the unknown-status degradation, and verify the service-down scenario completes a reconciliation run
- [ ] 7.3b Store cancellability, cancellation status and the raw EFOS value, and verify the in-process and deadline-expired scenarios
- [ ] 7.3c Implement the on-demand status check job and verify the unknown and cached-cancelled scenarios
- [ ] 7.4 Connect cancelled status to matching and audit, and verify a cancelled invoice's links move to review with an audit entry
- [ ] 7.5 Implement record and replay keyed to anonymised records and verify replay makes no network call
- [ ] 7.6 Phase 2: implement the real SOAP adapter selected by configuration, test it with one real payment CFDI, and record whether the service answers for payment CFDI in the BRS open question

## 8. Review, summaries and API (days 6–8)

- [ ] 8.1 Implement the review queue endpoint with ordering and reasons and verify the queue scenario passes
- [ ] 8.2 Implement confirm, reject and change decisions with audit and verify the three decision scenarios pass
- [ ] 8.3 Implement invoice summary and company overview endpoints and verify the worked-example summary shows 50% paid and 5,800.00 pending
- [ ] 8.3b Add complement status to the summary, the complement filter and the overview alerts, and verify the missing, overdue and alert-count scenarios
- [ ] 8.3c Implement the supported-banks list, the movement classification, the settings and the original-file endpoints from the design catalogue, and verify each is in the OpenAPI document
- [ ] 8.3d (stretch if time is short) Show the cash payment warning above 2,000.00 and verify the state is unchanged
- [ ] 8.4 Generate the versioned OpenAPI document with the disclaimer and verify it covers every endpoint
- [ ] 8.5 Build the minimal UI (review queue, invoice summary, overview) using only the API, and verify the worked example can be completed through it

## 9. Test data tooling (days 3–7, in parallel)

- [ ] 9.1 Implement the generator for CFDI and matching statements with a seed, and verify identical seeds give identical output
- [ ] 9.2 Implement the edge-case catalogue including the fiscal cases (discount, foreign currency, overdue complement, global invoice, cancellation in process) and hostile files, and verify each case is produced
- [ ] 9.3 Implement the demo seed and verify an empty installation shows reconciled, suggested, pending and cancelled items
- [ ] 9.4 Implement the anonymiser preserving relations, amounts and company direction and verify the relation, consistent-replacement and direction scenarios pass
- [ ] 9.5 Implement the leak scan and wire it to pre-commit and CI, and verify it fails on a planted identifier without printing it

## 10. Observability and operations (days 8–9)

- [ ] 10.1 Add structured logs with request and job identifiers and verify one import can be followed in the logs
- [ ] 10.2 Add traces across API, queue and worker with a viewer in Compose and verify one import appears as a single trace
- [ ] 10.3 Add metrics for job states, queue depth and SAT checks and verify they appear in the viewer
- [ ] 10.4 Write the failure-injection tests (killed worker, SAT timeout) and verify they run in CI
- [ ] 10.5 Write the short runbook for common failures and verify each entry names a symptom, a check and a fix

## 11. Delivery for reviewers (days 9–10)

- [ ] 11.1 Write ADR-006 (target cloud design, not deployed) and verify it lists services, costs and why it was not deployed
- [ ] 11.1b Write ADR-007 (fiscal rules applied to declared values: scope, settings, sources and points still to confirm) and verify it lists the open questions
- [ ] 11.2 Draw the architecture views (context, containers, deployment) and verify they match the running system
- [ ] 11.3 Write the README with the problem, the worked example, the 10-minute tour and the one-command run, and verify a fresh clone follows it successfully
- [ ] 11.4 Record the walkthrough and verify it shows import, matching, review, audit verification and a trace
- [ ] 11.5 Run the whole test suite, the leak scan and the Compose start from a clean checkout and verify all pass
