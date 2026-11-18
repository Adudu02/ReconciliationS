# User guide for clients and administrators

[Versión en español](user-guide.es.md) · [Server installation](server-installation.md)

## 1. What the product does

Imports CFDI 4.0 XML and bank movements from a supported CSV.\
Organizes information in one workspace per company.\
Suggests links between invoices and movements using explainable criteria.\
Supports suggestion review and inspection of balances, complements, and alerts.\
Retains an audit record of the corresponding actions.

**“Demo: results have no fiscal or legal validity.”**
Use only synthetic or anonymised data. The demo server uses a simulated SAT by
default; an alert does not replace fiscal verification.

## 2. Roles and permissions

Permissions come from membership in the selected company. A platform operator
creates companies; **the operator does not automatically gain access to their data**.
If the operator also has a membership, that role's permissions apply.

| Action | Operator without membership | Administrator | Accountant | Viewer |
|---|---|---|---|---|
| Read overview, invoices, movements, and review queue | No | Yes | Yes | Yes |
| Read an import report, originals, or a job by ID (API) | No | Yes | Yes | Yes |
| Upload imports | No | Yes | Yes | No |
| Run reconciliation | No | Yes | Yes | No |
| Confirm, reject, or change suggested links | No | Yes | Yes | No |
| Classify movements | No | Yes | Yes | No |
| Request SAT check (API) | No | Yes | Yes, without `anonymizedUuid` | No |
| List failed jobs / retry them (API) | No | Yes | No | No |
| Read settings (API) and members (Team/API) | No | Yes | Yes | Yes |
| Change settings (API) or add members (Team/API) | No | Yes | No | No |
| Archive company (API) | No | Yes | No | No |
| Create company (API) | Yes | Only if also an operator | Only if also an operator | Only if also an operator |
| Read audit entries and verify the chain (API) | No | Yes | Yes | Yes |
| Change own password (My account) | Yes | Yes | Yes | Yes |

“Administrator”, “Accountant”, and “Viewer” are the roles displayed in the English UI.
Archived companies show “Archived · read-only”: reads remain available, but new
imports, runs, decisions, classifications, settings/member changes, and retries
are refused. There is no company restoration action in the UI.

Permission sources: [CompanyRoleHandler.cs](../../src/Reconciliation.Core/Tenancy/CompanyRoleHandler.cs),
[SessionState.cs](../../src/Reconciliation.Web/SessionState.cs), and the
`*Endpoints.cs` files under Tenancy, Ingestion, Matching, Jobs, Sat, and Audit in
[`src/Reconciliation.Core`](../../src/Reconciliation.Core).

## 3. Sign in, language, and company

1. Open the URL provided by the server owner in your browser. On the second
   laptop, use the server address rather than `localhost`.
2. Under “Sign in”, enter “Email” and “Password”, then select “Sign in”.
   Use an account that has already been provisioned.
3. The initial language is Mexican Spanish. The “ES” and “EN” buttons switch
   between “Español” and “English”. Language is not persisted: reload uses the
   URL's initial language again; `?lang=en` starts in English and Spanish is the default.
4. Under “Choose a company”, select one of your memberships. The header shows
   the company name and your role. Use “Switch company” to select another.
5. Use “Sign out” when finished. If you have no memberships, ask an administrator
   for access. An empty company picker for an operator is expected.

## 4. Screens

### 4.1 Overview

“Overview” opens “Company overview”. It shows counts and amounts for “Invoices”
and “Movements” by “State”, plus “Invoice alerts”: “Complement overdue”,
“Cancellation in process”, “EFOS warning”, and “Global invoice”.

Invoice amounts use imported income CFDI gross totals; movement amounts add
credits and debits. Classified movements and zero-payable invoices remain in
the totals. These totals are not the outstanding receivable balance; inspect
each invoice's summary.

### 4.2 Import files

Available to accountants and administrators of an active company.

1. Open “Import files” and select “Import type”.
2. For CFDI, choose “CFDI XML files” and select CFDI 4.0 XML files. For a bank
   statement, choose “Bank CSV statement”, select one CSV in the “Supported bank
   format”, and enter “Account reference”. Arbitrary bank formats are not
   supported; use the server's reference format.
3. Select the files under “Files” and press “Upload files”. The limit is
   **20 files, 10 MiB per file, and 20 MiB total**; the API can impose smaller
   limits. Bank statement mode accepts a single file.
4. Wait for the job result and inspect “Per-file report”. Columns show “File”,
   “Status”, “Details”, and “Movements”, including imported and duplicate
   movement counts. Server-provided reasons may appear in English.

| File status | Meaning |
|---|---|
| “Processing” | The file is still being processed. |
| “Imported” | File import was processed; inspect the counts as well. |
| “Already imported” | The file was previously imported; the import is not duplicated. |
| “Rejected” | The file was not accepted; read “Details” before correcting and uploading again. |

Use “Open overview to run reconciliation” when finished. Importing alone does not
constitute a reconciliation decision. Viewers do not see this menu item; visiting
the page directly shows the read-only notice.

### 4.3 Invoices

“Invoices” lists imported income invoices. Filter “State” with “All states”,
“Pending”, “Suggested”, or “Reconciled”. Under “Complement status”, choose
“All complements”, “Missing (includes overdue)”, or “Overdue”, then press
“Apply filters”. Use “Load more invoices” when available.

The list includes UUID, issue date, direction, state, total, pending amount, and
complement due date. Open the UUID for the summary. The pending filter excludes
zero pending amounts. Complement deadlines are calculated for confirmed PPD
payments; “Missing” includes unreceived complements that are already overdue.

### 4.4 Invoice summary

“Invoice summary” shows the UUID, direction, and payment method. “Amounts” shows
“Invoice total”, “Credit notes applied”, “Net payable”, “Confirmed paid”,
“Paid percentage”, “Pending”, and “Uncovered by links”.

**“Suggested links do not count toward confirmed paid amounts.”**
“Pending” deducts confirmed amounts; “Uncovered by links” also deducts active
suggestions. The two amounts can therefore differ.

“Status and reasons” shows SAT state, cancellation, EFOS warning, and reasons
marked “requires review” or “information”. “Linked movements” shows applied amount,
state, confidence, and “Payment complement”, including due date, payment CFDI,
and payment form/date when available. A server-provided warning can remain in
English. There is no SAT-check button on this screen.

### 4.5 Movements

“Movements” shows “Credit”, “Debit”, “State”, and “Classification”. Filter with
“All states (includes classified)”, “Pending (unclassified)”, “Suggested”, or
“Reconciled”, then press “Apply filter”. Use “Load more movements” when shown.

To classify, select “Classify”, choose “Loan”, “Partner contribution”, “Tax payment”,
“Late invoicing”, or “Other”, add “Note (optional)”, and press “Save classification”.
“Clear classification” removes the classification when saved; “Cancel classification”
discards the edit. Classification is separate from confirming a link and does not
confirm it. Classified movements are excluded from “Pending (unclassified)” but
remain in totals.

### 4.6 Review queue

“Review queue” orders suggestions by confidence, followed by invoice flags.
Inspect date, amount, invoice, criteria, points, and reasons. “Invoice summary”
opens the detail. Use “Refresh queue” or “Load more entries” to inspect available
records.

- “Confirm” accepts the suggested link as reconciled.
- “Reject” rejects the suggestion; it does not record a confirmed payment.
- “Change” opens “Target invoice”, “Amount applied”, and “Decision note (optional)”.
  Select an invoice and a positive amount with at most two decimal places; press
  “Save change” or “Cancel change”. Use “Load more invoices” for another page.

Actions appear on suggestions with a link, not on every invoice flag. If someone
else has already decided a suggestion, refresh the queue; you may receive 409.
There is no reopen action or independent manual-link creation in the UI.

### 4.7 Reconciliation run

In “Overview”, use “Run reconciliation” under “Reconciliation run”. It uses the
company's imports and current settings. “Job” shows the identifier, attempt, and
“Progress”; wait for a final state before reviewing.

| Job state | Meaning |
|---|---|
| “Queued” | The job is waiting to run. |
| “Running” | The server is processing the job. |
| “Completed” | Processing finished successfully; inspect the results. |
| “Failed” | Processing did not finish successfully; inspect the message and per-file report where available. |

The UI polls progress periodically. A completed job can leave suggestions for
human review; it does not mean every invoice has been reconciled.

### 4.8 Team

“Team” shows the email and role of members in the selected company. Accountants
and viewers can read the list; in archived companies everyone sees a read-only team.

An administrator of an active company can use “Add member”: enter “Email”, choose
“Role”, and provide an “Initial password (new accounts only)” of at least 12
characters for a new account. “Show password” / “Hide password” controls visibility.
Share the initial password securely; never send it in a URL or public channel.
An existing account keeps its password, and the field can be left blank.
The password field is cleared after submission even if the operation fails.

The screen does not remove members or edit roles. If the email already appears in
the list, it reports that the user already belongs to the company and leaves the
role unchanged. An added user can sign in and select the company.
No invitation is sent and no forced password change occurs at first sign-in.

### 4.9 My account

“My account” is in the header and is available even without a selected company.
Under “Change password”, enter “Current password”, “New password”, and
“Confirm new password”. The new password must have at least 12 characters and
match the confirmation.

Submission clears the fields. On success, “Password changed. Sign in again.” is
displayed and the UI returns to sign-in. Sign in using the new password.
Refresh sessions are revoked; already issued access tokens remain valid until
expiry. If the current password cannot be verified, check it or sign in again.
This screen does not provide password recovery or reset.

## 5. States and confidence

| Label | Interpretation |
|---|---|
| “Pending” | Derived state without full confirmed coverage or a counted suggestion. A partial confirmed payment may exist; inspect the amounts. Cancelled or zero-payable invoices can also retain this state and do not necessarily appear in the pending filter. |
| “Suggested” | A proposed link awaits review; it is not counted as confirmed paid. A movement can also show this state for a link held because of cancellation. |
| “Reconciled” | Reconciled links cover the invoice's net payable or the movement amount. This does not certify fiscal validity or guarantee receipt of a complement. |
| “Received” | The corresponding payment complement is present for the link. |
| “Missing” | The complement is absent; inspect whether there is a due date. |
| “Overdue” | The complement is absent and its calculated deadline has passed; this does not mean the invoice itself is overdue. |
| “High”, “Medium”, “Low” | Link confidence levels, not paid percentages or guarantees. Inspect criteria and reasons even for high confidence. |

## 6. Worked example for an accountant

1. Sign in and choose an active company as “Accountant” or “Administrator”.
2. Import synthetic XML files, then the supported bank CSV. Inspect each report.
3. In “Overview”, press “Run reconciliation” and wait for “Completed”.
4. Open “Review queue”, inspect a suggestion and its invoice, and press “Confirm”
   only when the link represents the payment. Leave other suggestions unconfirmed
   until they have been reviewed.
5. Inspect “Invoice summary” and compare confirmed amounts with pending amounts.

The [documented example](../../README.md#review-api-and-summaries-8a) describes an
invoice of **11,600.00**, with **5,800.00 confirmed** and **5,800.00 suggested**:
it shows **50% paid**, **5,800.00 pending**, and zero uncovered by links.
These are the example's figures, not guaranteed results for arbitrary files.
Missing/overdue complements are evaluated separately from payment coverage.

## 7. Administration through the API

> **Operations still without a page:** creating companies, changing settings, and
> archiving require the API. “Team” supports reading and adding members; “My account”
> supports changing your own password. There are no job administration, audit, or
> SAT-management pages. Customers see their membership companies and their role's actions.

These recipes use curl. Replace the placeholders; the token must belong to the
specified role. Use the URL provided by the server owner. Creating a company
requires a **platform operator**, not just a company administrator:

```bash
BASE=http://localhost:8080
OPERATOR_TOKEN='<operator accessToken>'
curl -fsS -X POST "$BASE/companies" \
  -H "Authorization: Bearer $OPERATOR_TOKEN" -H 'Content-Type: application/json' \
  -d '{"rfc":"AAA010101AAA","name":"Synthetic demo company","adminEmail":"admin@example.invalid","adminPassword":"<administrator-password>"}'
```

Obtain the token with `POST /auth/tokens` and `{email,password}`, as shown in
[installation](server-installation.md#first-start). A new administrator requires a
password of at least 12 characters; an existing email retains its password.

### 7.1 Members

Use “Team” to add by email. The equivalent API operation requires an administrator
token for an active company:

```bash
ADMIN_TOKEN='<administrator accessToken>'
COMPANY_ID='<company UUID>'
curl -fsS -X POST "$BASE/companies/$COMPANY_ID/members" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  -H 'Content-Type: application/json' \
  -d '{"email":"accountant@example.invalid","role":"accountant","initialPassword":"<initial-password>"}'
```

A new account requires an initial password of at least 12 characters; an existing
account retains its password even if `initialPassword` is supplied. Allowed `role`
values are `administrator`, `accountant`, and `viewer`. No invitation is sent.
Supply exactly one of `email` or `userId`.

Adding members by email reveals to administrators whether an account exists: a new account requires an initial password, while an existing account does not.
As with `POST /companies`, an administrator can attach an existing account to the company without its owner's consent; no invitation or acceptance is required.

The previous `{userId,role}` form remains available for existing users:

```bash
USER_ID='<existing user UUID>'
curl -fsS -X POST "$BASE/companies/$COMPANY_ID/members" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  -H 'Content-Type: application/json' \
  -d "{\"userId\":\"$USER_ID\",\"role\":\"accountant\"}"
```

Both forms preserve the API's previous semantics: repeating the same membership
makes no change and adds no audit entry; sending another role updates the membership
and audits the change, while preserving at least one administrator. “Team” only
provides adding, not role editing. `GET /companies/{companyId}/members` returns
`{userId,email,role}` only to members of that company. New accounts record
`account.created`; memberships record `member.added` or `member.changed`, without
passwords or hashes.

### 7.2 Settings

With the variables above, change only the needed fields:

```bash
curl -fsS -X PATCH "$BASE/companies/$COMPANY_ID/settings" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  -H 'Content-Type: application/json' \
  -d '{"dateToleranceDays":3,"amountTolerance":0.50,"complementNextBizDay":true}'
```

These are example values: date tolerance is 0–366 days; absolute amount tolerance
is 0–1.00 with at most two decimal places; `complementNextBizDay` controls moving a
calculated deadline to the next business day when it falls on a weekend.
Read current values with `GET /companies/{companyId}/settings` and the same headers.
Customers do not see a settings form; subsequent reconciliation runs use the
current settings.

### 7.3 Archive

This call makes the company read-only; it does not delete it:

```bash
curl -fsS -X POST "$BASE/companies/$COMPANY_ID/archive" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID"
```

Customers retain read access and see “Archived · read-only” after reloading the
company. There is no documented unarchive action. Routes, fields, and restrictions
are defined in [CompanyEndpoints.cs](../../src/Reconciliation.Core/Tenancy/CompanyEndpoints.cs)
and [CompanyAdminEndpoints.cs](../../src/Reconciliation.Core/Tenancy/CompanyAdminEndpoints.cs).

## 8. Session and reload

The access token stays **in memory**; it is not stored in `localStorage`,
`sessionStorage`, or the URL. With the default
`Session__BrowserRefreshCookie=false`, the UI does not retain the JSON refresh
token. **Reload requires signing in again.** Expired access followed by failed
renewal also requires another sign-in.

Silent renewal after reload requires enabled refresh-cookie transport and a valid
cookie; company selection is requested again afterward. The server must allow the
browser's exact origin. The cookie is HttpOnly, SameSite=Strict, and scoped to
`/auth`; browser transport marks it Secure on HTTPS. HTTPS is required outside the
demo. Legacy global `Session__RefreshCookie` takes precedence and is not the
default configuration.

A 401 triggers one coordinated refresh attempt and one request retry; failed
renewal or another 401 returns to sign-in. Tabs do not share the in-memory token.
See [ADR-004](../adr/ADR-004-session-renewal.md),
[ADR-010](../adr/ADR-010-browser-ui-and-api-contract.md), and
[ApiClient.cs](../../src/Reconciliation.Web/ApiClient.cs).

## 9. FAQ and common messages

| Message or situation | Action |
|---|---|
| “No company memberships are available. Ask a company administrator for access.” | Request membership; an operator without memberships also sees this message. |
| “Read-only workspace. An accountant or administrator can decide on suggestions in an active company.” | Check the role and whether the company is archived; a viewer cannot decide. |
| “This workspace is read-only. Accountants and administrators can import into an active company.” | Ask the administrator for the appropriate access; this is not an upload failure. |
| 401 / “Your session has ended. Sign in again.” | Sign in again and select a company. |
| 403 | Check role, selected company, and origin configuration if using refresh cookies. |
| 409 on a decision | Another decision or state change may have occurred; refresh before retrying. |
| 413 / “The upload is too large. Reduce its size and try again.” | Reduce file count/size within the limits; the server can impose smaller limits. |
| 429 / “Too many requests. Wait a moment and try again.” | Wait before repeating the request. |
| “The service had a problem completing the request. Try again later.” | This is the generic server-error headline; try later and contact the owner if it persists. |
| English detail below a Spanish headline | This is original API text that is not translated. A 5xx error uses the generic headline without showing its raw body. It does not mean your language changed. |

Text sources: [Catalog.En.cs](../../src/Reconciliation.Web/Localization/Catalog.En.cs),
[Catalog.Es.cs](../../src/Reconciliation.Web/Localization/Catalog.Es.cs), and
[ErrorText.razor](../../src/Reconciliation.Web/Shared/ErrorText.razor).
