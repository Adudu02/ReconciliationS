## Context

Login (`POST /auth/tokens`) issues an HMAC-SHA256 JWT valid for one hour that carries only `sub` (see
`Authentication.cs`, `JwtTokenPolicy`). Company roles are read from `membership` on every request
(`CompanyRoleHandler`), so roles never live in the token. Company tables use forced row-level security keyed on
`app.company_id`; `cfdi_app` has no `DELETE` and only column-level `UPDATE`. `LoginProtection` already provides
fixed-window rate limiting. `audit_entry.company_id` is mandatory, so user-level events cannot be stored there.
Group 5 will replace the minimal audit with a hash-chained log. See proposal.md for motivation.

## Goals / Non-Goals

**Goals:**
- Renewal that keeps the Bearer contract unchanged for API clients.
- A stolen refresh token is detected on reuse and its session is revoked.
- Revocation and expiry rules that are testable with a controllable clock.
- No new runtime dependency.

**Non-Goals:**
- External identity provider, MFA, SSO, password reset by email or "remember me".
- Per-request session checks on the access token (see decision 5).
- A job that purges expired rows (waits for group 4 jobs; rows are small).
- A UI; only the API and cookie option the UI will use.

## Decisions

**1. Opaque rotating refresh token, stored as SHA-256.**
256 random bits from `RandomNumberGenerator`, base64url. A fast hash is enough because the value is high-entropy, so
nothing needs a slow password hash. A database leak yields no usable token.
*Alternatives:* refresh token as a JWT (rejected: cannot be revoked or rotated without a lookup anyway, and leaks claims);
slow hash such as PBKDF2 (rejected: no benefit for 256-bit secrets, costs a verification per renewal).

**2. Two tables plus an event table (`M0006`).**
- `auth_session(id, user_id, created_at, last_used_at, absolute_expires_at, revoked_at, revoked_reason)` is the token
  family.
- `refresh_token(id, session_id, token_hash UNIQUE, issued_at, expires_at, rotated_at, replaced_by)`; the newest token of
  a session is the one with `rotated_at IS NULL`.
- `auth_event(id, user_id, session_id, event, remote_ip, created_at)` append-only.
Indexes on `refresh_token(token_hash)`, `refresh_token(session_id)`, `auth_session(user_id)`.
*Alternative:* one table with a family id (rejected: mixes session state with per-token state and complicates revoke).

**3. Rotation is atomic through a row lock.**
`POST /auth/refresh` runs in one transaction: look up the token by hash, then `SELECT ... FROM auth_session WHERE id = @id
FOR UPDATE` before deciding. Concurrent requests with the same token serialise on that lock; the first rotates, later ones
see `rotated_at` set.
*Alternative:* optimistic update `WHERE rotated_at IS NULL` and count rows (kept as a second guard, but the lock gives
clearer outcomes for the grace window).

**4. Reuse detection with a grace window.**
If the presented token has `rotated_at` set: when `now - rotated_at <= ReuseGraceSeconds` (default 10) return **409** and
change nothing; otherwise revoke the session (`revoked_reason = 'reuse_detected'`), write the event and return 401. The
grace window exists because two tabs of a legitimate client can race; the loser did not receive the new token, so the
client must renew from a single place (single-flight) and retry on 409 using the token the winner stored.
*Trade-off:* an attacker who replays within the grace window is not detected by that request; the legitimate client's
next renewal still rotates normally and a later replay is caught.

**5. Access tokens stay stateless; revocation latency is the access lifetime.**
The access token carries `sub` and a new `sid` (session id) but is not checked against the database on each request.
Logout and password change therefore end renewal immediately and the already-issued access token dies within at most
15 minutes. Company authorisation is still live because `membership` is read every request.
*Alternative:* check `auth_session.revoked_at` on every request (rejected for now: one more query on the hot path that PR-4
just optimised). Upgrade path: add the check inside the existing per-request role query; `sid` already makes it possible.

**6. Expiry rules.**
`expires_at` of a new token = `min(now + IdleMinutes, absolute_expires_at)`; `absolute_expires_at = created_at + AbsoluteDays`.
Defaults: access 15 min (allowed 5 to 30), idle 480 min, absolute 30 days, grace 10 s. All validated at start-up like
`IngestionOptions`. All time goes through an injected `TimeProvider` so tests advance the clock instead of sleeping.

**7. Session data lives outside company row-level security.**
Sessions are per user. The tables get no company policy; protection is by grants (`SELECT`/`INSERT`, and `UPDATE` only on
`last_used_at`, `revoked_at`, `revoked_reason`, `rotated_at`, `replaced_by`) and by never exposing a route that lists
sessions of another user. Events are insert-only (no `UPDATE`, no `DELETE`).
*Alternative:* a per-user RLS variable such as `app.user_id` (rejected for this change: new context plumbing for tables
that no company-scoped query touches; revisit if a "list my sessions" route is added).

**8. Cookie transport is an option, not the default.**
`Session__RefreshCookie=true` moves the refresh token to a cookie (`HttpOnly; Secure; SameSite=Strict; Path=/auth`) and
removes it from the JSON body. `POST /auth/refresh` with a cookie additionally requires an `Origin` header present in
`Session__AllowedOrigins`; the Bearer header contract of every other route is untouched. When off, the token is in the
body and clients store it themselves.
*Alternative:* cookie-only (rejected: breaks scripts, tests and non-browser clients).

**9. Password change endpoint is included.**
No password-change route exists, yet "password change ends sessions" needs one. `POST /auth/password` takes the current and
new password, enforces `PasswordPolicy.MinimumLength`, updates `app_user.password_hash`, revokes all of the user's sessions
in the same transaction and writes `password_changed`. `cfdi_app` gets `UPDATE(password_hash)` on `app_user`.
Group 5 already lists tighter grants for `app_user`; this adds one named column.

**10. Rate limiting reuses the login limiter.**
`LoginProtection` is generalised to a named fixed-window IP limiter applied to `/auth/refresh` and `/auth/logout`
(`RefreshRateLimit__IpPermitLimit`, default 60 per `LoginRateLimit__WindowSeconds`). Per-IP only: the refresh token is
random, so an attacker cannot be throttled per victim.

**11. ADR-004.**
Records this design; why server-side cookie sessions were not chosen (breaks the Bearer contract and non-browser clients,
adds CSRF and a second lookup per request); and that for production an OIDC provider (Keycloak, Cognito) is the
recommended path because it supplies MFA, SSO and recovery without owning that code.

## Risks / Trade-offs

- [Rotation race logs out a legitimate user] → grace window returning 409, single-flight in clients, and a test for it.
- [Access token valid up to 15 minutes after logout or password change] → short lifetime; documented; upgrade path in
  decision 5.
- [Refresh token theft by XSS when stored in a body-delivered client] → cookie transport option; short access lifetime;
  reuse detection.
- [Grace window lets a replay inside 10 s through] → small window; the next legitimate rotation invalidates the stolen
  token and later replays revoke the session.
- [`app_user` gains an `UPDATE` grant] → limited to one column; covered by a test of the grants.
- [Per-IP limiting affects users behind one NAT] → generous default, configurable, same trade-off as login.
- [Expired rows accumulate] → small rows, indexed lookups; purge job added with group 4.

## Migration Plan

1. `M0006` creates the tables, indexes and grants (with `Down` dropping them); version 6.
2. Deploy API and migrator together. Existing one-hour tokens keep working until they expire; they carry no `sid`, so
   such tokens simply cannot be tied to a session (renewal is available only after the next login).
3. Rollback: revert the API, run `down`; clients return to the one-hour token and ignore the extra login fields.

## Open Questions

- Should the default idle period be shorter for production (for example 2 hours)? It is configuration only; the specs and
  tasks do not change.
