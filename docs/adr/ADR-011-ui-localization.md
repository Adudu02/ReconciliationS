# ADR-011 Spanish default for the browser UI

Status: accepted. Supersedes the English-only sentence and the no-localization non-goal in [ADR-010](ADR-010-browser-ui-and-api-contract.md).

## Decision

The Reconciliation.Web interface defaults to Spanish (Mexico), `es-MX` (`es` in the application), and offers English as its alternate language. This decision covers the existing Blazor WebAssembly UI and does not change API response language or contracts.

- **D1 — Default:** use Spanish (Mexico); English is the user-selectable alternative.
- **D2 — Catalogs:** use the dependency-free `Localizer` backed by paired C# dictionaries. Keep matching keys and placeholders in both catalogs.
- **D3 — Stable values:** format money with `InvariantCulture` and dates as ISO `yyyy-MM-dd` in both languages.
- **D4 — Initial language:** read `?lang=es|en` at startup; default to Spanish. Keep the choice in memory only. `localStorage`, `sessionStorage` and cookies are not used for language state.
- **D5 — Spanish copy:** use impersonal infinitives for actions and *usted* in sentences. Keep RFC, CFDI, UUID, SAT, EFOS, PUE and PPD as acronyms.
- **D6 — Server text:** do not translate server-authored English details, explanations or warnings in this change. Show them verbatim with `lang="en"` inside a Spanish page. Translate known machine codes with `Labels`; show unknown codes raw.

The document's `<html lang>` follows the selected UI language. User-visible UI strings come from the catalogs; API values, option values, identifiers, CSS classes and `data-*` hooks remain machine values. The required disclaimer remains “los resultados no tienen validez fiscal ni legal” in Spanish and “results have no fiscal or legal validity” in English on login, overview and footer. The API, OpenAPI contract, CSP and browser storage policy do not change.

## Follow-ups

- **F1 — Server-side Spanish:** consider `Accept-Language` negotiation for problem details, `CashPaymentWarning`, matching details/explanations and overview disclaimer/totals note. This changes the API contract, OpenAPI and tests that currently assert English.
- **F2 — Language persistence:** consider a same-origin `ui_lang` cookie only if users need language persistence across reloads. Amend this ADR first because current smoke checks require browser storage to remain empty.
- **F3 — es-ES formatting:** consider localized number/date formats only if customers outside Mexico need them; es-MX currently keeps invariant money and ISO dates for predictable reconciliation values.

## Release gate

An accountant familiar with SAT terminology must review all Spanish catalog copy and the glossary before release. Record the reviewer and date in the release or pull request record.
