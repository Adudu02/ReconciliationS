# ADR-008: SAT client port, adapters and shared status policy

Status: accepted for the offline MVP. Task 7.6 is Phase 2: the configurable real transport is implemented, but one real anonymised payment CFDI and a live SAT answer are still required before claiming compatibility.

## Context and decision

`ISatClient.CheckAsync(SatStatusRequest, CancellationToken)` separates status consultation from reconciliation. Immutable records carry UUID, issuer RFC, receiver RFC and exact decimal total; an optional AnonymizedUuid is only a recording/replay key. Outcomes distinguish Answered, InvalidExpression and NotFound; typed transient/permanent exceptions distinguish availability from a refused contract. Every text mapping lives in `SatContract`; unknown values preserve their raw text and map to Unknown. No import or reconciliation invokes SAT inline.

Configuration selects `Sat:Mode=stub|real|replay`. Stub and real use the SAME SOAP HTTP adapter and differ only by configured endpoint; real requires HTTPS and defaults to `https://consultaqr.facturaelectronica.sat.gob.mx/ConsultaCFDIService.svc`. Endpoints never come from a route or payload. The named HttpClient refuses redirects and has a total request timeout, default 10 seconds; the TimeProvider-driven adapter timeout also bounds headers and body. XML prohibits DTD/external resolution and bounds input to 64 KiB, depth 16 and 1,024 nodes. The local minimal API speaks SOAP 1.1 Consulta and exposes local-only outcome controls on the internal Compose network; no stub/control port is published to the host.

## One mapping contract

SOAP action is `http://tempuri.org/IConsultaCFDIService/Consulta`, body operation Consulta, argument expresionImpresa. The printed expression is `https://verificacfdi.facturaelectronica.sat.gob.mx/default.aspx?id=<UUID>&re=<issuer RFC>&rr=<receiver RFC>&tt=<total>` with escaped RFC values. RFC and UUID syntax are validated before sending. Total uses invariant decimal text, exactly six fractional digits and at least ten integer digits, preserving larger integers up to numeric(18,2) magnitude: 1939.27 becomes `0000001939.270000`; zero becomes `0000000000.000000`. Negative totals and values with more than six significant fractional digits are rejected without rounding. This padding is validated against the documented format only; confirm it with real responses in Phase 2.

| Response field | Exact raw value | Stored interpretation |
|---|---|---|
| CodigoEstatus | S - Comprobante obtenido satisfactoriamente | answered; use mapped Estado |
| CodigoEstatus | N - 601: La expresión impresa proporcionada no es válida | permanent per-CFDI error; preserve current status |
| CodigoEstatus | N - 602: Comprobante no encontrado | not_found, distinct from cancelled |
| Estado | Vigente / Cancelado / No Encontrado | current / cancelled / not_found |
| EsCancelable | Cancelable sin aceptación / Cancelable con aceptación / No cancelable | without acceptance / with acceptance / not cancellable |
| EstatusCancelacion | empty / En proceso / Plazo vencido / Cancelado sin aceptación / Cancelado con aceptación / Solicitud rechazada | null / in process / deadline expired / cancelled without acceptance / cancelled with acceptance / rejected |
| ValidacionEFOS | 200 | clear, by the current contract assumption |
| ValidacionEFOS | nonempty value other than 200, ONLY in an actual successful code-S response | warning, retain exact raw value |
| Any mapped field | unrecognised text | unknown plus raw text; never guess an alias |

An empty or absent EFOS field, legacy data, backfill and non-answered results do not warn. Empty is retained as empty, absent as NULL. Whether absence has a different fiscal meaning remains open. “In process” and “deadline expired” do not cancel a current CFDI. Only Estado=Cancelado invokes the cancellation cascade. An unknown code cannot make an otherwise plausible state authoritative.

Sources checked: the [SAT consultation specification v1.4, November 2022](https://www.sat.gob.mx/cs/Satellite?blobcol=urldata&blobkey=id&blobtable=MungoBlobs&blobwhere=1461175223997&ssbinary=true), the [SAT published consultation document](https://www.sat.gob.mx/minisitio/Factura/documentos/cancelacion/consulta_cfdi.pdf), and the [phpcfdi sat-estado-cfdi implementation](https://github.com/phpcfdi/sat-estado-cfdi). These support the SOAP structure and EFOS 100/200 convention; they are not a live-response validation. Exact spelling/punctuation aliases, padded totals, empty EFOS and consultation of payment CFDI with total zero remain Phase 2 questions.

## Shared cache, retry and rate policy

Every adapter is wrapped by the same SatStatusLookup policy and company-scoped store. M0011 adds `sat_status_cache`, primary key (company_id, uuid), composite tenant FK to cfdi, FORCE RLS, SELECT/INSERT/UPDATE only, and a partial (company_id, expires_at) index for expiry scans. The primary key serves set-based batch lookup. Cancelled cache entries have expires_at NULL permanently; current defaults to 24 hours; not_found to 1 hour. A valid cache hit makes zero service calls. Unknown and expired observations can be rechecked. Raw code/state/cancellability/cancellation/EFOS, checked_at, errors and actual attempt count remain readable.

M0011 backfills ONLY already-cancelled CFDI, source=backfill, permanent expiry and migration observation time. That is not a fresh SAT answer: code_raw, cancellability, EFOS and other response raw fields are NULL. Current/unknown rows are not backfilled. Down removes the cache consistently and restores the preceding read functions; Up reconstructs the permanent legacy cancellation observation without making a service call.

Timeouts, connection errors and HTTP 5xx use exponential backoff plus 0–20% jitter, at most Sat:MaxAttempts (default 3). Typed permanent errors and 601 do not retry within a job. For 601 the cache keeps its prior state, writes last_error/attempts and expires immediately; without a cache row it creates unknown cache metadata without changing cfdi.sat_status. The job succeeds for that permanent per-CFDI error. last_job_id/last_error_kind suppress repeating it on retries of the SAME mixed batch; a new explicit job may recheck it. Exhausted transient attempts produce unknown status/error metadata, committed before the job's normal bounded retry is scheduled.

`IRequestRateGate` uses one thread-safe spacing gate driven by TimeProvider and Task.Delay. Default is 2 requests/second, one immediate permit, no catch-up burst after a clock jump. The SAME class runs under the manual clock and production clock. System.Threading.RateLimiting.TokenBucketRateLimiter was rejected because it cannot be driven by TimeProvider; fake-vs-real timing would otherwise differ. No package is added. The limit is per process: N workers can send N times that limit; worker job concurrency bounds concurrent calls. A distributed quota is a later deployment concern.

## Three short phases and durable outcomes

A status_check payload contains 1–100 distinct company-visible CFDI ids. Newly imported CFDI enqueue one-id batches in each existing per-file transaction; duplicate UUID uploads enqueue none. Keeping that transaction boundary avoids changing crash/redelivery guarantees. The on-demand accountant/administrator route also queues work and returns 202; viewers cannot enqueue, archived companies are read-only, foreign/unknown UUIDs reveal nothing.

1. Phase 1: short tenant read transaction loads CFDI and cache set-based, verifies the lease, commits and disposes.
2. Phase 2: no connection or transaction during bounded sequential HTTP/rate/retry work. Heartbeats continue. Sat:JobBudget defaults to 60 seconds and cancels the pending call when exhausted; no new calls start after it.
3. Phase 3: short company write transaction locks the company, rereads CFDI/cache set-based and discards superseded responses. Permanent cancelled or a changed/newer cache observation wins. Cache UPSERTs and ordinary CFDI updates use UNNEST batches. Newly cancelled observations invoke the existing 6B cascade exactly once. A lease fence runs BEFORE commit; a stale worker commits no cache, status, audit or follow-up writes. There is no HTTP after the write lock.

Ordinary answers use about two set-based writes per batch (one cache UPSERT statement, one CFDI UPDATE statement), plus one append_audit_entry call per changed CFDI, an extra sat.efos_flagged only for a changed nonclear EFOS answer, and the existing cascade commands/audits per newly cancelled CFDI. Read/lock/fence/progress/queue commands are additional constant batch overhead; no per-link read or network loop is introduced. Same-answer TTL refreshes do not add audit noise. System audit detail contains previous/new status, cancellation/cancellability/EFOS and jobId/source; typed items reference CFDI UUID and job, never RFCs or totals.

Real transient exhaustion returns a typed deferred-retry result: persist first, then FinishAsync after commit with normal job backoff, leaving the original payload untouched. Already successful ids are cache hits on retry. Budget-only remainder instead enqueues ONE follow-up with only unchecked ids, parent id and generation+1, and finishes the parent SUCCEEDED atomically inside phase 3. An active-parent unique index and the lease fence prevent duplicate children. Mixed failures+budget create no child and retry the parent normally. Sat:MaxFollowUpGenerations defaults to 20; at that cap the remainder uses bounded normal job attempts. Existing handlers retain their original single-transaction dispatcher path.

M0011 also guarantees FIFO for equal run_after/created_at timestamps using monotonic enqueue seq; lease-expiry recovery preserves the original due time with LEAST(run_after,p_now), created_at and seq. Scheduled transient retries keep their backoff. Crash recovery adds no new backoff and still stops at Jobs:MaxAttempts. Down restores the previous id ordering and p_now recovery body exactly.

## Private recording and network-free replay

Recording is an optional decorator over SOAP in stub/real mode. The automatic path returns the answer even if recording is skipped: missing/empty/equal-to-real key or a privacy failure increments a counter and logs one sanitized warning per process. The explicit RecordAsync helper requires a different nonempty anonymised UUID and throws ArgumentException if unsafe. Group 9 owns real-to-anonymised mapping; nothing derives a recording key from the real UUID.

Files contain ONLY raw mapped response fields and checkedAt, keyed by the anonymised filename: no request, RFC, total, real UUID or raw SOAP envelope. Before writing, reject response fields echoing the request UUID (D or compact form), either RFC or the formatted total. Writes replace files atomically. Recordings default to `.local/sat-recordings` (git-ignored and excluded from Docker build context); Compose uses a writable named volume at that path. Never commit real recordings. Only the allowlisted synthetic sample is committed. Real-mode recording emits a startup notice; recording=true with replay fails startup.

Replay reads explicit AnonymizedUuid, or uses request.Uuid as a replay-only fallback when automatic anonymous import checks have no key. Equality is allowed here: replay writes nothing and resolves no HTTP client. A different explicit key selects that recording. Missing/malformed/bounded-out files yield a clear “no recording for key” unavailable result through the shared unknown/retry policy, never network fallback. Stub/real with recording OFF discard supplied keys; supplied on-demand keys are administrator-only. Stored observations use the current check completion time, while the saved checkedAt remains recording provenance.

## Alternatives and limits

| Alternative | Decision |
|---|---|
| SAT calls inside import/reconciliation transactions | Rejected: availability would block core work and pin pool connections |
| Separate stub protocol / duplicated status rules per adapter | Rejected: drift from SOAP and inconsistent cache/retry behavior |
| Hold a tenant transaction during HTTP | Rejected: up to 100×attempts×timeout idle work would tie up the pool |
| Throw after writing unknown inside the transaction | Rejected: rollback would lose degradation/audits; use explicit deferred outcome |
| Built-in TokenBucketRateLimiter | Rejected for noninjectable clock; small TimeProvider gate has identical test/runtime semantics |
| Distributed cache or quota | Extra deployment state unnecessary for this single-process demo; reconsider for multiple workers |
| Commit raw SOAP recordings | Rejected for request echoes/privacy; response-only guarded records retain useful evidence |

The local stub is development-only, reachable by peers on its Compose network; its controls are not an authenticated production service. The SOAP transport and mappings are implemented but not certified against SAT. No real network call occurs in tests/CI. Fiscal/cancellation deadlines remain informational; no blocking fiscal validation or exchange computation is added. Group 8 builds decisions/queue UI; 6.11 and 7.6 stay open.
