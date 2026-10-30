## Purpose

Gives users a review queue for doubtful matches, the ability to decide on them, and clear paid / pending summaries, through a documented API and a minimal UI. User stories: as an accountant, I want a queue of doubtful matches with their reasons so that I can decide quickly; as a finance lead, I want to see what is paid, partially paid and pending so that I can follow up.

## ADDED Requirements

### Requirement: Review queue
The system SHALL list, per company, all suggested links and flagged items awaiting a decision, ordered by confidence level and then date, with the reasons for each.

#### Scenario: Queue shows suggestions
- **WHEN** a user opens the review queue of a company with two suggested links
- **THEN** both are listed with their criteria and confidence, and reconciled items are not listed

### Requirement: Decisions on suggestions
The system SHALL let an authorised user confirm, reject, or change (different CFDI or different applied amount) a suggested link, and SHALL record the decision with user and timestamp.

#### Scenario: Confirm suggestion
- **WHEN** a user confirms a suggested link
- **THEN** the link becomes reconciled and the item states are recomputed

#### Scenario: Reject suggestion
- **WHEN** a user rejects a suggested link
- **THEN** the link is removed from the queue, is not proposed again, and the movement returns to pending if it has no other links

#### Scenario: Change suggestion
- **WHEN** a user re-links a suggested movement to a different invoice
- **THEN** the old suggestion is closed, a new reconciled link is created by the user, and both actions are recorded

### Requirement: Per-invoice summary
The system SHALL show for each invoice its total, credit notes applied, amount paid, percentage paid, amount pending, state, and the linked movements and payment CFDI.

#### Scenario: Summary of the worked example
- **WHEN** invoice A of 11,600.00 has a reconciled link of 5,800.00 and a suggested link of 5,800.00
- **THEN** the summary shows 50% paid, 5,800.00 pending, both movements, and the payment CFDI for the reconciled one

### Requirement: Company overview
The system SHALL show per company the counts and amounts of reconciled, suggested and pending invoices and movements.

#### Scenario: Overview totals
- **WHEN** a user opens the overview of a company
- **THEN** counts and amounts per state are displayed and add up to the totals of imported invoices and movements

### Requirement: Documented, versioned API
The system SHALL expose all user-facing capabilities through a versioned HTTP API described by a machine-readable OpenAPI document, and the minimal UI SHALL use only that API.

#### Scenario: API document available
- **WHEN** a developer requests the API description
- **THEN** the system returns an OpenAPI document that covers every endpoint of the current version

### Requirement: Clear demo disclaimer
The system SHALL state in the UI and API documentation that results have no fiscal or legal validity.

#### Scenario: Disclaimer visible
- **WHEN** a user opens the overview or the API description
- **THEN** the disclaimer is shown

### Requirement: Complement status in the summary
The system SHALL show on each invoice summary its direction, its payment method, the complement status of each payment (received, missing, overdue) and the due date of missing complements.

#### Scenario: Missing complement
- **WHEN** a PPD invoice has a reconciled payment with no complement yet
- **THEN** the summary shows the payment with complement missing and its due date

### Requirement: Filter invoices by complement status
The system SHALL let users list invoices filtered by complement status (missing, overdue), so that overdue complements can be followed up.

#### Scenario: Overdue list
- **WHEN** a user filters invoices by complement overdue
- **THEN** only invoices with at least one overdue complement are listed with their due dates

### Requirement: Overview alerts
The system SHALL show in the company overview the number of invoices with complement overdue, cancellation in process, EFOS warning, or marked global.

#### Scenario: Alert counts
- **WHEN** a company has two invoices with complement overdue and one with an EFOS warning
- **THEN** the overview shows 2 and 1 for those alerts

### Requirement: Cash payment warning
The system SHALL show a non-blocking warning on a payment whose form of payment is cash and whose amount exceeds 2,000.00, stating that results have no fiscal validity and that deductibility must be checked.

#### Scenario: Cash payment above the limit
- **WHEN** a payment complement declares form of payment 01 for 3,000.00
- **THEN** the payment is shown with a warning and its state is unchanged

### Requirement: One decision resource
The system SHALL expose confirm, reject and change as decisions recorded on a link, each decision creating one audit entry.

#### Scenario: Reject recorded
- **WHEN** a user rejects a suggested link
- **THEN** one decision of type reject is recorded with the user and one audit entry is added
