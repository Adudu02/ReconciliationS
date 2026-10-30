# ADR-002: C# on .NET 10, ASP.NET Core and PostgreSQL

Status: accepted. Project owner's choice; it replaces the TypeScript / NestJS proposal of the project brief.

## Context
The brief proposed TypeScript with NestJS. The owner chose C# instead, to build the demo with a language they
want to learn and that fits the Mexican accounting market. The domain needs exact money, strong constraints,
N:M relations, transactional queues and database-level tenant isolation.

## Decision
- **Language and runtime:** C# on .NET 10 (long-term support).
- **Web:** ASP.NET Core minimal APIs, one endpoint group per module, with the built-in OpenAPI document.
- **Processes:** the API and the worker are two ASP.NET Core hosts that share one class library
  (`Reconciliation.Core`). The worker hosts background services and a small health endpoint.
- **Database:** PostgreSQL 17, accessed with Npgsql and Dapper. The SQL stays explicit, so row-level security
  policies, triggers and the queue are visible. No ORM.
- **Migrations:** FluentMigrator with versioned migrations that can go up and down, run by a separate
  migrator tool.
- **Tests:** xUnit, FsCheck (property-based), Testcontainers (a real PostgreSQL per test run) and
  WebApplicationFactory for API tests.
- **Quality gates:** .NET analyzers with warnings as errors, and `dotnet format` verification in CI.
- **Money:** `decimal` in code and `NUMERIC(18,2)` in the database; never floating point.

## Alternatives rejected
- **TypeScript with NestJS:** the brief's proposal. There is no native decimal type, there are fewer CFDI
  libraries, and the first scaffold showed tooling friction: NestJS 12 packages are ESM-only and Jest could
  not load them, so the scaffold had to fall back to NestJS 11 and TypeScript 5.9.
- **Java with Spring Boot:** a strong enterprise signal and the quickest start (a JDK was installed), but
  heavier for a one to two week scope. It stays the fallback.
- **Go:** strong cloud signal, but more code for the rules engine and weaker XML and SOAP libraries.
- **PHP:** the best CFDI ecosystem (`phpcfdi`), but a weaker signal for architecture roles.
- **Ruby on Rails:** fast to write, same trade-off as PHP.
- **Elixir with Phoenix:** the best fit for worker supervision and Oban, but a steep learning curve and almost
  no CFDI or SAT libraries, which would push the delivery past two weeks.
- **EF Core:** convenient, but it hides the row-level security and trigger work this demo should show.

## Consequences
- The author is new to C#, so the first tasks will be slower while patterns settle.
- The same ideas (modular monolith, queue in PostgreSQL, hash-chained audit) carry over unchanged; only the
  implementation language differs.
- The earlier TypeScript scaffold was discarded; no project data depended on it.
