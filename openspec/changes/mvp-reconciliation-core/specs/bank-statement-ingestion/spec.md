## Purpose

Lets an accountant load a bank's CSV statement into a company's workspace so its movements can be reconciled. User story: as an accountant, I want to upload a bank statement so that its movements become candidates for matching.

## ADDED Requirements

### Requirement: Accept CSV statements in the reference format
The system SHALL accept CSV statements in a documented reference format with the columns `fecha, concepto, abono, cargo, saldo` (header row required, ISO dates, decimal point, no thousands separator) and SHALL store each line as a movement with date, description, credit amount (abono), debit amount (cargo) and balance. The reference format SHALL be identified by the bank identifier `reference`, and the format of a real bank is a later, additional parser.

#### Scenario: Valid statement imported
- **WHEN** a user uploads a valid statement containing two credit lines
- **THEN** the system stores two movements of type credit with their dates, descriptions, amounts and balances

#### Scenario: Malformed line
- **WHEN** a statement contains a line with an unparseable date or amount
- **THEN** the system rejects the whole statement with the line number and reason, and imports no movements from it

### Requirement: Extensible parser per bank
The system SHALL select the parser by an explicit bank identifier supplied with the upload, and adding support for another bank SHALL NOT require changes to matching, review or audit behaviour.

#### Scenario: Unknown bank identifier
- **WHEN** a user uploads a statement with a bank identifier that has no parser
- **THEN** the system rejects the upload and lists the supported banks (initially only `reference`)

### Requirement: Idempotent by statement line
The system SHALL treat a movement as already imported when a movement with the same bank account, date, description, amount and running balance exists in the same company, and SHALL NOT create a duplicate.

#### Scenario: Overlapping statements
- **WHEN** a user uploads two statements whose date ranges overlap
- **THEN** the overlapping movements are stored once and the report states how many were new and how many were already imported

#### Scenario: Two legitimate identical movements
- **WHEN** a statement contains two lines with the same date, description and amount but different running balances
- **THEN** both are stored as separate movements

### Requirement: Amounts handled exactly
The system SHALL represent monetary amounts without floating-point rounding errors and SHALL preserve two decimal places of the source.

#### Scenario: Cent-exact sums
- **WHEN** movements of 0.10 and 0.20 are summed
- **THEN** the result is exactly 0.30

### Requirement: Reference extracted from the description
The system SHALL expose for each movement an optional reference (tracking key or reference number) extracted by the bank parser from the description, using the token `RASTREO:<key>` or `REF:<number>` in the reference format.

#### Scenario: Tracking key present
- **WHEN** a statement line has the description "SPEI REC 0034 RASTREO:ABC123 PAGO FACT 0001"
- **THEN** the movement stores the reference ABC123

#### Scenario: No reference
- **WHEN** a statement line has no reference token
- **THEN** the movement stores no reference and the import is not rejected

### Requirement: List of supported banks
The system SHALL let a user list the supported bank identifiers.

#### Scenario: Supported banks listed
- **WHEN** a user asks for the supported banks
- **THEN** the system returns the identifiers that have a parser (initially only `reference`)

### Requirement: Original statements retained
The system SHALL keep every accepted statement file unchanged with its hash, SHALL NOT delete it through any API, and SHALL keep it for a configurable retention period of at least five years.

#### Scenario: Original statement available
- **WHEN** a user requests the original file of an imported statement
- **THEN** the system returns the stored file byte for byte
