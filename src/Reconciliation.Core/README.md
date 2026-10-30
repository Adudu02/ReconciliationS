# Reconciliation.Core

One class library, seven modules (design D1). The API and the worker are two hosts built on this library.

| Folder / namespace | Responsibility |
|---|---|
| `Ingestion` | CFDI and bank statement upload, parsing, idempotent import |
| `Matching` | Rule pipeline that links movements to CFDI, item states |
| `Review` | Review queue, decisions, summaries, overview |
| `Audit` | Append-only, hash-chained audit log |
| `Tenancy` | Companies, users, roles, company context (row-level security) |
| `Sat` | SAT status client port with stub, real and replay adapters |
| `Jobs` | PostgreSQL job queue, retries, failed-jobs list |
| `Common` | Database access, health endpoint, module contract |

Rule: modules talk through explicit interfaces, never through each other's tables.
