## Why

Access tokens expire after one hour and nothing renews them, so a user who is still working gets a 401 and must log in
again. Lengthening the token would make a stolen token more valuable. The web interface planned for group 8 needs
sessions that renew safely, can be revoked and end after a fixed time. This change should land before group 8 starts.

## What Changes

- Shorten the access token lifetime from 1 hour to a configurable default of 15 minutes.
- Login also returns an opaque **refresh token** (256 random bits). Only its SHA-256 hash is stored in PostgreSQL.
- Add `POST /auth/refresh`: exchanges a refresh token for a new access token and a **new** refresh token (rotation);
  the old one becomes unusable.
- **Reuse detection:** presenting an already-rotated refresh token outside a short grace window revokes the whole
  session (token family). Presenting it inside the grace window answers 409 and revokes nothing, so two browser tabs
  racing do not log the user out.
- Sessions end at an **absolute cap** (default 30 days) and after **inactivity** (default 8 hours), both configurable.
- Add `POST /auth/logout` (revokes the current session) and `POST /auth/password` (change own password; revokes all of
  the user's sessions).
- Record session events (`login`, `refresh`, `reuse_detected`, `logout`, `password_changed`) in an append-only,
  user-scoped `auth_event` table. Group 5 later folds them into the hash-chained audit log.
- Rate-limit `POST /auth/refresh` and `POST /auth/logout` per IP, like login.
- Optional **HttpOnly cookie transport** for the refresh token (`Session__RefreshCookie=true`) for the web interface,
  with `SameSite=Strict`, `Path=/auth`, `Secure` and an `Origin` check against `Session__AllowedOrigins`.
- Add **ADR-004** (session renewal) recording the chosen design, why cookie sessions were rejected and why an OIDC
  provider is the recommended production path.
- **BREAKING (behavioural):** access tokens now last 15 minutes instead of one hour, and the login response gains
  `refreshToken` and `expiresIn` fields. Clients must renew or log in again after 15 minutes. Existing fields are kept.
- Company roles stay out of the token and are still read from `membership` on every request, so removing a user from a
  company or changing a role takes effect immediately without touching sessions. No membership-removal route exists
  today; none is added here.

## Capabilities

### New Capabilities
- `session-management`: renewal, rotation, reuse detection, expiry, logout, password change, session audit events,
  rate limiting and cookie transport for the refresh token.

### Modified Capabilities
<!-- None: no spec has been archived into openspec/specs yet. The pending requirement "Authentication" in
mvp-reconciliation-core/specs/tenancy-and-access keeps its text; this change adds behaviour around it. -->

## Impact

- **Code:** `src/Reconciliation.Core/Tenancy` (token issuing, `JwtTokenPolicy`, `LoginProtection`, new session service
  and endpoints), `src/Reconciliation.Migrator/Migrations` (new `M0006`), `src/Reconciliation.Api/Program.cs`.
- **Database:** new tables `auth_session`, `refresh_token` and `auth_event`. They are per user, not per company, so they
  sit outside the company row-level-security policies (see design). Grants for `cfdi_app` are `SELECT`/`INSERT` and
  `UPDATE` on named columns only; no `DELETE`.
- **API:** two new routes under `/auth`, one more route for password change, changed login response and token lifetime.
- **Configuration:** `Session__AccessTokenMinutes`, `Session__IdleMinutes`, `Session__AbsoluteDays`,
  `Session__ReuseGraceSeconds`, `Session__RefreshCookie`, `Session__AllowedOrigins`, `RefreshRateLimit__IpPermitLimit`.
- **Docs:** ADR-004, both build logs (architecture, dictionary, new PR entry), README endpoint list and `.env.example`.
- **Tests:** new suites for rotation, reuse, expiry (with an injectable clock), concurrency, logout, password change,
  cookie transport and rate limiting; the existing authentication tests are updated for the shorter lifetime.
- **Dependencies:** none new (`TimeProvider` and `RandomNumberGenerator` are in the base library).
- **Placement:** after group 7 and before group 8 of `mvp-reconciliation-core`; independent of groups 4 to 7.
