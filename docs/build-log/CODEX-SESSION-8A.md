# Codex session log: group 8A (review, summaries and API)

A session-specific build log. It records what one Codex run did: how it was started, the questions it asked, the
decisions it received, and the code and documents it produced. The project-wide history stays in
[`BUILD-LOG.md`](BUILD-LOG.md) (entry PR-12); this file is the detailed, chronological view of the session behind it.

## 0. How this log was made

Claude orchestrated the session: it wrote the plan, answered Codex's questions and ran the independent checks. This
log was assembled by Claude from three sources only:

1. the git history of the branch `feat/review-api` (18 commits, `git log main..HEAD`);
2. the consultation messages Codex sent through herdr and the answers it received;
3. Codex's own final report.

It is **not** a transcript of Codex's internal reasoning, which was not available. Where a statement comes from
Codex's own report and was not re-checked, it says so.

## 1. Session facts

| Item | Value |
|---|---|
| Date | 2026-11-05 (commits 19:01 to 20:01 local time, about 1 hour of work) |
| Agent | Codex CLI, model `GPT-6.1-Sol`, effort `high`, in a herdr pane next to Claude |
| Branch | `feat/review-api`, created from `main` after PR #7 |
| Scope | Tasks 8.1, 8.2, 8.3, 8.3b, 8.3c, 8.3d, 8.4 (task 8.5, the minimal UI, deliberately left to a later PR 8B) |
| Method | Spec-driven and test-first: each slice is a failing-test commit (`test:`) followed by a green implementation commit (`feat:`) |
| Rules given | Local commits only, no push or merge, no Claude attribution, leave an unrelated docs edit untouched, run .NET only through the SDK container, tear down any smoke containers |
| Result | 18 commits, 47 files changed (+1,460 / -125), 607 tests: 603 passed, 4 opt-in benchmark skips, 0 failed |

## 2. Timeline

| Time | What happened |
|---|---|
| Before 19:00 | First attempt on a Codex pane running `GPT-6-Luna` at medium effort. It created the branch, read the repository, and stopped after 17 s and again after 21 s without writing anything ("the only available action completed was repository inspection"). Nothing was committed. The user switched the pane back to `GPT-6.1-Sol` high and the plan was sent again. |
| 19:01 | S1 red: `test: define confirm and reject decision boundary` |
| 19:06 | S1 green: tenant confirm and reject decisions recorded atomically (migration M0012, `link_decision`) |
| 19:07-19:13 | S2: change decisions, terminal-state conflicts, explicit payment-coverage validation |
| 19:13 | **Question 1** (partial unique index), answered by Claude |
| 19:14-19:16 | S3: the pending tenancy and audit scenarios become real tests |
| 19:18-19:22 | S4: review queue |
| 19:24-19:26 | S5: invoice summaries, complement filters, cash warning |
| 19:27-19:34 | S6: company overview, additive totals, alerts, 20k review benchmark |
| about 19:35 | **Question 2** (decision precondition wording) and **Question 3** (downgrade guard), answered by Claude |
| 19:35-19:42 | S7: versioned OpenAPI document and catalogue coverage; downgrade guard test; ADR and bilingual documentation tests |
| about 19:45 | **Question 4** (smoke variant), answered by Claude |
| 20:01 | S8: final delivery commit (documentation, exports, diagrams, ticks), live smoke, teardown |

## 3. The brief Codex received

Claude's plan (`group8a-review-api.md`) fixed these design decisions in advance, so Codex only had to ask about what was
genuinely unspecified:

- **Decision resource:** `POST .../links/{linkId}/decisions` with confirm, reject and change; accountants and
  administrators write, viewers read, archived companies are read-only. One row per decision in a new `link_decision`
  table, one audit entry per decision (two for change), in the same transaction. Link row locked; a decision on a link
  that is no longer suggested returns 409; two concurrent decisions produce exactly one winner.
- **Review queue:** suggested links plus invoices with problem reasons, ordered by confidence then date, keyset paged,
  informational reasons excluded.
- **Summary and list:** totals, credit notes, net payable, paid, percentage, pending, state, linked movements, payment
  complement status per payment, cash warning above 2,000.00; list filters by type, direction, state and complement status.
- **Overview:** counts and amounts per state that add up to the imported totals, plus alert counts and the disclaimer.
- **OpenAPI:** anonymous `/openapi.json`, API version, disclaimer, security scheme, roles and error responses; no
  `/v1` path prefix now (it would rename every route).
- **Out of scope, recorded as gaps:** reopening a rejected link, manual many-to-many link creation, the UI (8B).

## 4. Questions Codex asked and the decisions it received

| # | Question from Codex | Decision | Why |
|---|---|---|---|
| 1 | The `link` table has `UNIQUE NULLS NOT DISTINCT (company_id, movement_id, cfdi_id, payment_cfdi_id)`. A "change to a different amount" closes the old link and creates a new one to the same invoice, which violates it. Replace it by a partial unique index for active links? | **Approved**, with conditions: unique index only `WHERE state IN ('suggested','reconciled')`; update every `ON CONFLICT` and insert path on `link`; keep rejected links and user-"change"-closed links blocking re-proposal of the pair explicitly; tests that nothing flaps after a change, a cleanup close or a rejection; Down refuses when historical duplicate pairs exist. | The old constraint silently did two jobs (no duplicates, tombstones block re-proposal). Replacing it means making the second job explicit. |
| 2 | The brief says decisions apply only to suggested links, but the test wording said "confirm then change to the same invoice". Which? | **Keep suggested-only.** The case to test is "suggestion, then change to the same invoice with a different amount", plus 409 tests for decisions on reconciled, rejected and closed links. | Claude's wording was wrong; the API precondition was not meant to widen. |
| 3 | M0012 Down refuses on duplicate link history but would drop `link_decision`. Also refuse when decisions exist, or document that Down drops decision metadata? | **Approved the extra guard:** Down refuses with a clear error when `link_decision` is non-empty; tests for both refusals and for a clean up, down, up. Documented: downgrading past M0012 needs a manual export of decisions; audit entries are never touched. | Decisions are user decisions; the project retains them (no silent deletion). |
| 4 | Importing the worked-example payment complement before matching reconciles the first 5,800.00 automatically, so confirming the other half gives 100%, not the requested 50%. Which smoke variant? | **Variant without a complement before the first run:** invoice plus two movements, run (two suggestions), queue, confirm one, summary shows 50% and 5,800.00 pending, overview totals, teardown. The payment-CFDI half is covered by the separate worked-example test. An optional second leg may attach the complement afterwards using references read from the disposable database. | The requested 50% only arises when both movements start as suggestions. |

## 5. Code and documents produced

### 5.1 New source files

| File | Purpose |
|---|---|
| `src/Reconciliation.Migrator/Migrations/M0012Review.cs` | Replaces the old unique constraint by the active-pair partial unique index; creates `link_decision` (forced RLS, `SELECT`/`INSERT` only for `cfdi_app`); adds `link_review_queue_idx` and `cfdi_list_idx`; Down refuses when decisions or historical duplicate pairs exist |
| `src/Reconciliation.Core/Matching/DecisionEndpoints.cs` | The decision resource (confirm, reject, change) with row lock, capacity re-check and payment-reference validation |
| `src/Reconciliation.Core/Matching/ReviewEndpoints.cs` | Review queue with keyset paging |
| `src/Reconciliation.Core/Matching/SummaryEndpoints.cs` | Invoice summary and filtered invoice list |
| `src/Reconciliation.Core/Matching/OverviewEndpoints.cs` | Company overview with additive totals and alerts |
| `src/Reconciliation.Api/ApiDescription.cs` | Versioned OpenAPI description with disclaimer, security scheme, roles and error responses |
| `docs/adr/ADR-009-review-decisions-and-read-model.md` | Decision record: decision resource, active uniqueness, the two-audit change exception, derived reads, paging, version migration, downgrade retention |

### 5.2 Endpoints added

| Method and path | Role |
|---|---|
| `POST /companies/{companyId}/links/{linkId}/decisions` | Accountant, administrator |
| `GET /companies/{companyId}/review-queue` | Any member |
| `GET /companies/{companyId}/cfdi` | Any member |
| `GET /companies/{companyId}/cfdi/{invoiceId}/summary` | Any member |
| `GET /companies/{companyId}/overview` | Any member |

The supported-banks list, movement classification, settings and original-file endpoints for task 8.3c already existed;
the work there was verifying that each is described in the OpenAPI document.

### 5.3 Tests

23 tests were added in six new files (the suite grew from 578 to 607 tests, three previously skipped domain scenarios
became real, and one opt-in review benchmark was added):

- `LinkDecisionTests` (10): forced RLS and minimal grants on `link_decision`, M0012 up/down/up preserving data and audit, Down refusing to delete decisions, atomic decision + one audit entry and 409 on repeat, change to the same invoice with a different amount without flapping, decisions on non-suggestions return the current state, confirm attaches both payment references, confirming a replacement closes the obsolete cancellation marker, capacity re-check without recording a refused decision, concurrent decisions have exactly one winner.
- `ReviewQueueTests` (3): suggestions and reasons shown, reconciled and informational items excluded; overpaid invoice included and global invoice not; keyset ordering and page-limit validation.
- `SummaryTests` (4): worked example (50% paid, both movements, explicit payment CFDI); confirmed PPD without complement has a due date and appears in the "missing" filter; cash warning without any state change; pending list excludes zero-payable invoices.
- `OverviewTests` (2): totals include classified movements and zero-payable invoices; overdue, cancellation, EFOS and global alert counts.
- `ReviewOpenApiTests` (1): the anonymous versioned document describes every endpoint, its security, roles and errors.
- `ReviewDocumentationTests` (3): ADR-009, the PR-12 build log entry and the README examples exist.
- `ReviewReadBenchmarks`: opt-in benchmark for the queue and overview.

### 5.4 Tasks ticked

`8.1`, `8.2`, `8.3`, `8.3b`, `8.3c`, `8.3d`, `8.4`, and the two long-pending `2.4` (roles scenarios) and `5.3` (audit
in the same transaction as decisions). `8.5` (UI) stays open for PR 8B.

### 5.5 Documentation and generated artefacts

PR-12 entry in the English and Spanish build logs (with the Word/PDF exports regenerated), a README section with
synthetic curl examples, refreshed database diagrams (English and Spanish), and regenerated status maps.

## 6. Verification

| Check | Result | Who verified |
|---|---|---|
| Build in the .NET 10 SDK container | 0 warnings, 0 errors | Codex, then Claude |
| `dotnet format --verify-no-changes` | Clean | Codex, then Claude |
| Full test suite | 607 total, 603 passed, 4 skipped (opt-in benchmarks), 0 failed | Codex, then Claude |
| Secret, real-RFC and attribution scan of the branch diff | Clean; no commit mentions Claude | Claude |
| Review benchmark, 20,000 invoices, 20,000 movements, 20,000 suggestions (1 warm-up, 3 samples) | Median 525.07 ms for the review queue (limit 200), 463.68 ms for the overview | Codex's report; not re-run by Claude |
| Populated migration Down then Up | Preserved 1 invoice, 2 movements, 2 links and all 6 audit hashes | Codex's report; not re-run by Claude |
| Live API smoke through `docker compose` | 50% paid, 5,800.00 pending, both links, additive overview totals, 7 consistent audit entries | Codex's report; not re-run by Claude |
| Cleanup | `down -v` completed; Claude later found no `bnk-` containers running | Codex, then Claude |

## 7. Limits and known gaps

- Reopening a rejected link and creating a manual many-to-many link are not supported (outside the spec's confirm,
  reject and change). The minimal UI (8.5) is the next PR.
- Benchmark timings are local observations, not capacity guarantees.
- Downgrading past M0012 deliberately refuses while decisions or historical duplicate link pairs exist; an operator
  must export or clean them explicitly.
- The API has no `/v1` path prefix; the document carries the version and a prefix will be introduced with a v2.

## 8. What the session shows about the workflow

- The plan fixed the architecture; Codex's four questions were all about gaps the plan could not know in advance
  (a schema constraint with a hidden second purpose, a wording slip, a downgrade policy, a smoke scenario).
- Two of the four questions exposed mistakes or ambiguities in Claude's own plan (the test wording, the smoke
  scenario); asking was cheaper than guessing.
- Test-first held throughout: the history is alternating `test:` and `feat:` commits, and the pending tenancy and
  audit scenarios became real tests.
- An agent stalling silently (the first attempt on a different model) was visible only by reading its pane; it was
  recovered by correcting the configuration, not by lowering the task.
