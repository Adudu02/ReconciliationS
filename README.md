# CFDI and bank statement reconciliation platform

Portfolio demo (MVP core). A multi-company backend that ingests CFDI 4.0 XML and bank statement CSV files,
proposes which bank movement pays which invoice, sends doubtful matches to human review and keeps a
tamper-evident audit log. Results have **no fiscal or legal validity**; use anonymised or synthetic data only.

Stack: C# on .NET 10, ASP.NET Core, PostgreSQL 17 (see `docs/adr/ADR-002-stack-choice.md`).
Planning documents: `docs/` (BRS, SRS, diagrams, ADRs) and `openspec/changes/mvp-reconciliation-core/` (source of truth).
Progress and what each piece is for: [`docs/BUILD-LOG.md`](docs/BUILD-LOG.md) (English) and [`docs/BUILD-LOG-ES.md`](docs/BUILD-LOG-ES.md) (Español); status map: `docs/progress/status-map.png`.

## Layout

| Path                              | Contents                                                                  |
| --------------------------------- | ------------------------------------------------------------------------- |
| `src/Reconciliation.Core`         | The seven modules: Ingestion, Matching, Review, Audit, Tenancy, Sat, Jobs |
| `src/Reconciliation.Api`          | HTTP API host (ASP.NET Core minimal APIs)                                 |
| `src/Reconciliation.Worker`       | Background worker host (same library, separate process)                   |
| `src/Reconciliation.Migrator`     | Database migrations (FluentMigrator), `up` and `down`                     |
| `src/Reconciliation.SatStub`      | Local SAT status stub (its own container)                                 |
| `tests/Reconciliation.Tests`      | xUnit tests                                                               |
| `tools`                           | Test-data tooling (anonymiser, generators, leak scan)                     |
| `docs`                            | BRS, SRS, diagrams and architecture decision records                      |

## Run everything (Docker)

```bash
docker compose up --build --wait
docker compose ps                        # every service healthy
docker compose down                      # add -v to drop the database volume
```

Compose starts PostgreSQL, runs the migrations once, then starts the API (http://127.0.0.1:8080/health), the
worker (health endpoint on 3001 inside the Compose network only) and the SAT stub (8081). Published ports are
bound to 127.0.0.1.

## Develop locally

Install the .NET 10 SDK (Fedora: `sudo dnf install dotnet-sdk-10.0`) and start a database
(`docker compose up -d postgres`).

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Username=cfdi;Password=cfdi;Database=cfdi"
dotnet build                                             # warnings are errors
dotnet format --verify-no-changes                        # style check
dotnet test
dotnet run --project src/Reconciliation.Migrator -- up   # or `down` to revert one migration
dotnet run --project src/Reconciliation.Api
```

No SDK on the machine? Run any command inside the official image, for example:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

## Decisions

Architecture decision records are in `docs/adr/`.
