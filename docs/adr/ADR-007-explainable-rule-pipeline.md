# ADR-007: Explainable rule pipeline for matching

- Status: Accepted
- Date: 2026-11-04
- Scope: group 6A, tasks 6.1–6.3, 6.2b, 6.5–6.10

## Context

The accountant needs to see why a bank credit was linked to an issued income CFDI. The worked example has an invoice
of 11,600.00, a first movement of 5,800.00 with a payment complement, and a second movement of 5,800.00 described as
`SPEI REC 0071 XEXX COMERCIAL`. Only the first is deterministic. Data, decisions and an unchanged rerun must preserve
amounts exactly and must never silently replace a user's decision. All monetary calculations use C# decimal and
PostgreSQL numeric, never floating point.

## Decision

`MatchingEngine.Run` is a pure in-memory function. Inputs are movements, issued invoices, payment/complement documents,
existing links and company settings; outputs are new links, explicit before/after updates, computed invoice balances
and a candidate-comparison counter. It has no database dependency. `reconciliation_run` loads tenant data with set-based
SQL, calls the engine, persists batches of 500 links with `unnest`, and appends one audit entry per change in the SAME
company transaction. M0009 supplies forced RLS, composite tenant foreign keys, null-safe link uniqueness and no DELETE
grant. Nothing stores an item state: `movement_state()` and `cfdi_state()` derive it from active link amounts.

The handler locks the company row, so imports, settings/classification changes and matching serialize for that company.
Archived companies remain readable but cannot run matching or change classification. Job progress uses short separate
transactions, as imports do, so a progress write cannot hold up heartbeat renewal for the entire run. A final lease fence
in the domain transaction refuses stale work before commit; links and their audit entries roll back together. Progress
is advisory while a job is running, and the job's terminal status determines success.

### Stage 1 deterministic evidence

Only credit movements and type I, issued, MXN invoices participate. Received CFDI, debits and classified movements are
excluded. A type P issued payment complement version 2.0 must reference known invoice UUIDs; its amount must equal the
movement credit exactly, its payment date must be within `date_tolerance_days`, and its positive consistent MXN document
amounts must add up to the payment amount and fit invoice capacity. There is no amount tolerance in this stage.
Several payment documents may allocate one movement across invoices. These links are automatic/reconciled, with a
payment-complement criterion and nullable confidence.

Stage 1 reserves only reconciled links and active user decisions. Automatic suggestions are provisional and reserve
nothing against deterministic evidence. A later complement for the same movement/invoice upgrades the suggestion in
place (`link.upgraded`, preserving its id and recording previous values). A different invoice closes the superseded
suggestion and creates a reconciled link. `closed` is retained from the link state diagram as historical supersession;
this extends its existing user-replacement use to the explicitly approved automatic supersession. Reconciled links,
user-origin links and non-reopened user rejections are never overwritten. A rejected movement/invoice pair blocks any
later complement for that pair, independent of `payment_cfdi_id`.

After stage 1, sort provisional suggestions by lowest confidence, newest creation time, then stable id (with the stable
movement/invoice/payment tuple for pure inputs without a database id). An O(L log L) sort followed by an O(L) scan closes
excess suggestions until both invoice and movement capacities fit, with `link.closed` and reason
`capacity taken by deterministic link`. The comparer never relies on input order. The closed history remains visible.

### Stage 2 explainable suggestions

A candidate must pass an eligibility gate: it has an amount criterion OR a strong identifier (serie/folio, a UUID
fragment of at least eight hex characters, or a reference equal to a complement operation number with equal amounts).
Date alone, RFC alone, or RFC plus date with an unrelated amount cannot make a candidate. There is no name criterion.
RFC and date only add points after the gate. Identifier-only partial payments use the smaller of the movement's remaining
credit and the invoice's remaining payable, with the explanation `identifier without amount match`.

Exactly ONE amount criterion applies, in this precedence order. Independent evidence adds to that one amount score.

| Criterion | Points | Meaning |
|---|---:|---|
| Exact net total | 40 | Credit equals CFDI total after discount and retentions |
| Exact gross target | 35 | Credit equals total plus federal and local retained taxes |
| Exact remaining payable | 30 | Credit equals a partial remainder strictly below total |
| Within amount tolerance | 25 | Near total, gross or partial remainder when none matches exactly |
| Date within tolerance | 20 + 0–10 | Integer proximity bonus rounded to nearest, maximum on the same date |
| Issuer/receiver RFC in description | 15 | One RFC bonus, even if both RFCs occur |
| Serie/folio or UUID fragment in text | 30 | One identifier bonus |
| Reference equals operation number | 30 | Complement payment amount must equal movement credit |

The single Retained engine input is the exact numeric sum of declared retained_tax and local_retained_tax (COALESCE nulls to zero).
At the SQL boundary, persisted gross criteria and explanations list both components separately, e.g. federal 0.00, local 10.00;
the same detail is hashed into the system audit entry. “Payer did not withhold” means neither category was withheld,
consistent with D14's stored net total. Local-only and mixed-retention integration tests fix this interpretation.
Within-tolerance detail identifies the closest target and the exact difference in cents. The gross target, exact or
near, includes the reason `amount equals total before retentions`; if retained taxes are zero, the total target wins
and no gross reason appears. Amount targets are never added together. Confidence is high at 70+, medium at 45+, low at
25+; below 25 creates nothing. All stage-2 links stay suggested, including high-confidence gross payments.

Process stage 1 before computing remaining payable for stage 2. Within stage 2, remaining capacity also subtracts prior
suggestions in the same run. Choose by score descending, date distance ascending, stable invoice id ascending. Each
movement gets at most one suggestion; both invoice and movement capacities bound its applied amount. Exact tied counts
are shown for the inspected candidate set; when retrieval is truncated the explanation states `50+ others may have
matched equally` (the injected K replaces 50 in tests). A suggestion whose evidence disappears stays until a user acts.
An existing active link excludes that movement from further automatic suggestions; leftover credit remains visible as
uncovered for manual N:M allocation in group 8. Explicit reopening permits reconsideration. Unchanged reruns create no
links or updates, including after an upgrade or capacity cleanup.

For the actual second worked-example movement, amount equals remaining payable contributes 30 points. Its description
has only a customer-name fragment, not the full RFC, so this is a correct low-confidence suggestion; no invented name
criterion or lowered threshold is necessary.

### Bounded retrieval and cost

Index invoices by exact cent amounts for total, gross and partial remainder, UUID, normalized serie/folio and UUID
fragments. Each amount bucket has invoices sorted by date then stable id, and a second date-sorted sub-index per
issuer/receiver RFC. Payments have an amount/date index and operation-number postings. Tokenize each movement once;
normalized serie+folio is indexed, a folio alone is indexed only when it has at least four digits, and UUID windows have
at least eight hex characters. No database text index is needed. Pre-M0009 rows have NULL serie/folio, so they lack that
criterion; retained originals permit a later backfill, but UUID-idempotent re-import never rewrites the row.

`MaxCandidatesPerMovement = 50` (K) caps retained amount candidates. For each queried amount bucket retrieve nearest dates
from the ordinary index and the matching amount/RFC sub-index; RFC posting lists are NEVER independently enumerated.
Merge those ordered streams by their amount/date/RFC score, then add direct strong-identifier posting hits. The normal
stream covers the best non-RFC candidate and the sub-index covers the best RFC candidate. With a single amount score,
base points are constant within a target bucket and date points are monotone in distance; the RFC bonus is constant for
the matching sub-index. Thus their union contains the true best under score/date/id order, including an RFC candidate
beyond K nearer RFC-less invoices. Strong text/operation evidence arrives directly. Farther equal candidates may appear
only in the `K+ others` notice. Small-K tests compare the result against an independent brute-force full scan.

Company amount tolerance is capped at 1.00 (100 cents), validated by the settings API and a DB CHECK; the engine also
clamps its effective tolerance. It probes B = 2*100+1 <= 201 cent buckets per amount target (B = 3 at the default 0.01),
never a segment tree or an unbounded range scan. Gross/remainder targets add a constant factor. Date lookup and merging
avoid scanning dense equal-amount buckets. Scored comparisons per movement are bounded by B*2K plus direct postings
hits (the implementation retains at most K merged amount candidates); tombstones/overflow probes are filtered before
scoring. Index maintenance removes fully allocated invoices and updates remainder buckets.

Stage-1 payment invalidity is monotone because provisional suggestions do not reserve its capacity. Lazy deletion
removes each exhausted/used payment once, after disposing lookup enumerators; K counts usable candidates rather than a
dead prefix. Total removals are O(C + P), with ordered-index maintenance inside the overall logarithmic factor.

The guaranteed indexed-engine bound is linearithmic, O((M + C + P) log n + M*(B log n + K)), plus direct posting hits and
existing-link processing O(L log L) for deterministic cleanup. B and K are constants. Direct strong identifiers can
legitimately have many posting hits; that explicit term is not hidden in the bound. No claim of linear total cost is made.
CI checks a dense 2,000 x 2,000 case using comparison counts rather than elapsed time. The opt-in benchmark uses 20,000
unique-amount movements/invoices and a dense 5,000 x 5,000 case, one warmup plus three measured runs; PR-9 records medians.

### Balances classification and audit

Payable is the stored, validated CFDI net total. `pendingAmount = payable - reconciled`, floored at zero;
`uncoveredAmount = payable - reconciled - suggested`, floored at zero. The retention scenario's suggested link fully
covers 1,939.27, so uncoveredAmount is zero while pendingAmount stays 1,939.27 until confirmation or a complement. This
is a spec tension with “no amount pending”; it follows the explicit partially-paid scenario's confirmed-only pending
meaning. A later product decision may choose which computed value that phrase means.

Classification has its own field and optional note; it never stores an item state. An accountant/administrator may
set loan, partner_contribution, tax_payment, late_invoicing or other, or clear with explicit null. Classified movements
leave the pending list and the engine. Clearing reopens matching. Any member can read chronological keyset pages and
filter by state/classification; archived companies stay readable. `movement.classification_changed` records the user
and before/after values in the action transaction.

System link entries use ADR-006's append function, literal system actor and root jobId/source in detail. Items are typed
references `[{kind,id}]`: movement id, CFDI UUID and link id; criteria and prior values live in hashed detail. Session and
authentication events remain in auth_event. Audit task 5.3 remains open until later user decisions/SAT events exist.

## Alternatives considered

- A single opaque score would hide individual reasons and make threshold changes hard to review. The additive rubric
  keeps an explicit criteria list, but it is still a demo policy that needs accountant feedback.
- A learned model needs labeled data, drift monitoring and an explanation policy that this synthetic demo cannot justify.
  It may later rank review candidates while deterministic confirmation remains separately guarded.
- Database-driven matching would tie domain tests to SQL and make property testing harder. A pure engine with tenant SQL
  orchestration keeps rules independently testable without granting cross-company privileges.
- Unbounded amount/date scans fail on recurring equal invoices. Nearest-date buckets plus RFC sub-indexes and direct
  strong identifiers retain deterministic ranking without heaps of representatives or a segment tree.

## Limits and follow-up

PR 6B owns credit notes (6.4), foreign currency (6.11), complement due dates (6.12), overpaid flags (6.13), global invoices
(6.14) and SAT cancellation/replacement cascades (6.15). These tasks stay unticked. No speculative FX columns are added.
User confirmation/rejection/reopening and manual N:M endpoints belong to group 8. Matching uses current settings only on
the next run and does not erase prior suggestions. It loads a company's matching inputs into memory once; deployment
volumes beyond the measured demo need profiling and possibly partitioned runs. Classification does not delete evidence.
