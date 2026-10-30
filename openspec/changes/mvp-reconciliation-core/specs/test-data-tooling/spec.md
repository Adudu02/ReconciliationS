## Purpose

Provides safe, realistic data for development, tests and the demo: real CFDI anonymised before they reach the repository, plus generated bank statements and edge cases. User story: as the project owner, I want realistic data without exposing anyone's information so that I can show the system publicly.

## ADDED Requirements

### Requirement: Anonymise real CFDI
The system SHALL provide a tool that turns real CFDI XML files into anonymised files that stay structurally valid, by replacing UUIDs, RFCs, names, addresses, certificates, stamps and free-text fields, while preserving the relations between documents (an anonymised payment complement still references its anonymised invoice) and the monetary amounts.

#### Scenario: Relations preserved
- **WHEN** an income CFDI and the payment complement that references it are anonymised together
- **THEN** the anonymised complement references the anonymised invoice's new UUID and the amounts are unchanged

#### Scenario: Seals and certificates removed
- **WHEN** a CFDI is anonymised
- **THEN** the digital seals, certificate data and stamp are replaced, and the output is documented as structurally valid but not cryptographically valid

#### Scenario: Consistent replacement
- **WHEN** the same RFC appears in several files of one run
- **THEN** it is replaced by the same pseudonymous RFC everywhere

### Requirement: Mapping kept outside the repository
The tool SHALL write the mapping between real and anonymised identifiers to a location outside the repository, and the repository SHALL ignore the folder holding raw files.

#### Scenario: Mapping location
- **WHEN** the tool finishes
- **THEN** the mapping exists only in the configured local location and no raw file is written inside the tracked tree

### Requirement: Leak scan
The system SHALL provide a scan that fails when any file to be committed contains an identifier from the local mapping (UUID, RFC or name), and SHALL run it before every commit and in CI.

#### Scenario: Leaked RFC
- **WHEN** a file to be committed contains a real RFC present in the mapping
- **THEN** the scan fails and names the file, without printing the sensitive value

### Requirement: Generate matching bank statements
The system SHALL generate bank statements in the reference format whose movements correspond to a set of CFDI, including exact payments, partial payments, a payment to several invoices, movements without complement, movements without any CFDI, duplicates and near-duplicates.

#### Scenario: Statement for the worked example
- **WHEN** the generator runs on the worked example's invoice and payment complement
- **THEN** it produces the two credit movements of 5,800.00 dated 2026-10-10 and 2026-10-20 with their descriptions

### Requirement: Edge-case catalogue
The system SHALL generate CFDI and statements for these cases: cancelled invoice, cancellation in process, credit note, replacement CFDI (relation 04), invoice with discount, invoice in a foreign currency paid in pesos, PPD invoice with several complements, complement with inconsistent balance, overpaid invoice, global invoice, payment received without withholding, movement amount differing by cents, and malformed or hostile files (external entity, oversized, wrong version).

#### Scenario: Hostile file present
- **WHEN** the catalogue is generated
- **THEN** it includes a file with an external entity declaration and a file larger than the upload limit

### Requirement: Synthetic sample from a real shape
The repository SHALL include at least one synthetic CFDI sample modelled on a real invoice shape (PPD, transferred tax and retention) containing only invented identifiers, in `samples/`.

#### Scenario: Sample contains no real data
- **WHEN** the leak scan runs over `samples/`
- **THEN** it finds no identifier from the local mapping

### Requirement: Deterministic generation
The system SHALL produce identical output for the same seed and inputs.

#### Scenario: Same seed twice
- **WHEN** the generator runs twice with the same seed
- **THEN** both outputs are byte-identical

### Requirement: Demo seed
The system SHALL provide a command that loads a ready-made demo dataset into a demo company, covering every state (reconciled, suggested, pending) and a cancelled invoice.

#### Scenario: Seed demo
- **WHEN** the demo seed runs on an empty installation
- **THEN** the demo company shows reconciled, suggested and pending items and one cancelled invoice

### Requirement: Anonymisation keeps the company direction
The system SHALL replace the company's RFC consistently, so that a CFDI that was issued by the company stays issued and one that was received stays received after anonymisation.

#### Scenario: Direction kept
- **WHEN** an invoice issued by the company and an invoice received by the company are anonymised together
- **THEN** both keep their direction relative to the anonymised company RFC
