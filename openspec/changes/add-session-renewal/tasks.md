## 1. Foundations

- [x] 1.1 Write ADR-004 (session renewal) comparing rotating refresh tokens, sliding renewal, cookie sessions and an OIDC provider, and verify it states why cookie sessions were rejected and why OIDC is the production recommendation
- [x] 1.2 Add `TimeProvider` to the API and test host, plus `SessionOptions` (access 5 to 30 min, idle, absolute days, grace seconds, cookie flag, allowed origins) validated at start-up, and verify invalid values stop the application with a clear message
- [x] 1.3 Add migration `M0006` (`auth_session`, `refresh_token`, `auth_event`, indexes, column-level grants, no DELETE, append-only events) and verify up/down/up succeeds and the application role cannot delete or alter events

## 2. Login and rotation (tests first, then implementation)

- [x] 2.1 Write red tests for short access lifetime, refresh token returned at login with only its digest stored, and failed login creating nothing; then implement and verify they pass
- [x] 2.2 Write red tests for rotation (new pair issued, old token dead, unknown token refused) and implement `POST /auth/refresh`; verify they pass
- [x] 2.3 Write red tests for reuse detection outside the grace window (session revoked, event recorded) and the 409 inside it; then implement and verify they pass
- [x] 2.4 Write a concurrency test where many parallel requests present one refresh token and implement the row-lock rotation; verify exactly one new token is issued and the session stays active

## 3. Expiry, logout and password change

- [x] 3.1 Write red tests with a controllable clock for absolute cap, inactivity expiry and a renewal near the cap not extending it; then implement and verify they pass
- [x] 3.2 Write red tests and implement `POST /auth/logout` (current session only, other sessions unaffected); verify they pass
- [x] 3.3 Write red tests and implement `POST /auth/password` (wrong current password, weak new password, all sessions revoked, old password rejected); verify they pass and that only `password_hash` can be updated on `app_user`
- [x] 3.4 Add a test that a role change takes effect on the next request while the session is active, and verify no role is stored in tokens or sessions

## 4. Events, limits and cookie transport

- [x] 4.1 Write red tests and implement `auth_event` writes for login, refresh, reuse_detected, logout and password_changed; verify no token or password appears in any event
- [x] 4.2 Generalise the login rate limiter to `/auth/refresh` and `/auth/logout` and verify 429 with `Retry-After` in a test
- [x] 4.3 Write red tests and implement cookie transport (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/auth`, no token in body, `Origin` allow-list, cookie cleared on logout); verify they pass and that body transport still works when the flag is off

## 5. Hardening and documentation

- [x] 5.1 Extend the isolation guard so every new `/auth` route is covered (anonymous, wrong user, rate limit) and verify the suite fails if a route lacks its case
- [ ] 5.2 Update the existing authentication tests and README (new response fields, 15-minute lifetime, refresh and logout examples, cookie option) and `.env.example`, and verify the documented commands work against a running stack
- [ ] 5.3 Update both build logs (architecture, dictionary, new PR entry in the six-question format) and regenerate the Word and PDF exports; verify the log tests pass and the exports open
- [ ] 5.4 Run the full suite, `dotnet format --verify-no-changes`, Compose start and migrations up/down/up, and verify everything is green before marking the change complete
