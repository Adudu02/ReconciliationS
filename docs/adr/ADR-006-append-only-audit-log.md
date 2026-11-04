# ADR-006 Append-only hash-chained audit log

Status: accepted. Implements group 5, design D3/D7/D11. Later tasks that proposed ADR-006 for cloud design must use the next free number.

## Decision and transaction boundary

Every tenant audit append goes through `append_audit_entry(company, actor_type, actor_id, action, items, detail)`. The function computes the timestamp, company_seq, prev_hash and hash itself. API and worker actions call AuditLog.WriteAsync inside the same CompanyScope transaction as the action: rolling back the action rolls back its entry and its head. Session/authentication events remain in auth_event, outside this company log; this supersedes the earlier ADR-004 expectation of integrating those events into group 5.

M0008 upgrades the existing audit_entry and backfills each company in created_at, id order. Each company has one audit_chain_head row. INSERT ON CONFLICT creates the empty head; SELECT FOR UPDATE locks it before reading last_seq/last_hash. A writer increments last_seq, inserts one entry and updates the head. Concurrent connections in a company serialize; different companies use different head rows. Appending is O(1) in chain length, plus the size of the new entry's items/detail. A statement containing many movements has a correspondingly larger audit payload.

## Canonical format and fixed vector

The exact canonical text is stored in audit_entry.canonical when the entry is written. Its format is a JSON array with eight ordered elements:

1. company_id as a lowercase UUID string.
2. company_seq as an unsigned base-10 string (starts at 1).
3. occurred_at as a signed base-10 string of **UTC microseconds since the Unix epoch**, calculated with PostgreSQL exact numeric extract(epoch) times 1000000.
4. actor_type: `user` or `system`.
5. actor_id as a lowercase UUID string, or the literal `system`.
6. action as a JSON string.
7. items as an array of typed references, each `{"kind":"cfdi","id":"<uuid>"}`; kinds currently include cfdi, movement, import_file, job and user. IDs are always nonempty strings. Several movements are several elements. Future link entries use kind link.
8. detail as a JSON object. The compatibility writer uses `{"old":...,"new":...}`; SQL system actors carry a nonempty jobId/source in detail.

PostgreSQL 17 jsonb output supplies the initial JSON spelling, escaping, key ordering and number spelling (including retained numeric scale). UTF-8 encoding has no BOM, newline or trailing bytes. Array order is significant; object key order is not a semantic field. The genesis prev_hash is exactly 32 zero bytes. For every entry:

```text
hash = sha256(UTF-8(canonical) || prev_hash)
```

Here `||` appends **32 binary bytes**, not hexadecimal text. The timestamp and detail are hashed through canonical; created_at/old_values/new_values are generated compatibility columns and never second sources of truth.

The fixed-vector canonical text below is one line (no final newline); prev_hash is 32 zero bytes:

```json
["00000000-0000-0000-0000-000000000001", "1", "1767225600123456", "user", "00000000-0000-0000-0000-000000000002", "synthetic.vector", [{"id": "00000000-0000-0000-0000-000000000003", "kind": "cfdi"}], {"new": {"n": 1.20}, "old": null}]
```

Timestamp: `2026-01-01T00:00:00.123456Z`. SHA-256: `6fe438cb1966bbf22f4db35bcdc14ff8a6de0c89c4b977e3edcce87ea301b2dd` (independently calculated with Python hashlib). AuditChainTests freezes both the exact text and digest.

Verification never hashes a new JSON serialization. It hashes the stored canonical bytes and checks that parsing canonical produces the same eight fields as the stored row. Therefore a PostgreSQL upgrade that changes jsonb text formatting does not invalidate existing entries. Changing a column while leaving canonical untouched is detected by the semantic comparison; changing canonical without updating the hash is detected by the byte comparison. An intentional Down/Up rebuilds chains; preserve/anchor head hashes before such an operational rollback if historical hashes matter.

## Permissions and refusal recording

cfdi_audit_owner is NOLOGIN, NOSUPERUSER, NOBYPASSRLS, NOCREATEDB, NOCREATEROLE, NOREPLICATION. It owns only append_audit_entry and has SELECT/INSERT on audit_entry and SELECT/INSERT/UPDATE on audit_chain_head, plus schema USAGE. It has no privileges on identity, company, imports, queue or other tables. cfdi_app is not its member. Both audit tables have enabled and FORCE RLS, so the audit owner is also constrained by the current company. The function explicitly refuses missing or mismatched app.company_id before touching a head.

The SECURITY DEFINER function pins search_path=pg_catalog,public,pg_temp, qualifies table/function references, has no dynamic SQL, revokes EXECUTE from PUBLIC and grants EXECUTE only to cfdi_app. pg_catalog comes first and pg_temp last. cfdi_app can SELECT under RLS but cannot INSERT, UPDATE, DELETE or TRUNCATE audit tables. BEFORE UPDATE OR DELETE and BEFORE TRUNCATE triggers reject mutations even for the table owner. Tests deliberately disable the trigger as the test owner only to demonstrate corruption detection, then roll back that transaction.

A refused mutation aborts its transaction; a trigger cannot durably record an attempt inside that same failed transaction. The internal guarded AuditLog.RecordModificationRefusedAsync seam accepts only an audit-mutation refusal, verifies a target seq visible in its company, and appends audit.modification_refused in a **separate transaction**. No HTTP endpoint can modify audit rows. This is defense plus a tested seam for future trusted code that catches a refused mutation, not an autonomous PostgreSQL record of arbitrary SQL attempts.

member removal does not exist yet; when added, its action and audit write must use the same tenant transaction and append function.

## Verification and read access

verify_audit_chain(company_id) is STABLE, read-only, SECURITY INVOKER and runs under caller RLS. A PostgreSQL cursor walks company_seq order, retaining one entry and the previous hash. It checks sequence contiguity, previous hash, semantic canonical fields, exact stored bytes, and finally last_seq/last_hash against the head. Time is O(n) per company; application memory does not grow with the whole log. A consistent result includes the entry count; an inconsistent result includes the number of successfully verified prefix entries, firstInconsistentSeq and a reason (sequence_gap, previous_hash_mismatch, canonical_invalid, canonical_mismatch, hash_mismatch or head_mismatch). A missing middle row reports the missing position; a truncated tail is detected using the head. An empty log is consistent with zero entries.

GET /companies/{companyId}/audit-entries accepts inclusive from/to bounds, actor UUID or system, action, item, optional itemKind, afterSeq and limit (default 50, maximum 200). It reads at most limit+1 rows, returns entries plus nextAfterSeq, and orders by company_seq. Equal timestamps cannot produce paging gaps. GET /companies/{companyId}/audit/verification returns consistent, entries, firstInconsistentSeq and reason. All current member roles may read archived companies. The spec's auditor maps to viewer. Both routes require Bearer authentication and matching X-Company-Id; they check current membership inside the same RLS transaction and return the same empty 404 for absent and inaccessible companies. They are registered with TenantIsolationChecks. Other company routes retain their existing authorization behavior.

## RLS query-plan limitation and measured cost

The unique (company_id, company_seq) index serves the chain and paging. (company_id, occurred_at, id) serves chronological/date-range reads. Typed items use the parameterized containment predicate `items @> '[{"id":"value"}]'`; adding kind narrows the same reference. JSON construction handles escaping; no user text is interpolated into SQL.

An experimental GIN(items jsonb_path_ops) index was **removed**. PostgreSQL documents the [RLS evaluation boundary](https://www.postgresql.org/docs/17/ddl-rowsecurity.html), [LEAKPROOF function requirements](https://www.postgresql.org/docs/17/sql-createfunction.html) and [JSONB index operator classes](https://www.postgresql.org/docs/17/datatype-json.html#JSON-INDEXING). With FORCE RLS, jsonb containment @> is non-LEAKPROOF and remains a post-RLS filter for cfdi_app. ANALYZE does not change that boundary. Maintaining an index that runtime reads cannot use adds write cost. The app first narrows through a company index, then filters items: item search is O(n) in that company's entries at this demo's scale. No speculative (company_id, action) index is added without a measured need.

AuditHttpTests populates more than 20,000 entries across three companies, ANALYZEs the table, then EXPLAINs as cfdi_app with enable_seqscan=off. It asserts company-index narrowing, a filter, and no sequential scan. AuditBenchmarks measures actual HTTP item lookup and verification at 20,000 and 100,000 company entries (one warmup, three lookup samples); observations live in the PR-8 build logs. Opt in with RUN_AUDIT_BENCHMARKS=1 through the SDK container.

For an owner-level diagnostic only, the benchmark creates a temporary-in-transaction GIN index, runs this EXPLAIN, and rolls back the index:

```sql
CREATE INDEX audit_items_demo_idx ON audit_entry USING gin(items jsonb_path_ops);
SET LOCAL enable_seqscan=off;
EXPLAIN SELECT id FROM audit_entry WHERE items @> '[{"id":"100000"}]';
-- Bitmap Heap Scan on audit_entry
--   Recheck Cond: (items @> ...)
--   -> Bitmap Index Scan on audit_items_demo_idx
--        Index Cond: (items @> ...)
```

That owner plan proves index capability without RLS, not application performance. Do not mark built-ins LEAKPROOF, weaken policies or bypass RLS to force a plan. If deployments require indexed search, evaluate a read-only SECURITY DEFINER search function with an audit-only restricted owner, explicitly enforced company predicate/context and equivalent isolation tests (the queue capability trust pattern), or per-company partitioning. Neither escape hatch ships here.

## Alternatives and limits

| Alternative | Decision |
|---|---|
| triggers-only | Rejects ordinary mutations but provides no evidence of privileged changes or missing rows; retained only as defense in depth |
| app-level hashing | Leaves SQL writers able to choose sequence/hash and can fork under concurrency unless every caller implements identical locking; rejected in favor of one database append boundary |
| external ledger | External anchoring can resist whole-database forgery, but a separate write loses atomicity with domain changes without an outbox; future work |
| event sourcing | Replaces the domain persistence model and adds replay/versioning work outside this slice |

A database superuser, or an owner with sufficient DDL privileges, can disable triggers, rewrite the entire chain and head, and forge a consistent result. External anchoring of head hashes is future work. Hashes detect tampering relative to trusted heads; they do not authenticate a compromised application actor or give fiscal/legal validity. Company-wide serialization bounds one company's append throughput; independent companies do not share its lock.

M0008 Down preserves rows and readable legacy columns. After dropping generated expressions it packs actor type, typed items and full detail into reserved new_values._audit_v8 metadata; a later Up restores that metadata before re-chaining. Older legacy fields remain readable. Group-5 system rows keep nullable actor_id during downgrade because a system actor is not an app_user. Tests cover populated up/down/up, including a system entry and its typed reference. Down/Up is a migration compatibility operation, not a tamper-preserving archive.

Group 5.3 remains unchecked: real automatic links, user decisions/classification and SAT transitions do not exist until groups 6/7/8. Three explicit pending audit tests name those scenarios. Member added/changed, company created/settings/archive, per-file imports and job retry ship now. There is no new member-removal route and no DELETE route.
