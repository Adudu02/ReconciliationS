## Purpose

Checks whether a CFDI is current or cancelled with the SAT status service, through an interface that has a local stub first and the real service later, without changing reconciliation behaviour. User story: as an accountant, I want cancelled invoices detected so that I never reconcile against an invalid document.

## ADDED Requirements

### Requirement: Status check contract
The system SHALL query status with the issuer RFC, receiver RFC, total and UUID, and SHALL interpret the response fields: code (N 601 invalid expression, N 602 not found, S success), state (for example current, cancelled, not found), cancellability, cancellation status (including in process and deadline expired) and the EFOS validation value.

#### Scenario: Current invoice
- **WHEN** the service answers success with state current
- **THEN** the system stores the status as current with the time of the check

#### Scenario: Cancelled invoice
- **WHEN** the service answers success with state cancelled
- **THEN** the system stores the status as cancelled and triggers review of its links

#### Scenario: Not found
- **WHEN** the service answers N 602
- **THEN** the system stores the status as not found and shows it distinctly from cancelled

#### Scenario: Invalid expression
- **WHEN** the service answers N 601
- **THEN** the system records an error for that CFDI, does not change its status and does not retry the same request

### Requirement: EFOS warning
The system SHALL show a warning on any CFDI whose issuer's EFOS validation value is anything other than clear, and SHALL store the raw value returned.

#### Scenario: Flagged issuer
- **WHEN** the response carries the EFOS flag for the issuer
- **THEN** the CFDI is shown with a warning and the event is recorded in the audit log

### Requirement: Local stub reproducing the contract
The system SHALL provide a local stub implementing the same request and response contract as the real service, and SHALL let tests force each outcome (current, cancelled, not found, invalid expression, EFOS flagged, timeout, server error, slow response).

#### Scenario: Forced timeout
- **WHEN** a test configures the stub to time out for a given UUID
- **THEN** the client treats it as an unavailable service for that check

### Requirement: Switch by configuration
The system SHALL select the stub or the real service by configuration only, and the reconciliation logic SHALL be identical in both cases.

#### Scenario: Switch to the real service
- **WHEN** the configuration selects the real service
- **THEN** the same checks run against the real endpoint with no code change

### Requirement: Record and replay
The system SHALL be able to record real responses once, locally, and replay them deterministically for tests and the demo, associating each recorded response with the anonymised record that stands for the real CFDI.

#### Scenario: Replay for anonymised data
- **WHEN** the status of an anonymised CFDI is checked in replay mode
- **THEN** the system returns the response recorded for its real counterpart and makes no network call

### Requirement: Caching
The system SHALL cache a cancelled status permanently and a current status for a configurable period, and SHALL NOT query the service again for a cached status.

#### Scenario: Cached current status
- **WHEN** the same CFDI is checked twice within the cache period
- **THEN** only the first check calls the service

### Requirement: Retries and graceful degradation
The system SHALL retry transient failures with increasing waits and a bounded number of attempts, SHALL never block imports or reconciliation because the service is unavailable, and SHALL mark affected CFDI as status unknown until a later check succeeds.

#### Scenario: Service down
- **WHEN** the service is unreachable during a reconciliation run
- **THEN** the run completes, affected invoices show status unknown, and the check is retried later

### Requirement: Respectful request rate
The system SHALL limit its request rate to the service to a configurable maximum.

#### Scenario: Large batch
- **WHEN** thousands of CFDI need checking
- **THEN** requests are spread so the configured rate is not exceeded

### Requirement: Cancellation process is stored
The system SHALL store, with each status, whether the CFDI is cancellable and its cancellation status (for example in process, request rejected, deadline expired), and SHALL keep a CFDI as current until the service reports it cancelled.

#### Scenario: Cancellation in process
- **WHEN** the service answers success with state current and cancellation status in process
- **THEN** the system stores status current with cancellation status in process

#### Scenario: Deadline expired
- **WHEN** the service answers state current with cancellation status deadline expired
- **THEN** the system keeps the CFDI as current and shows that status

### Requirement: Check on demand
The system SHALL let an authorised user request a new status check for a CFDI as a background job, SHALL respect the rate limit, and SHALL answer from the cache, without calling the service, for a cancelled status.

#### Scenario: Unknown status rechecked
- **WHEN** a user requests a check for a CFDI with status unknown
- **THEN** a job is queued, and when the service answers the status is updated and the change recorded in the audit log

#### Scenario: Cancelled status cached
- **WHEN** a user requests a check for a CFDI already stored as cancelled
- **THEN** the system answers from the cache and does not call the service
