## Purpose

Runs imports, reconciliation and status checks in the background so that users are never blocked, and so that failures are retried without losing or duplicating work. User story: as an accountant, I want to start a large import and keep working so that I am not waiting on the screen.

## ADDED Requirements

### Requirement: Asynchronous execution with visible progress
The system SHALL execute imports, reconciliation runs and status checks as background jobs, SHALL return immediately with a job identifier, and SHALL let users see each job's state (queued, running, succeeded, failed) and progress.

#### Scenario: Start an import
- **WHEN** a user uploads files
- **THEN** the system responds at once with a job identifier and the job's state changes from queued to running to succeeded

### Requirement: Idempotent handlers
The system SHALL ensure that running the same job more than once, including after a crash or a redelivery, yields the same final data as running it once.

#### Scenario: Redelivered job
- **WHEN** a job is delivered twice to workers
- **THEN** its effects are applied once and the audit log shows no duplicate links or imports

### Requirement: Retries with backoff
The system SHALL retry a failed job with increasing waits up to a configured number of attempts, and SHALL distinguish transient failures (retry) from permanent ones (fail at once).

#### Scenario: Transient failure then success
- **WHEN** a job fails on its first attempt because of a temporary error and succeeds on the second
- **THEN** its final state is succeeded and both attempts are recorded

#### Scenario: Permanent failure
- **WHEN** a job fails because its input is invalid
- **THEN** it is marked failed without retries and the reason is shown to the user

### Requirement: Failed jobs are retained
The system SHALL move a job that exhausts its attempts to a failed-jobs list that keeps its input and error details, and SHALL let an administrator re-run it.

#### Scenario: Exhausted job
- **WHEN** a job reaches its maximum attempts
- **THEN** it appears in the failed-jobs list with its error history and can be re-run by an administrator

### Requirement: No loss on worker failure
The system SHALL ensure that a job being processed when a worker stops is picked up again by another worker.

#### Scenario: Worker killed mid-job
- **WHEN** a worker process is terminated while processing a job
- **THEN** the job is processed to completion by another worker and its effects are applied once

### Requirement: Scale out
The system SHALL allow several workers to process jobs concurrently without processing the same job simultaneously and without violating company isolation.

#### Scenario: Two workers
- **WHEN** two workers run against a queue of jobs from two companies
- **THEN** each job is processed by exactly one worker and each worker only touches the data of the job's company

### Requirement: Reconciliation run on demand
The system SHALL let an authorised user start a reconciliation run for a company as a background job, SHALL return the existing job when a run for that company is already queued or running, and SHALL support the job types import CFDI, import statement, reconciliation run and status check.

#### Scenario: Run requested twice
- **WHEN** a user requests a reconciliation run while one is running for the same company
- **THEN** the system returns the running job and starts no second run
