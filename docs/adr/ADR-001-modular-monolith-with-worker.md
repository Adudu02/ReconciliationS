# ADR-001: Modular monolith with a separate worker process

Status: accepted (author's call, design D1)

## Context
One developer, one to two weeks, and an audience that judges architecture. The system needs asynchronous
imports and reconciliation, retries, and the ability to show a worker crash and recovery.

## Decision
One codebase organised in seven modules (ingestion, matching, review, audit, tenancy, sat, jobs) with two
entry points built from the same code: the HTTP API and the worker. They share one image and one database.
Modules talk through explicit interfaces and never read each other's tables.

## Alternatives rejected
- **Microservices:** operational cost (deployments, networking, tracing) with no benefit at this size.
- **Single process with in-process jobs:** cannot show worker failure, recovery or scale-out, which the
  job-processing spec requires.
- **Serverless functions:** hides the queue and transaction behaviour this project wants to demonstrate.

## Consequences
- Boundaries stay visible and testable inside one repository.
- The worker scales out on its own (several containers, same image).
- Splitting a module into a service later is possible because modules already use interfaces.
