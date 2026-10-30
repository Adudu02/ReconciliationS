## Purpose

Keeps a complete, append-only and tamper-evident record of what happened to a company's data and who decided what, so that every reconciliation result can be explained afterwards. User story: as an auditor, I want to see who matched what and when so that I can trust the result.

## ADDED Requirements

### Requirement: Record relevant actions
The system SHALL record an audit entry for every import (accepted, duplicate, rejected), every automatic link, every user decision (confirm, reject, change, classify), every status change caused by a SAT check, and every role or access change, each with actor (user or system), company, timestamp, action and affected items.

#### Scenario: User confirms a link
- **WHEN** a user confirms a suggested link
- **THEN** an entry is added with the user, the time, the action, the movement and the CFDI involved

#### Scenario: Automatic link
- **WHEN** the system creates a reconciled link automatically
- **THEN** an entry is added with the system as actor and the criteria that justified it

### Requirement: Append-only
The system SHALL NOT allow audit entries to be modified or deleted through any API or application role.

#### Scenario: Attempt to modify
- **WHEN** any application role attempts to update or delete an audit entry
- **THEN** the operation is refused and the attempt itself is recorded

### Requirement: Tamper evidence
The system SHALL chain entries so that altering or removing an entry can be detected, and SHALL provide a verification operation that reports the first inconsistent entry, if any.

#### Scenario: Intact log
- **WHEN** an auditor runs the verification on an untouched log
- **THEN** the system reports the log as consistent

#### Scenario: Altered entry
- **WHEN** an entry is altered directly in storage and the verification runs
- **THEN** the system reports the position of the first inconsistent entry

### Requirement: Read access for auditors
The system SHALL let users with the viewer or auditor role read and filter the audit log of their companies by date, actor, action and item, without allowing any change.

#### Scenario: Filter by item
- **WHEN** an auditor filters the log by a CFDI UUID
- **THEN** only entries involving that CFDI are returned, in chronological order

### Requirement: Company isolation of the log
The system SHALL show a user only the audit entries of companies the user can access.

#### Scenario: Other company's entries
- **WHEN** a user queries the log of a company they cannot access
- **THEN** the system returns no entries and reveals nothing about that company
