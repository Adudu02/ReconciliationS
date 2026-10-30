## Purpose

Lets several companies, and accounting firms serving many of them, use one installation with strict data isolation enforced below the application layer, and with roles that limit what each user can do. User story: as a firm, I want to manage several clients with separate access so that their data stays isolated.

## ADDED Requirements

### Requirement: Company workspaces
The system SHALL organise all data (CFDI, movements, links, jobs, audit entries) by company, and every request SHALL operate in exactly one company chosen among those the user can access.

#### Scenario: Switch company
- **WHEN** a user with access to two companies switches from one to the other
- **THEN** all lists and summaries show only the selected company's data

### Requirement: Isolation enforced in the database
The system SHALL enforce company isolation in the data store itself, so that a defect in application code that omits a company filter cannot return another company's rows.

#### Scenario: Missing filter in a query
- **WHEN** a query runs in the context of company A without any company condition
- **THEN** it returns only rows of company A

#### Scenario: Context absent
- **WHEN** a query runs without any company context
- **THEN** it returns no rows and the attempt is logged

### Requirement: Roles
The system SHALL support at least these roles per company: administrator (manage users and settings), accountant (import, review, decide) and viewer (read-only, including the audit log).

#### Scenario: Viewer cannot decide
- **WHEN** a viewer attempts to confirm a suggested link
- **THEN** the request is refused and nothing changes

#### Scenario: Accountant cannot manage users
- **WHEN** an accountant attempts to add a user to the company
- **THEN** the request is refused

### Requirement: Access for several companies
The system SHALL allow one user to hold different roles in different companies.

#### Scenario: Different roles
- **WHEN** a user is accountant in company A and viewer in company B
- **THEN** the user can decide on links in A and only read in B

### Requirement: Authenticated access only
The system SHALL require authentication for every endpoint except health checks and the API description, and SHALL store credentials only as salted, slow hashes.

#### Scenario: Anonymous request
- **WHEN** a request without valid credentials calls a data endpoint
- **THEN** the system refuses it without revealing whether the company exists

### Requirement: Isolation verified by tests
The system SHALL include automated tests that attempt cross-company reads and writes through every data endpoint and fail if any succeeds.

#### Scenario: Cross-company attempt in the test suite
- **WHEN** the isolation tests request company B's invoice using a user of company A
- **THEN** the response is not-found or forbidden in every case

### Requirement: Company identity
The system SHALL give each company an RFC supplied at creation, validated for format, that cannot be changed afterwards.

#### Scenario: Invalid RFC
- **WHEN** an operator creates a company with an RFC that has an invalid format
- **THEN** the system rejects the request with the reason

### Requirement: Company creation by a platform operator
The system SHALL let a platform operator, a role outside any company, create a company with its first administrator, and SHALL record the action in the audit log.

#### Scenario: Company created
- **WHEN** an operator creates a company with RFC AAA010101AAA and an administrator email
- **THEN** the company exists, the administrator can sign in with the role administrator, and the creation is recorded

#### Scenario: Not an operator
- **WHEN** a company administrator tries to create another company
- **THEN** the request is refused

### Requirement: Company settings
The system SHALL let a company administrator change the company settings (date tolerance in days, amount tolerance, complement due date moved to the next business day) and SHALL record each change in the audit log with the previous and new values.

#### Scenario: Tolerance changed
- **WHEN** an administrator changes the amount tolerance from 0.01 to 0.02
- **THEN** the new value applies to the next reconciliation run and the change is recorded

#### Scenario: Accountant cannot change settings
- **WHEN** an accountant tries to change a company setting
- **THEN** the request is refused

### Requirement: Records are retained
The system SHALL NOT delete CFDI, statements, original files, links, decisions or audit entries through any API, and SHALL archive a company instead of deleting it.

#### Scenario: Delete attempt
- **WHEN** a user attempts to delete an imported CFDI
- **THEN** the operation is not available and nothing changes
