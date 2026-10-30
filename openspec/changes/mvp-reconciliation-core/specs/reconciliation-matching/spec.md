## Purpose

Links bank movements to CFDI according to the business rules in the BRS, and classifies every item so that clear cases are automated and doubtful ones go to a human. User story: as an accountant, I want obvious matches made for me so that I only spend time on exceptions.

## ADDED Requirements

### Requirement: Many-to-many links between movements and CFDI
The system SHALL allow one movement to be linked to several CFDI and one CFDI to be linked to several movements, recording for each link the amount applied.

#### Scenario: Partial payments on one invoice
- **WHEN** two movements of 5,800.00 are linked to an invoice of 11,600.00
- **THEN** the invoice shows 11,600.00 applied in total and no amount pending

#### Scenario: One payment covering two invoices
- **WHEN** one movement of 10,000.00 is linked to invoice A for 6,000.00 and invoice B for 4,000.00
- **THEN** the movement shows 10,000.00 fully applied and each invoice shows its own applied amount

### Requirement: Automatic confirmation only for deterministic links
The system SHALL mark a link as reconciled automatically only when it is deterministic: a payment complement references the invoice UUID, its payment amount equals the movement amount, and the movement date is within the configured tolerance of the payment date. All other candidate links SHALL be proposed as suggested.

#### Scenario: Payment complement matches movement
- **WHEN** a payment complement relates invoice A for 5,800.00 on 2026-10-10 and a credit movement of 5,800.00 exists dated 2026-10-10
- **THEN** the system links the movement to the payment CFDI and invoice A and marks the link reconciled

#### Scenario: Movement without complement
- **WHEN** a credit movement of 5,800.00 dated 2026-10-20 has no payment complement but its description and amount point to invoice A
- **THEN** the system proposes a link to invoice A marked suggested, with the reasons, and does not mark it reconciled

### Requirement: Confidence and explanation for suggestions
The system SHALL attach to each suggested link a confidence level and the list of criteria that matched (amount, date proximity, RFC, reference text), and SHALL be able to show why a link was proposed.

#### Scenario: Reasons visible
- **WHEN** a user opens a suggested link
- **THEN** the system lists the matched criteria and the confidence level

### Requirement: Payable amount is the net total
The system SHALL use the CFDI total (subtotal minus discount plus transferred taxes minus retained taxes) as the amount a payer is expected to settle, not the subtotal.

#### Scenario: Retention reduces the payment
- **WHEN** an invoice has subtotal 1,690.00, tax 270.40, retention 21.13 and a credit movement of 1,939.27 exists with a matching reference
- **THEN** the system treats the invoice as fully covered by that movement and shows no amount pending

### Requirement: Item states
The system SHALL assign every movement and every invoice exactly one state: reconciled, suggested (to review) or pending, derived from its links and the amounts applied.

#### Scenario: Invoice partially paid
- **WHEN** an invoice has 5,800.00 applied through a reconciled link out of 11,600.00 and an additional suggested link of 5,800.00
- **THEN** the invoice state is suggested and its summary shows 50% paid and 5,800.00 pending

#### Scenario: Movement with no candidate
- **WHEN** no CFDI satisfies any criterion for a movement
- **THEN** the movement state is pending

### Requirement: Credit notes reduce the payable amount
The system SHALL subtract the total of related credit notes from the amount payable of an invoice before computing paid percentage and pending amount.

#### Scenario: Credit note applied
- **WHEN** an invoice of 11,600.00 has a related credit note of 1,160.00 and 10,440.00 is paid and reconciled
- **THEN** the invoice shows no amount pending

### Requirement: Cancelled documents are not reconciled as valid
The system SHALL NOT propose or confirm links that depend on a CFDI whose SAT status is cancelled, SHALL treat a cancellation that is still in process as not cancelled, and SHALL move to review the existing links that depend on an invoice, payment complement or credit note later found cancelled.

#### Scenario: Invoice found cancelled
- **WHEN** the status check reports that an invoice with a reconciled link is cancelled
- **THEN** the link is moved to review with the reason "cancelled invoice" and the change is recorded in the audit log

#### Scenario: Payment complement found cancelled
- **WHEN** the status check reports that a payment complement used by a reconciled link is cancelled
- **THEN** the link is moved to review with the reason "cancelled payment complement" and the change is recorded in the audit log

#### Scenario: Cancellation in process
- **WHEN** the status check reports that the cancellation of an invoice is in process
- **THEN** the invoice keeps its links and shows the cancellation status, and no link is moved to review

#### Scenario: Replacement found
- **WHEN** an invoice is found cancelled and an imported CFDI declares relation code 04 to it
- **THEN** the system proposes the replacement CFDI as a suggested link for the affected movements

### Requirement: Movements without CFDI
The system SHALL allow a movement to be classified as having no CFDI with a reason (loan, partner contribution, tax payment, late invoicing, other) and SHALL exclude such movements from the pending list.

#### Scenario: Loan deposit classified
- **WHEN** a user classifies a deposit as a loan
- **THEN** the movement leaves the pending list and shows the reason

### Requirement: Re-running reconciliation is safe
The system SHALL produce the same links and states when reconciliation is run again over unchanged data, and SHALL NOT overwrite a decision made by a user.

#### Scenario: Second run
- **WHEN** reconciliation runs twice with no new data
- **THEN** the second run creates no new links and changes no states

#### Scenario: User decision preserved
- **WHEN** a user has rejected a suggested link and reconciliation runs again
- **THEN** the rejected link is not proposed again unless the user reopens it

### Requirement: Direction of matching
The system SHALL propose links only between credit movements and income CFDI issued by the company, and SHALL leave received CFDI and debit movements unmatched in this version, keeping the direction of each CFDI so that matching them can be added later.

#### Scenario: Debit movement
- **WHEN** a debit movement of 5,800.00 exists and an issued invoice of 5,800.00 exists
- **THEN** the system does not propose a link between them

### Requirement: Payer did not withhold
The system SHALL propose, marked suggested and never confirmed automatically, a link when a credit movement equals the CFDI total plus its retained taxes, and SHALL show that the payment appears not to include the retention.

#### Scenario: Gross payment
- **WHEN** an invoice has total 1,939.27 with retained tax 21.13 and a credit movement of 1,960.40 exists with a matching reference
- **THEN** the system proposes the link with the criterion "amount equals total before retentions" and the reason is visible

### Requirement: Reference match
The system SHALL treat a movement reference equal to a payment's operation number as a matching criterion that raises confidence.

#### Scenario: Tracking key equals operation number
- **WHEN** a movement reference equals the operation number of a payment in a complement and the amounts are equal
- **THEN** the criterion "reference equals operation number" appears among the reasons

### Requirement: Foreign-currency payments
The system SHALL record on each link the amount applied in the invoice currency, the movement amount in the movement currency and the exchange rate declared in the payment complement, and SHALL NOT compute exchange gains or losses.

#### Scenario: Dollar invoice paid in pesos
- **WHEN** an invoice of 1,000.00 USD has a complement payment of 17,500.00 MXN at the declared rate 17.50 and a credit movement of 17,500.00 MXN exists
- **THEN** the link applies 1,000.00 USD to the invoice and shows 17,500.00 MXN for the movement with the declared rate

### Requirement: Payment complement due date
The system SHALL compute for each payment received on a PPD invoice a complement due date equal to the fifth calendar day of the month following the payment date, SHALL show an invoice as complement overdue when that date has passed without a complement for the payment, and SHALL allow a company setting, off by default, that moves the due date to the next business day.

#### Scenario: Complement overdue
- **WHEN** a movement of 5,800.00 paid on 2026-10-10 against a PPD invoice has no complement on 2026-11-06
- **THEN** the invoice shows complement overdue with due date 2026-11-05

#### Scenario: Due date on a weekend
- **WHEN** the fifth day falls on a Saturday and the company setting is off
- **THEN** the due date stays on that Saturday

### Requirement: Applied amount never exceeds the payable amount
The system SHALL flag for review, with the reason "overpaid", any invoice whose reconciled applied amounts exceed its payable amount.

#### Scenario: Overpayment
- **WHEN** reconciled links apply 12,000.00 to an invoice whose payable amount is 11,600.00
- **THEN** the invoice is flagged for review with the reason "overpaid"

### Requirement: Global invoices are not matched by RFC
The system SHALL NOT use RFC criteria to propose links for global invoices and SHALL show them as pending with the reason "global invoice".

#### Scenario: Global invoice pending
- **WHEN** a global invoice exists and a credit movement mentions the generic RFC
- **THEN** no link is proposed and the invoice shows pending with the reason "global invoice"
