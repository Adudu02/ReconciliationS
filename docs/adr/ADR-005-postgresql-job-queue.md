# ADR-005: PostgreSQL job queue with transactional enqueue

Status: accepted. ADR-004 is session renewal; task 4.5's ADR-004 wording predates that change. This decision uses the next free number.

## Context and decision

Imports, reconciliation and SAT status checks need durable background execution, retries, visible progress and recovery after worker death. One developer operates a local demonstration on PostgreSQL 17. Keep transactional enqueue in the caller's CompanyScope transaction, together with every queued import_file row. No database/queue dual write is introduced.

M0007 adds company-scoped job and job_attempt tables with enabled and forced RLS. The application role cfdi_app remains NOSUPERUSER NOBYPASSRLS. A partial unique index permits one queued/running reconciliation_run per company. Claim order is run_after, created_at, id, using FOR UPDATE SKIP LOCKED. Claims record an attempt before handing work to a handler. Bounded reaping returns abandoned leases to queued or failed when attempts are exhausted. Renew and finish fence by job id, worker id, attempt number and unexpired lease. Handlers run inside CompanyScope and imports additionally check the lease inside each per-file transaction.

Transient timeouts, deadlocks, serialization and connection failures retry with capped exponential backoff plus CSPRNG jitter. Invalid data and validation failures fail immediately. Unknown exceptions fail conservatively with safe text. History, input_ref and payload remain for an audited administrator retry; retry adds a fresh bounded attempt budget without deleting earlier attempts. Shutdown cancels handlers and leaves unfinished claims recoverable. Every lease, deadline and retry calculation uses injected TimeProvider. Queue functions accept optional p_now (default clock_timestamp()) for SQL callers; the worker always supplies injected UTC. No HTTP endpoint accepts or exposes p_now.

## Why a restricted SECURITY DEFINER boundary

Workers must find any company's work without giving the application a general RLS bypass. A dedicated cfdi_queue_owner is NOLOGIN, NOSUPERUSER, BYPASSRLS, with SELECT/INSERT/UPDATE privileges only on job/job_attempt and USAGE on public. It owns only the claim/renew/finish functions. It has no grants on company, identity, imports or other domain tables, and cfdi_app is not its member. The ordinary app role can EXECUTE these functions, cannot bypass RLS when selecting tables, and has no DELETE privilege.

Each function is SECURITY DEFINER with pinned search_path=pg_catalog,public,pg_temp, qualified public table names, no dynamic SQL, EXECUTE revoked from PUBLIC and granted only to cfdi_app. Bodies touch only job/job_attempt and derive the company from the claimed row. Public is not writable by the application. Migrator/Compose/CI use the PostgreSQL owner capable of creating the restricted BYPASSRLS role; this is deliberately distinct from runtime credentials. The isolation guard verifies exactly this one non-superuser bypass role and no domain grants. The application credential still has a privileged global queue capability, so it must remain private to server processes; this does not authenticate human users.

## Retained input and idempotency

Queued originals live in transient pending_original, permitted only for queued/processing files and checked against sha256. They are not returned by any endpoint. Accepted finalization moves bytes into immutable original and clears pending_original in the same UPDATE; retention starts at acceptance. Rejection clears pending bytes and keeps hash/reason. The immutability trigger permits original NULL-to-bytes only from queued/processing to imported; committed accepted originals and their hashes cannot change and retention cannot shrink. A file_index preserves upload order now that files are inserted in one transaction.

Processing status is private to each per-file transaction: a failure rolls back to queued. Completed files retain their stable ids; redelivery skips their domain and audit writes. Remaining files of exhausted jobs stay queued with pending bytes until an administrator reruns successfully. Cleanup is outside this MVP; D15 permits no delete path. Down refuses when queued/processing input remains rather than discard it, and otherwise restores the earlier retention trigger and removes queue objects and grants. Migrations up/down/up are verified on a disposable database.

## Alternatives

| Option | Benefit | Cost / reason rejected for this demo |
|---|---|---|
| PostgreSQL queue | Atomic domain change plus enqueue, familiar backups and SQL inspection, no service added | Polling, table/index churn, shared database capacity; application must implement leases and idempotent handlers |
| Redis queue | Mature worker libraries, fast scheduling | Extra service and durability configuration; database change and enqueue need an outbox to avoid lost work |
| RabbitMQ broker | Routing, consumer acknowledgements and isolation from database load | Extra operations and an outbox; more than this slice needs |
| SQS | Managed durability, visibility timeouts and scale | Cloud dependency, at-least-once delivery still needs idempotency and an outbox; suitable production migration target |

## Volume limit and migration path

The conservative **volume limit is 100 jobs/minute sustained**, with an operating backlog budget of 10,000 jobs and default two concurrent lanes per worker. These are review thresholds for this demo, not measured PostgreSQL capacity claims or an admission-control limit. Reassess earlier if p95 claim latency exceeds one second, queue wait exceeds one minute, or queue writes materially affect interactive queries. No formal load test is claimed.

The migration path is a transactional outbox committed with the domain change, followed by a publisher to SQS (or RabbitMQ when routing is needed). Keep stable job/input ids, progress and attempt history in PostgreSQL, use the broker only for delivery, retain idempotent handlers, and replace lease transport behind the worker dispatch boundary. Drain or bridge the old queue before switching consumers; do not dual-publish without an idempotency key. Retention and tenant transactions stay unchanged.

## Scope and verification

The API returns 202 with jobId/uploadId after streaming limits and atomic persistence. Viewers read job progress; accountants enqueue; administrators inspect failed jobs and retry with an audit entry. Archived companies refuse new writes. Four handlers are registered: real import_cfdi/import_statement, and explicit reconciliation_run/status_check placeholders recording “not implemented until group 6” / “not implemented until group 7” while succeeding without domain effects.

Tests use real PostgreSQL, application credentials and a fake clock: transactional rollback, concurrent distinct claims across companies, retry histories, heartbeat/reaper fencing, real import redelivery and crash recovery, retained input, API roles and isolation. A manual Compose worker-kill demonstration supplements deterministic CI tests; its commands and observed result are recorded in the PR-7 build log.
