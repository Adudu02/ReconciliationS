# ADR-010 Browser UI and HTTP API contract

Status: accepted. Implements task 8.5, design D11, ADR-004 browser-transport amendment and ADR-009.

## Static shell and browser application

Reconciliation.Web is a standalone net10.0 Blazor WebAssembly application. The API image publishes it into
wwwroot. The static shell (index.html, CSS and _framework assets) is anonymous and contains no tenant data,
credentials or configuration secrets. UseStaticFiles serves these files. Only GET requests accepting text/html
and outside reserved API paths can receive the SPA fallback. An unknown API resource retains its 401/403/404,
including when a browser accepts HTML. The shell is a delivery surface, not an authorization boundary.

Once running in the browser, the application uses only the documented HTTP API. It has no database connection,
no Reconciliation.Core or Migrator reference, no in-process server-service access and no UI-only endpoint.
API authorization, live company roles, archived-company write refusal, CompanyScope, forced RLS and audit
transactions remain authoritative. The API process also serves the files; there is no new server container.

Login, company selection/switching, overview, review decisions, invoice list/summary, imports/progress and
movements/classification are included. Accountant and administrator actions are hidden for viewers and archived
companies. The API still checks each write. A company switch replaces the routed view. English is the only UI
language. Native labelled forms, semantic tables, keyboard focus/scroll regions and prefers-color-scheme support
responsive light/dark use. Login, overview and footer state: results have no fiscal or legal validity.

## Sessions and shell headers

Access tokens remain in memory. They never enter localStorage, sessionStorage, a URL or an application log.
Session.BrowserRefreshCookie defaults false. When enabled, X-Refresh-Transport: cookie selects the existing
ADR-004 refresh transport on login, refresh and logout. Each browser request requires an allowed Origin.
Cookies are HttpOnly, SameSite=Strict, Path=/auth and Secure for HTTPS requests. Plain HTTP localhost is only a
local-development convenience; production must use HTTPS. The UI does not read the refresh credential.

Legacy Session.RefreshCookie retains global precedence and behavior. Without the browser opt-in header,
JSON clients retain accessToken, refreshToken and expiresIn. The header alone is ignored when the browser
feature is disabled. All paths share the original rotation, grace and replay-revocation implementation.
Logout calls the API and clears browser memory. Refresh transport failures, timeouts or malformed responses
clear an expired session instead of leaving a stale company selected. One 401 causes one single-flight refresh
and one retry;
failed renewal or another 401 returns to login. Reload silently renews, then asks for company selection again.
No persistent company selection is added. Separate tabs do not coordinate in-memory access tokens; the
existing refresh grace behavior still applies. Existing access tokens retain ADR-004's bounded expiry latency.

The Content-Security-Policy is default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self';
img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'.
There is no inline script or style in index.html. Shell responses set X-Content-Type-Options: nosniff and
Referrer-Policy: no-referrer. index.html uses no-cache; only fingerprinted _framework files are immutable.

## Contract and company directory

A thin hand-written ApiClient uses System.Net.Http.Json and source-generated JSON metadata. A generated client
would add tooling for a small fixed surface without improving the real-response tests. Reconciliation.Contracts
contains DTO records only, shared by the browser and built-in OpenAPI schema generation; the API's existing
responses are still exercised independently. Required constructor fields make missing response fields fail
rather than silently becoming zero. Review criteria explicitly describe code, points and detail. Multipart
files use the framework's binary-file schema. All 17 operations observed through the worked-example client
are checked against /openapi.json, including auth, companies, banks, imports/reports, jobs/runs, review,
decisions, invoice list/summary, overview and movement reads/classification. bUnit checks presentation and roles.

The browser accepts application/problem+json for RFC 7807 errors, with reason/state extensions where applicable.
Requests with no Accept or */* keep legacy error bodies byte-for-byte. OpenAPI lists both media types and the
negotiation rule. Tests cover both paths for 400/401/403/404/409. Validation messages explain decision bounds;
raw exceptions are never rendered. Unexpected protocol failures and timeouts have generic readable messages.

GET /companies was missing. M0013 adds user_companies(uuid), a STABLE SECURITY DEFINER function with a pinned
search_path=pg_catalog,public,pg_temp. A dedicated cfdi_directory_owner is NOLOGIN and BYPASSRLS, owns only this
function, and has column-level SELECT on company(id,rfc,name,archived_at) and membership(user_id,company_id,role).
It has no other table grants; EXECUTE is revoked from PUBLIC and granted to cfdi_app. cfdi_app remains
NOBYPASSRLS. The API sets app.user_id LOCAL from the authenticated JWT subject in the same transaction;
the function refuses a missing/mismatched context and takes no caller-supplied user id from HTTP. It returns
only that user's memberships, including archived companies and roles. Operators have no special directory
access. No RLS policy is widened. This consistency check does not protect against compromise of server DB
credentials, which are already trusted to establish transaction context. Up/Down/Up and role/grant checks are
tested; Down drops only the function/owner and preserves user, company, decision and audit data.

## Polling, publishing and evidence

Pages stay thin; view-models handle API operations, selection, paging, validation and feedback. Job polling uses
TimeProvider and backoff of 1, 2, 4, 5, 5 seconds, stopping at succeeded/failed and cancelling on disposal.
No server push endpoint is added. SSE is a possible future improvement if profiling justifies it.

The solution and existing CI build/test include Web and its bUnit tests. Docker publishes the trimmed standalone
app without AOT or workload installation. The measured image contains 7,319,437 uncompressed _framework bytes;
12,544,744 bytes including prebuilt .br/.gz copies (Brotli copies total 2,347,811 bytes). These are file sizes,
not a claimed network-transfer benchmark; UseStaticFiles is the hosting mechanism.

The in-process test drives login, company pick, XML plus two movements, run/poll, queue, one confirmation,
50% paid / 5,800.00 pending, additive overview totals and viewer reads through the same client/models as pages.
No complement precedes the first run. Separate real-response contract tests retain the payment-CFDI worked
example. tools/ui_smoke.mjs repeats the interactive flow in installed headless Chrome against a disposable
Compose stack, including uploads, refresh-cookie reload, CSP/cache checks and synthetic screenshots.
Only Node standard libraries and Chrome CDP are used. Profiles, Chrome processes, Compose containers and
volumes are removed afterward. No browser dependency or CI browser job is introduced.

## Alternatives and limits

- React: rejected for this C# project; another language/build stack is unnecessary for the minimal application.
- Blazor Server: rejected because server circuits and in-process services/DB access would obscure the required
  pure HTTP-client boundary and add a stateful hosting model.
- MAUI desktop wrapper: rejected because the deliverable is a browser app, not an executable or native installer.
- plain JavaScript: feasible, but duplicates DTO/session/view-model logic in another language; Blazor WebAssembly
  keeps the chosen C# stack and bUnit seams.
- Generated client: deferred; handwritten typed methods plus machine-readable schemas and real-response tests
  cover the current surface without a generator dependency.

No password-change page, administration/audit/SAT-management UI, standalone manual N:M creation or reopen UI
is added. Their existing API availability and ADR-009 gaps are unchanged. Browser uploads cap at 20 files,
10 MiB each and 20 MiB total; API configuration may impose smaller limits. No list snapshot is promised between
keyset pages. There is no offline mode, localization, cross-tab refresh coordinator or production deployment.
