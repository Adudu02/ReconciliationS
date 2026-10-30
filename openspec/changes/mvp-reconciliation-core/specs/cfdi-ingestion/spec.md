## Purpose

Lets an accountant load CFDI 4.0 XML files (income invoices, payment complements, credit notes) into a company's workspace so they can be reconciled. User story: as an accountant, I want to upload invoices for a company so that I can start reconciling.

## ADDED Requirements

### Requirement: Accept CFDI 4.0 XML files
The system SHALL accept CFDI 4.0 XML files of type income (I), payment (P) and credit note / egress (E) uploaded to a company workspace, and SHALL store for each: UUID, type, issuer RFC, receiver RFC, issue date, currency, total, payment method (PUE/PPD) and, where present, the related invoice UUIDs with amounts.

#### Scenario: Income invoice accepted
- **WHEN** a user uploads a valid CFDI 4.0 income XML to a company
- **THEN** the system stores it with its UUID, RFCs, date, currency and total and reports it as imported

#### Scenario: Payment complement accepted
- **WHEN** a user uploads a valid CFDI 4.0 payment complement 2.0 that relates invoice UUID A for 5,800.00
- **THEN** the system stores the payment CFDI together with the relation to UUID A and the related amount

#### Scenario: Unsupported version or type rejected
- **WHEN** a user uploads a CFDI of a version other than 4.0, or a file that is not a CFDI
- **THEN** the system rejects that file with a reason and imports nothing from it

### Requirement: Taxes, discounts and retentions are stored
The system SHALL store, for each income CFDI, the subtotal, the discount, the total transferred taxes, the total retained taxes, any local taxes transferred or retained, and the total, and SHALL reject a CFDI whose total differs from subtotal minus discount plus transferred taxes minus retained taxes (federal and local in both cases), or whose discount exceeds the subtotal. The system SHALL check only this arithmetic on the declared header amounts and SHALL NOT recompute taxes from rates.

#### Scenario: Invoice with retention
- **WHEN** a user uploads an income CFDI with subtotal 1,690.00, discount 0.00, transferred tax 270.40, retained tax 21.13 and total 1,939.27
- **THEN** the system stores all amounts and imports the file

#### Scenario: Invoice with discount
- **WHEN** a user uploads an income CFDI with subtotal 1,000.00, discount 100.00, transferred tax 144.00 and total 1,044.00
- **THEN** the system stores all amounts and imports the file

#### Scenario: Inconsistent total
- **WHEN** a CFDI declares a total that does not equal subtotal minus discount plus transferred taxes minus retained taxes
- **THEN** the system rejects the file with the reason

#### Scenario: Discount above subtotal
- **WHEN** a CFDI declares a discount greater than its subtotal
- **THEN** the system rejects the file with the reason

### Requirement: Idempotent by UUID
The system SHALL treat a CFDI as already imported when a CFDI with the same UUID exists in the same company, and SHALL NOT create a duplicate or alter the stored data.

#### Scenario: Same file uploaded twice
- **WHEN** the same CFDI file is uploaded twice to one company
- **THEN** the second upload creates no new record and the import report lists it as already imported

#### Scenario: Same UUID in two companies
- **WHEN** a CFDI with the same UUID is uploaded to two different companies the user can access
- **THEN** each company holds its own isolated copy

### Requirement: Safe handling of untrusted XML
The system SHALL parse uploaded XML without resolving external entities, external DTDs or network references, and SHALL enforce a maximum file size and a maximum number of files per upload.

#### Scenario: External entity attempt
- **WHEN** a user uploads an XML file that declares an external entity pointing to a local file or a network address
- **THEN** the system rejects the file, does not read the referenced resource and records the rejection

#### Scenario: Oversized upload
- **WHEN** an upload exceeds the configured size or file-count limit
- **THEN** the system rejects the upload with a clear reason and imports nothing

### Requirement: Per-file import report
The system SHALL return, for each upload, a report with one result per file: imported, already imported, or rejected with reason; one invalid file SHALL NOT prevent valid files in the same upload from being imported.

#### Scenario: Mixed upload
- **WHEN** a user uploads three files of which one is invalid and one was imported before
- **THEN** the report shows one imported, one already imported and one rejected, and only the valid new file is stored

### Requirement: Credit notes linked to invoices
The system SHALL record the relation between a credit note and the invoice it refers to when the CFDI declares one.

#### Scenario: Credit note relation stored
- **WHEN** a credit note CFDI declares a related invoice UUID
- **THEN** the system stores the relation so that the matching capability can reduce the invoice's payable amount

### Requirement: CFDI must involve the company
The system SHALL accept a CFDI into a company only when the issuer or the receiver RFC equals the company's RFC, and SHALL record its direction: issued when the company is the issuer, received when it is the receiver.

#### Scenario: Issued invoice
- **WHEN** a user uploads to a company with RFC AAA010101AAA a CFDI whose issuer RFC is AAA010101AAA
- **THEN** the system stores it with direction issued

#### Scenario: Unrelated CFDI
- **WHEN** a user uploads a CFDI in which neither RFC is the company's RFC
- **THEN** the system rejects the file with the reason "does not involve this company"

### Requirement: Payment complement detail
The system SHALL accept only payment complements of version 2.0 and SHALL store each payment (date, form of payment, currency, exchange rate, amount, operation number when present) and, for each payment, each related invoice with its installment number, previous balance, amount paid, remaining balance and currency. The system SHALL check that the remaining balance equals the previous balance minus the amount paid.

#### Scenario: Complement with consistent balances
- **WHEN** a user uploads a payment complement that relates invoice A with previous balance 11,600.00, amount paid 5,800.00 and remaining balance 5,800.00
- **THEN** the system stores the payment and the related invoice line

#### Scenario: Inconsistent balance
- **WHEN** a payment complement declares previous balance 11,600.00, amount paid 5,800.00 and remaining balance 4,000.00
- **THEN** the system imports the complement, flags it for review with the reason "inconsistent balance" and does not use it for automatic confirmation

#### Scenario: Old complement version
- **WHEN** a user uploads a payment complement of a version other than 2.0
- **THEN** the system rejects the file with the reason

### Requirement: Relations between CFDI keep the SAT code
The system SHALL store each relation between CFDI with its SAT relation code (including credit note 01, return 03, substitution 04 and advance application 07), and SHALL store relations of unsupported codes without applying them to payable amounts.

#### Scenario: Substitution recorded
- **WHEN** an income CFDI declares relation code 04 to a previous UUID
- **THEN** the system stores the relation as a substitution of that CFDI

### Requirement: Global invoices and generic RFCs
The system SHALL mark a CFDI as global when it carries the global-information node or its receiver RFC is the generic national RFC (XAXX010101000), and SHALL accept the generic foreign RFC (XEXX010101000) as a receiver.

#### Scenario: Global invoice
- **WHEN** a user uploads an income CFDI with receiver RFC XAXX010101000 and global information
- **THEN** the system stores it marked as global

### Requirement: Dates as declared
The system SHALL store the issue date and time exactly as declared (local time, no time zone) and the stamp date and time separately.

#### Scenario: Issue date near midnight
- **WHEN** a CFDI is issued at 2026-09-30T23:50:00 and stamped at 2026-10-01T00:05:00
- **THEN** the system stores both values unchanged and the issue date remains 2026-09-30

### Requirement: Original files retained
The system SHALL keep every accepted original XML file unchanged together with its hash, SHALL NOT delete it through any API, and SHALL keep it for a configurable retention period of at least five years.

#### Scenario: Original available
- **WHEN** a user requests the original file of an imported CFDI
- **THEN** the system returns the stored XML byte for byte
