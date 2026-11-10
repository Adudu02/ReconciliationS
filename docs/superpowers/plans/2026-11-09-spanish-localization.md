# Spanish Localization (es-MX) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The browser UI is Spanish (Mexico) by default with an English switch, so the main customers read every label, state, error and notice in their own language.

**Architecture:** A small dependency-free `Localizer` service backed by two C# dictionaries (`es`, `en`), injected into the Razor pages and view models. Machine values from the API (`suggested`, `succeeded`, `invoice-reference`) never change: they stay in CSS classes, `data-*` attributes and logic, and a `Labels` mapper turns them into display text, passing unknown values through unchanged. Client-originated errors become `UiError(key)` objects rendered by `PageStatus`. The API, its English text and its OpenAPI stay untouched in this plan.

**Tech Stack:** Blazor WebAssembly (net10.0), bUnit + xUnit v3 (Microsoft.Testing.Platform), headless Chrome smoke (`tools/ui_smoke.mjs`). No resx, no satellite assemblies, no ICU data.

**Spec:** none written. The decisions below are the spec; Task 7 records them as `docs/adr/ADR-011-ui-localization.md`.

## Decisions (change one and say so before Task 1)

| # | Decision | Why |
|---|---|---|
| D1 | Default language `es` (es-MX). English is the switch. | CFDI/SAT users are Mexican. |
| D2 | Custom `Localizer` with C# dictionaries, not `IStringLocalizer`/resx. | About 150 strings. Trim-safe, no satellite assemblies, a key-parity test is trivial. |
| D3 | Money stays `InvariantCulture` (`11,600.00`) and dates stay ISO (`2026-10-10`). | Already identical to es-MX, so no ICU download and the smoke maths keep working. |
| D4 | Language lives in memory; initial value from `?lang=es\|en`, else `es`. No localStorage, sessionStorage or cookie. | ADR-010 and the smoke test forbid browser storage. Cost: an English user who reloads returns to Spanish. |
| D5 | Spanish register: impersonal infinitive on buttons (`Iniciar sesión`), *usted* in sentences. | Common in Mexican B2B accounting software. |
| D6 | Server-sent English text (problem `detail`, `Explanation`, `Detail`) is not translated by this plan. Known codes are mapped on the client; any other server sentence is shown verbatim under a Spanish headline, marked `lang="en"`. | Server translation changes the API contract; it is a separate plan (Follow-up F1). |

## Global Constraints

- Every user-visible string comes from `Localizer`. No Razor literal survives except the machine values and `data-*` hooks listed in Task 2.
- Keys are lowercase dotted (`nav.overview`, `error.session-ended`). Placeholders are `{0}`, `{1}`. A key exists in both `es` and `en` with the same placeholder set (enforced by test).
- Machine values are never translated: state, confidence, role, direction, job state, complement status, classification, criterion code, reason code. Class names such as `badge suggested` keep the raw value.
- An unknown machine value displays raw. Nothing is hidden or blanked.
- `<html lang>` always matches the active language. English server text inside a Spanish page carries `lang="en"`.
- Required disclaimer, `results have no fiscal or legal validity` in English and `los resultados no tienen validez fiscal ni legal` in Spanish, appears on login, overview and footer in both languages.
- No `localStorage`/`sessionStorage`. CSP unchanged: any new script is a same-origin file, no inline script.
- Warnings are errors (`dotnet build`). Run tests with `dotnet test --solution Reconciliation.slnx`.

## Review Focus

1. **Unknown server value** (a new state, reason or criterion code): displays the raw value; never blank, never throws. Pinned in Task 2.
2. **Server English sentence in a Spanish page** (a 409 `already decided`, a validation message): shown in full, under a Spanish headline, with `lang="en"`. Pinned in Task 4.
3. **Missing key or placeholder mismatch** between `es` and `en`: fails the build's test run. Pinned in Task 1.
4. **Switching language mid-flow** (Change form open, job polling, a company selected): state and selected company survive; only text changes. Pinned in Task 3.
5. **Spanish is 20 to 30 percent longer**: topbar, tab strip, badges and table headers still fit at 390 px with no horizontal page scroll. Pinned in Task 7 (smoke at 390 px in Spanish).

## Glossary (draft: an accountant who works with SAT must approve before release)

| English | Spanish | | English | Spanish |
|---|---|---|---|---|
| Reconciliation / reconciled | Conciliación / conciliado | | Credit / Debit (bank) | Abono / Cargo |
| Review queue | Bandeja de revisión | | Credit note | Nota de crédito |
| Suggested / Pending | Sugerido / Pendiente | | Payment complement | Complemento de pago |
| Confirm / Reject / Change | Confirmar / Rechazar / Cambiar | | Complement overdue | Complemento de pago vencido |
| Company / Switch company | Empresa / Cambiar de empresa | | Cancellation in process | Cancelación en proceso |
| Sign in / Sign out | Iniciar sesión / Cerrar sesión | | Global invoice | Factura global |
| Import files | Importar archivos | | Payment method / form | Método de pago / Forma de pago |
| Movements | Movimientos | | EFOS warning | Alerta EFOS |
| Invoices (income CFDI) | Facturas (CFDI de ingreso) | | Net payable / Confirmed paid | Neto por cobrar / Cobrado confirmado |

RFC, CFDI, UUID, SAT, EFOS, PUE and PPD stay as acronyms.

---

## File Structure

- Create `src/Reconciliation.Web/Localization/Localizer.cs`: the service and `UiError`.
- Create `src/Reconciliation.Web/Localization/Catalog.Es.cs` and `Catalog.En.cs`: the dictionaries, one file per language.
- Create `src/Reconciliation.Web/Localization/Labels.cs`: machine value to display text.
- Create `src/Reconciliation.Web/Localization/DocumentLanguage.cs` and `wwwroot/lang.js`: sets `<html lang>`.
- Create `src/Reconciliation.Web/Shared/LanguageSwitch.razor`.
- Modify: `Program.cs`, `index.html` (`lang="es"`), `App.razor`, every file in `Pages/` and `Shared/`, `ViewModels/*.cs`, `ApiClient.cs`, `Display.cs`, `wwwroot/app.css`.
- Create `tests/Reconciliation.Tests/LocalizationTests.cs`; modify the component tests and `WebDocumentationTests.cs` named in the tasks.
- Create `docs/adr/ADR-011-ui-localization.md`; modify `ADR-010`, `README.md`, `docs/README.md`, both build logs, `tools/ui_smoke.mjs`.

---

### Task 1: Localizer, catalogs and the parity gate

**Files:**
- Create: `src/Reconciliation.Web/Localization/Localizer.cs`, `Catalog.Es.cs`, `Catalog.En.cs`
- Modify: `src/Reconciliation.Web/Program.cs`
- Test: `tests/Reconciliation.Tests/LocalizationTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class Localizer { public static IReadOnlyList<string> Supported { get; } /* ["es","en"] */ public string Language { get; } /* default "es" */ public event Action? Changed; public void Set(string language); public string this[string key] { get; } public string Format(string key, params object[] args); }`
  - `public static class Catalog { public static IReadOnlyDictionary<string,string> Es { get; } public static IReadOnlyDictionary<string,string> En { get; } }`
  - `public sealed record UiError(string Key, string? Raw = null, int? Status = null);`
  - DI: `builder.Services.AddScoped<Localizer>();`

- [ ] **Step 1: Write the failing tests** in `LocalizationTests.cs`:
  - `Defaults_to_spanish`: `new Localizer().Language == "es"`.
  - `Set_ignores_unknown_languages`: `Set("fr")` leaves `"es"`; `Set("en")` raises `Changed` once and returns `"en"`; `Set("en")` again raises nothing.
  - `Indexer_falls_back_to_english_then_key`: a key only in `En` returns the English text while in `es`; a key in neither returns the key itself.
  - `Format_substitutes_placeholders`: `Format("import.files-selected", 3)` is `"3 archivo(s) seleccionado(s)"` in `es`.
  - `Catalogs_have_identical_keys_and_placeholders`: `Catalog.Es.Keys` equals `Catalog.En.Keys` as sets, and for every key the sorted `{n}` tokens match. The failure message lists the offending keys.
  - `No_catalog_value_is_empty`.
- [ ] **Step 2: Run** `dotnet test --solution Reconciliation.slnx --filter 'FullyQualifiedName~LocalizationTests'`. Expected: build FAIL (types missing).
- [ ] **Step 3: Implement** `Localizer`, `Catalog` and `UiError` per Interfaces. Seed both catalogs with the keys for `nav.*`, `topbar.*`, `login.*`, `notice.disclaimer`, `import.files-selected` (Spanish wording from the Glossary; D5 register). Register in `Program.cs`.
- [ ] **Step 4: Run** the same filter. Expected: all `LocalizationTests` PASS.
- [ ] **Step 5: Commit** `feat: add Localizer with es/en catalogs and key-parity test`.

### Task 2: Machine values to display text

**Files:**
- Create: `src/Reconciliation.Web/Localization/Labels.cs`
- Modify: `Catalog.Es.cs`, `Catalog.En.cs`, `Program.cs`
- Test: `LocalizationTests.cs`

**Interfaces:**
- Consumes: `Localizer`.
- Produces: `public sealed class Labels(Localizer t)` with `string State(string v)`, `Confidence(string? v)`, `Role(string v)`, `Direction(string v)`, `JobState(string v)`, `ComplementStatus(string v)`, `Classification(string? v)`, `Criterion(ReviewCriterion c)`, `Reason(string code)`, `Explanation(ReviewEntry e)`. Registered `AddScoped<Labels>()`. Catalog keys are `state.<value>`, `role.<value>`, `criterion.<code>`, `reason.<code>` and so on.

- [ ] **Step 1: Write the failing tests:**
  - `State_translates_known_values`: in `es`, `State("suggested") == "Sugerido"`, `State("reconciled") == "Conciliado"`, `State("pending") == "Pendiente"`.
  - `Unknown_values_pass_through_raw` (Review Focus 1): `State("quarantined") == "quarantined"`, `Reason("brand_new_code") == "brand_new_code"`, `Role("auditor") == "auditor"`.
  - `Criterion_uses_code_and_ignores_server_detail`: `Criterion(new("invoice-reference", 30, "any english"))` returns the Spanish label for the code. An unknown code returns the server `Detail` unchanged.
  - `Explanation_rebuilds_from_criteria_and_keeps_counts`: criteria `invoice-reference` and `amount-equals-remaining` with `Explanation "…; 3 other invoices matched equally"` gives the localized criteria joined by `"; "` plus `"3 facturas más coincidieron por igual"`. Text that matches no known pattern returns the server `Explanation` unchanged.
- [ ] **Step 2: Run** the filter. Expected: FAIL.
- [ ] **Step 3: Implement `Labels`.** Cover every value in: states (`pending suggested reconciled received overdue missing rejected cancelled`), confidence (`high medium low`), roles (`administrator accountant viewer`), directions, job states (`queued running succeeded failed`), classifications (`loan partner_contribution tax_payment late_invoicing other`), the eight criterion codes in `src/Reconciliation.Core/Matching/HeuristicMatching.cs` and `MatchingEngine.cs`, and every reason code the API emits (`grep -rn "RequiresReview\|ReviewReason" src/Reconciliation.Core`). `Explanation` recognises exactly two server suffixes, `"{n} other invoices matched equally"` and `"{n}+ others may have matched equally"`, by anchored regex.
- [ ] **Step 4: Run** the filter. Expected: PASS. Re-run the Task 1 parity test.
- [ ] **Step 5: Commit** `feat: add Labels for machine-value display text`.

### Task 3: Shell, language switch and `<html lang>`

**Files:**
- Create: `Shared/LanguageSwitch.razor`, `Localization/DocumentLanguage.cs`, `wwwroot/lang.js`
- Modify: `wwwroot/index.html` (`lang="es"`, `<script src="lang.js">`), `App.razor`, `Pages/Login.razor`, `Pages/CompanyPicker.razor`, `Program.cs`, `app.css`
- Test: `tests/Reconciliation.Tests/ClientSessionTests.cs` (line 119), `WorkspaceComponentTests.cs` (company picker), new `LanguageSwitchTests` in `LocalizationTests.cs`

**Interfaces:**
- Consumes: `Localizer`, `Labels`.
- Produces: `public interface IDocumentLanguage { ValueTask SetAsync(string language); }` (JS-backed in the app; bUnit tests register a fake). `LanguageSwitch` renders two buttons, `ES` and `EN`, with `aria-pressed` and `lang` attributes. `App` reads `?lang=` once on first render.

- [ ] **Step 1: Write the failing tests:**
  - `Login_renders_in_spanish_by_default`: the markup contains `Iniciar sesión` and `los resultados no tienen validez fiscal ni legal`.
  - `Switch_to_english_rerenders_and_sets_document_language`: clicking `EN` shows `Sign in`, and the fake `IDocumentLanguage` receives `"en"`.
  - `Query_parameter_selects_initial_language`: `?lang=en` starts in English; `?lang=zz` starts in `es`.
  - `Switching_language_keeps_the_selected_company` (Review Focus 4): after `SelectCompany`, switching leaves `Session.Company` unchanged.
  - Update the two existing English assertions (`ClientSessionTests` 119, `WorkspaceComponentTests` 39) to set `en` through a shared `TestLanguage.English(context)` helper, so they keep proving the English text.
- [ ] **Step 2: Run** `--filter 'FullyQualifiedName~LanguageSwitch|FullyQualifiedName~ClientSession|FullyQualifiedName~WorkspaceComponent'`. Expected: FAIL.
- [ ] **Step 3: Implement.** Replace literals in `App.razor`, `Login.razor`, `CompanyPicker.razor` with catalog keys. `App` subscribes to `Localizer.Changed` like it does `Session.Changed`. `lang.js` exposes `setLanguage(value)` and sets `document.documentElement.lang`. Add `.lang-switch` styles, two segmented buttons that wrap cleanly in the 390 px grid topbar.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: Spanish default shell, language switch and html lang`.

### Task 4: Errors become keys

**Files:**
- Modify: `ViewModels/PageViewModel.cs`, `ApiClient.cs`, `JobProgressViewModel.cs`, `ImportViewModel.cs`, `ReviewQueueViewModel.cs`, `MovementsViewModel.cs`, `InvoiceViewModel.cs`, `WorkspaceViewModel.cs`, `Shared/PageStatus.razor`, `App.razor`
- Test: `OperationsComponentTests.cs`, `ReviewComponentTests.cs`, `LocalizationTests.cs`

**Interfaces:**
- Consumes: `UiError`, `Localizer`.
- Produces: `PageViewModel.Error` becomes `UiError?`. `ApiProblemException` gains `string? Key`. `PageStatus` renders `Localizer[error.Key]` for client keys. For a server message it renders a localized headline chosen by `Status` (`error.status-409` and so on) followed by `<small lang="en">` with the raw text.

- [ ] **Step 1: Write the failing tests:**
  - `Server_problem_detail_is_kept_verbatim_under_a_spanish_headline` (Review Focus 2): a 409 with `detail: "Link already decided"` renders a Spanish headline, and `[role=alert]` text still contains `already decided`, with `small[lang=en]` present.
  - `Client_errors_follow_the_active_language`: the service-unavailable error renders in `es`, then in `en` after `Set("en")`, without a reload.
  - Existing `Assert.Null(model.Error)` tests stay green unchanged.
- [ ] **Step 2: Run** the filter. Expected: FAIL.
- [ ] **Step 3: Implement.** Move every client-originated sentence (the `catch` blocks in `PageViewModel`, `ApiClient` lines for 401 and "Select a company first", the validation messages in `ReviewQueueViewModel`, `ImportViewModel` and the shell errors in `App.razor`) to `error.*` keys. `ReviewComponentTests` line 46 (`already decided`) must pass unmodified.
- [ ] **Step 4: Run** the full web test set: `--filter 'FullyQualifiedName~Component|FullyQualifiedName~Web|FullyQualifiedName~Localization'`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: localized client errors; server text kept verbatim`.

### Task 5: Overview, Review queue, Run reconciliation

**Files:**
- Modify: `Pages/Overview.razor`, `Pages/Review.razor`, `Shared/RunReconciliation.razor`, `Shared/JobProgress.razor`, catalogs
- Test: `WorkspaceComponentTests.cs`, `ReviewComponentTests.cs`

**Interfaces:**
- Consumes: `Localizer`, `Labels`.
- Produces: nothing new. The disclaimer and totals note render from keys `notice.disclaimer` and `overview.totals-note`, not from `OverviewResponse.Disclaimer` or `TotalsNote`. The API fields stay and keep their tests.

- [ ] **Step 1: Write the failing tests:**
  - `Overview_in_spanish_shows_localized_states_and_keeps_css_classes`: the badge text is `Sugerido` and the element still has classes `badge suggested`, since the smoke test counts `.badge.suggested`.
  - `Overview_alert_hooks_survive`: `[data-alert=complement]` and `[data-alert=efos]` still hold `"2"` and `"1"`.
  - `Review_entry_shows_localized_criteria_and_confidence`: criterion labels come from `Labels.Criterion`, the confidence badge reads `Alta`, `Media` or `Baja` and keeps class `high`, `medium` or `low`.
  - `Switching_language_mid_change_form_keeps_form_state` (Review Focus 4): open Change, type a note, switch to `en`; the form is still open with the note intact.
  - `Job_progress_label_localizes_state_but_not_polling`: switching language while `running` leaves the poll loop and `Current.Id` unchanged.
- [ ] **Step 2: Run** the filter. Expected: FAIL.
- [ ] **Step 3: Implement.** Replace literals with keys. Use `Labels` for every state, confidence, criterion, reason and job state. The progress text is `Progreso {0} / {1}`.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: Spanish overview, review queue and run progress`.

### Task 6: Invoices, Invoice detail, Movements, Imports, Company picker

**Files:**
- Modify: `Pages/Invoices.razor`, `Pages/InvoiceDetail.razor`, `Pages/Movements.razor`, `Pages/Imports.razor`, catalogs
- Test: `InvoiceComponentTests.cs`, `OperationsComponentTests.cs`

**Interfaces:**
- Consumes: `Localizer`, `Labels`.
- Produces: select options keep their machine `value` attributes (`pending`, `suggested`, `missing`, `overdue`, `loan` and so on) and localize only the visible text.

- [ ] **Step 1: Write the failing tests:**
  - `Filter_options_keep_machine_values_in_spanish`: option `value` attributes are unchanged and the visible text is Spanish.
  - `Classification_options_post_machine_values`: choosing `Préstamo` still sends `"loan"` (reuses the PATCH-contract test).
  - `Cash_payment_warning_from_server_is_shown_verbatim`: `SummaryLink.CashPaymentWarning` renders unchanged inside `[lang=en]` in `es`. The server text is out of scope here (D6).
  - `Money_and_dates_are_not_localized` (D3): `11,600.00` and `2026-10-10` render identically in `es` and `en`.
  - `Unknown_state_in_a_table_row_renders_raw` (Review Focus 1).
- [ ] **Step 2: Run** the filter. Expected: FAIL.
- [ ] **Step 3: Implement.** Replace literals with keys; use `Labels` for `State`, `Direction`, `ComplementStatus`, `Classification`, `Role`. `Archived · read-only` becomes `Archivada · solo lectura`. The pluralization-safe form is `{0} archivo(s) seleccionado(s)`.
- [ ] **Step 4: Run** the filter, then the full suite: `dotnet test --solution Reconciliation.slnx`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: Spanish invoices, movements and imports`.

### Task 7: Smoke test, documentation and release gate

**Files:**
- Modify: `tools/ui_smoke.mjs`, `docs/adr/ADR-010-browser-ui-and-api-contract.md`, `README.md` (line 499 and the Web UI section), `docs/README.md`, `docs/build-log/BUILD-LOG.md`, `docs/build-log/BUILD-LOG-ES.md`, `tests/Reconciliation.Tests/WebDocumentationTests.cs`
- Create: `docs/adr/ADR-011-ui-localization.md`

**Interfaces:**
- Consumes: everything above.
- Produces: ADR-011 (Status accepted; "Supersedes the English-only sentence and the no-localization non-goal of ADR-010"). It states D1 to D6 and F1.

- [ ] **Step 1: Write the failing documentation test** in `WebDocumentationTests.cs`: ADR-011 exists and contains `es-MX`, `Localizer`, `lang`, `localStorage` (as forbidden) and `Accept-Language` (as follow-up). The README Web UI section contains `Spanish` and no longer contains `The UI is English only`. ADR-010 contains `ADR-011`. The existing `"English"` token assertions stay satisfied by the new wording.
- [ ] **Step 2: Run** `--filter 'FullyQualifiedName~WebDocumentation'`. Expected: FAIL.
- [ ] **Step 3: Write the docs.** Create ADR-011, add a one-line pointer in ADR-010, update the README (`The UI is Spanish (Mexico) by default with an English switch`), docs index, and one build-log entry in each language following the PR 13 entry (the build-log tests enforce this).
- [ ] **Step 4: Update `tools/ui_smoke.mjs`.** Run the existing flow in Spanish: replace English text matches (`"Company overview"`, `"succeeded"`, `"imported"`) with `[data-*]` hooks or the Spanish strings. Add a 390 px Spanish layout assertion (`scrollWidth <= innerWidth`, Review Focus 5). Add one `?lang=en` pass over login and overview, and an assertion that `document.documentElement.lang` is `es`, then `en`. Keep `localStorage.length === 0`.
- [ ] **Step 5: Verify:** `dotnet test --solution Reconciliation.slnx` PASS; run the smoke on the disposable stack (see the script header for the environment variables). Expected output ends with `"succeeded":true`.
- [ ] **Step 6: Human gate.** An accountant who uses SAT terminology reads every Spanish string in `Catalog.Es.cs` and the Glossary. Merge only after they sign off; record who and when in the PR description.
- [ ] **Step 7: Commit** `docs: ADR-011 UI localization, smoke in Spanish, build logs`.

---

## Follow-up plans (not in this plan)

- **F1, server-side Spanish:** `Accept-Language` negotiation in the API for problem `detail` (about 22 messages), `CashPaymentWarning` (SQL literal in `SummaryEndpoints.cs`), matching `Detail` and `Explanation`, and the overview `Disclaimer` and `TotalsNote`. It changes the API contract, the OpenAPI document and about ten tests that assert English.
- **F2, language persistence:** a same-origin `ui_lang` cookie if users complain about the reload reset (needs an ADR-011 amendment, because the smoke test forbids browser storage).
- **F3, es-ES formatting** (`11.600,00`): only if a non-Mexican customer appears.

## Self-Review

- **Coverage:** login, shell, picker (T3); errors (T4); overview, review, run (T5); invoices, detail, movements, imports (T6); docs, ADR, smoke, accountant review (T7). Machine values (T2) and parity (T1) are cross-cutting.
- **Step scan:** every step names its tests, files and signatures. Spanish wording is decided in the Glossary and the tests, not left to the implementer.
- **Type consistency:** `Localizer`, `Labels`, `UiError`, `IDocumentLanguage` and the `LocalizationTests.cs` / `TestLanguage` helper names are used identically across tasks. `PageViewModel.Error` changes type in T4 only.
- **Review Focus:** each of the five lines has a named test in Tasks 1, 2, 3, 4 and 7.
- **Proportion:** the plan is a decision record plus tests; there are no function bodies.
- **Risk to flag:** no .NET SDK was available while writing this, so the line numbers and test names above come from reading the source; the executor should confirm them on first build.
