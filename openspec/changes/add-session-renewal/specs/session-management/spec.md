## Purpose

Lets a signed-in user keep working without logging in again every few minutes, while limiting the damage of a stolen
credential: short-lived access, single-use renewal credentials, detection of stolen-token reuse, bounded session
lifetime and the ability to end sessions. User story: as an accountant, I want my session to renew while I work and end
when I log out or change my password so that a stolen token cannot be used for long.

## ADDED Requirements

### Requirement: Short-lived access tokens
The system SHALL issue access tokens that expire after a configurable number of minutes (default 15, allowed range 5 to
30) and SHALL refuse an expired access token.

#### Scenario: Access token expires
- **WHEN** a request presents an access token older than its lifetime
- **THEN** the request is refused as unauthenticated

#### Scenario: Invalid lifetime configuration
- **WHEN** the configured access lifetime is outside 5 to 30 minutes
- **THEN** the application refuses to start with a clear message

### Requirement: Login issues a refresh credential
Successful login SHALL return an access token, a refresh token and the access token lifetime in seconds. The refresh
token SHALL be an unguessable random value with at least 256 bits of entropy, and the system SHALL store only a
non-reversible digest of it.

#### Scenario: Successful login
- **WHEN** a user logs in with valid credentials
- **THEN** the response contains an access token, a refresh token and `expiresIn`, and the database holds no plaintext
  refresh token

#### Scenario: Failed login
- **WHEN** credentials are wrong
- **THEN** no session and no refresh token is created

### Requirement: Rotation on every renewal
Presenting a valid refresh token SHALL return a new access token and a new refresh token and SHALL make the presented
refresh token unusable, within the same session.

#### Scenario: Renew a session
- **WHEN** a client presents a valid, unexpired refresh token
- **THEN** it receives a new access token and a new refresh token, and the old refresh token no longer renews

#### Scenario: Unknown refresh token
- **WHEN** a client presents a token that was never issued
- **THEN** the request is refused as unauthenticated and no session is changed

### Requirement: Reuse detection
If a refresh token that was already rotated is presented again after a short grace period (default 10 seconds), the
system SHALL revoke the whole session and record the event. Inside the grace period it SHALL answer a conflict without
revoking anything, so that two concurrent renewals by the same legitimate client do not end the session.

#### Scenario: Stolen token replayed
- **WHEN** an already-rotated refresh token is presented after the grace period
- **THEN** the request is refused, every refresh token of that session stops working and a `reuse_detected` event is
  recorded

#### Scenario: Concurrent renewal inside the grace period
- **WHEN** two requests present the same valid refresh token at nearly the same time
- **THEN** exactly one succeeds and the other receives 409, and the session stays active

#### Scenario: Rotation is atomic
- **WHEN** many concurrent requests present the same refresh token
- **THEN** exactly one new refresh token is issued and no request receives a second valid token

### Requirement: Bounded session lifetime
A session SHALL end after an absolute maximum age (default 30 days) and after a period without renewal (default 8
hours of inactivity), both configurable; renewal SHALL NOT extend a session beyond the absolute cap.

#### Scenario: Absolute cap
- **WHEN** a session reaches its absolute maximum age
- **THEN** the next renewal is refused even if the session was active a minute earlier

#### Scenario: Inactivity
- **WHEN** a refresh token is presented after the inactivity period
- **THEN** the renewal is refused

#### Scenario: Renewal does not extend the cap
- **WHEN** a session renews close to its absolute cap
- **THEN** the new refresh token expires no later than the cap

### Requirement: Logout
The system SHALL let an authenticated user end the current session, after which none of its refresh tokens renews.

#### Scenario: Logout
- **WHEN** a user logs out
- **THEN** that session's refresh tokens are refused afterwards and a `logout` event is recorded

#### Scenario: Other sessions unaffected
- **WHEN** a user logs out on one device while signed in on another
- **THEN** the other session keeps renewing

### Requirement: Password change ends all sessions
The system SHALL let an authenticated user change their own password by supplying the current one, SHALL enforce the
minimum password length, and SHALL revoke every session of that user.

#### Scenario: Password changed
- **WHEN** a user changes their password
- **THEN** every refresh token of that user stops working, the new password logs in and the old one does not

#### Scenario: Wrong current password
- **WHEN** the supplied current password is wrong
- **THEN** the change is refused and no session is revoked

#### Scenario: Weak new password
- **WHEN** the new password is shorter than the minimum length
- **THEN** the change is refused and nothing changes

### Requirement: Roles remain live
Company roles SHALL NOT be stored in tokens or sessions and SHALL be evaluated from current membership on every
request, so a role change or removal takes effect immediately.

#### Scenario: Role removed while the session is active
- **WHEN** a user's membership in a company is changed to viewer while their session is active
- **THEN** their next request to that company is authorised as viewer without logging in again

### Requirement: Session events are recorded
The system SHALL record login, renewal, reuse detection, logout and password change as append-only events tied to the
user and session, and SHALL NOT store tokens or passwords in them.

#### Scenario: Event recorded
- **WHEN** a session is renewed
- **THEN** a `refresh` event with the user, session and time is stored and no token value appears in it

#### Scenario: Events cannot be altered
- **WHEN** the application role attempts to update or delete an event
- **THEN** the database refuses it

### Requirement: Renewal rate limiting
The system SHALL limit renewal and logout requests per remote IP in a fixed window and answer 429 with `Retry-After`
when the limit is exceeded.

#### Scenario: Excess renewal attempts
- **WHEN** one IP exceeds the configured renewals per window
- **THEN** further requests in that window receive 429 with `Retry-After`

### Requirement: Cookie transport for the refresh token
When cookie transport is enabled, the system SHALL deliver the refresh token only in an `HttpOnly`, `Secure`,
`SameSite=Strict` cookie scoped to the authentication routes, SHALL omit it from the response body, and SHALL refuse a
cookie-based renewal whose `Origin` is not on the configured allow-list. When disabled, the token travels in the JSON
body.

#### Scenario: Cookie delivered
- **WHEN** a user logs in with cookie transport enabled
- **THEN** the refresh token is in a `Set-Cookie` header with `HttpOnly`, `Secure`, `SameSite=Strict` and a path limited
  to `/auth`, and the body has no refresh token

#### Scenario: Cross-site renewal
- **WHEN** a renewal arrives with a cookie but an `Origin` not on the allow-list
- **THEN** it is refused and no token is rotated

#### Scenario: Cookie cleared on logout
- **WHEN** a user logs out with cookie transport enabled
- **THEN** the response clears the cookie

### Requirement: Refresh credentials stay out of company isolation
Session data SHALL be per user, not per company, SHALL NOT expose another user's sessions through any route, and the
application role SHALL have no privilege to delete session rows.

#### Scenario: Application role cannot delete
- **WHEN** the application role attempts to delete a session or refresh token row
- **THEN** the database refuses it
