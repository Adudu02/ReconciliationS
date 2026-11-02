# ADR-004 Session renewal

Status: accepted. Implements `openspec/changes/add-session-renewal/design.md`.

## Context and decision

Preserve the Bearer contract for API and non-browser clients. Issue short access tokens (default 15 minutes,
configurable from 5 to 30) and opaque rotating refresh tokens made from 256 CSPRNG bits. Store SHA-256 digests only.
Roles remain outside tokens and sessions; company membership is read on every company request.

A session has a default absolute cap of 30 days and an inactivity limit of 8 hours. Each renewal expires at the
lesser of the inactivity deadline and the original cap. All application session times use injected TimeProvider.
Rotation locks auth_session with SELECT FOR UPDATE in one transaction. Reuse inside the default 10-second grace
window returns 409 without changing or revoking anything; later reuse revokes the session and records an event.
Clients must coordinate renewal with single-flight behavior and retry a conflict using the winner's stored token.
Logout ends the current session; changing the password ends all the user's sessions. Existing access tokens remain
valid until expiry: revocation latency is the configured access lifetime. Existing tokens without sid retain access
to existing Bearer routes until they expire; a new login is needed to obtain renewal credentials.

## Alternatives

| Approach | Decision and reason |
|---|---|
| Rotating refresh tokens | Chosen: explicit renewal, per-session revocation and reuse detection; a database leak exposes no usable refresh token |
| Sliding access-token renewal | Rejected: extending a self-contained credential does not provide a revocable token family or bound a stolen credential without extra state |
| Server-side cookie sessions | Rejected as the primary authentication mechanism: replaces the Bearer contract, complicates scripts/non-browser clients, adds CSRF handling and a session lookup per request |
| OIDC provider | Production recommendation: Keycloak or Cognito supplies MFA, SSO and recovery without implementing those features here; outside this demo's scope |

Optional cookie transport carries only the refresh credential; access remains Bearer. Cookies are HttpOnly,
Secure, SameSite=Strict and scoped to /auth. Cookie-based renewal requires an Origin on the configured allow-list;
the JSON body transport remains the default.

## Storage and permissions

M0006 creates per-user auth_session, refresh_token and append-only auth_event outside company RLS. cfdi_app has
SELECT/INSERT and only the named mutable columns granted UPDATE; it has no DELETE. Only password_hash gains UPDATE
on app_user. Events contain user/session, event, remote IP and time, never credentials. Group 5 will integrate
these events with hash-chained auditing. Expired-row purging waits for the jobs group.

## Consequences and rollback

The grace window tolerates legitimate races but does not detect a replay within that window. Per-IP renewal/logout
limits may affect users behind a shared NAT; configure and monitor them. Body-delivered refresh tokens need secure
client storage; cookie transport reduces JavaScript exposure. No per-request session database lookup is added.
Deploy API and migrator together; rollback restores the previous API and reverts M0006 with Down. Versions and
existing table/column names stay unchanged. No new NuGet dependency is needed.
