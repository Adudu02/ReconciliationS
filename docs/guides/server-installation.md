# Server installation and first start

Run the server on laptop A; use a browser on laptop A or laptop B on the same LAN.
The API container serves both the UI and its API. Laptop B needs no executable or SDK.
This is a synthetic-data demo; results have no fiscal or legal validity.

## Requirements

- Docker Engine with access to its daemon and Docker Compose v2 supporting `up --wait`.
- Bash, OpenSSL, and `ip` from iproute2 for the LAN launcher.
- Git to obtain/update the checkout; curl for the checks and API recipes below.
- Internet access for the initial image pulls and build dependencies.

No host .NET SDK is needed. Run all commands in the repository root. Check the tools:

```bash
docker version
docker compose version
docker compose up --help
bash --version
openssl version
ip -4 route
curl --version
```

Confirm that `up --help` lists `--wait`. Docker installation instructions depend on
which operating system runs laptop A; use Docker's instructions for that system.

## Choose a mode

| Mode | Startup | Browser URL |
|---|---|---|
| A: local only | `JWT_SIGNING_KEY="$(openssl rand -hex 32)" docker compose up --build --wait` | `http://localhost:8080` on laptop A |
| B: LAN demo | `./scripts/demo-lan.sh` | The printed URL on laptop B |

The one-line local command uses a new key each time. For ongoing operation, export
and retain one private key as shown below so later Compose commands can resolve it.
For mode B, follow [Demo on two laptops](../../README.md#demo-on-two-laptops);
that section documents IP overrides, the launcher, and `.env.demo` persistence.
PostgreSQL stays bound to `127.0.0.1:5432` in both modes. The worker and SAT stub
have no host ports. Sources: [Compose](../../docker-compose.yml),
[launcher](../../scripts/demo-lan.sh).

## Environment variables

Set these before startup; shell exports take precedence over Compose env files.
Do not commit passwords or signing keys. The LAN launcher loads `.env` if present
and `.env.demo` if present; plain `docker compose` loads `.env`, not `.env.demo`.

| Name | Required? | Default | Purpose |
|---|---|---|---|
| `JWT_SIGNING_KEY` | Yes; launcher generates it if unset and no `.env.demo` exists | None | Random signing secret, at least 32 UTF-8 bytes; `openssl rand -hex 32` produces a suitable value. Retain it between runs. |
| `OPERATOR_EMAIL` | Both operator variables for first bootstrap | Empty | Valid email for the first platform operator, e.g. `operator@example.invalid`. |
| `OPERATOR_PASSWORD` | With `OPERATOR_EMAIL` | Empty | At least 12 characters. Used during migration bootstrap. |
| `APP_DB_PASSWORD` | Optional for the demo | `synthetic-local-app-password` | Password for `cfdi_app`; change outside demos and keep the same value for migrations, API, and worker. |
| `API_BIND` | Optional | `127.0.0.1` | API host bind address. The LAN launcher exports `0.0.0.0`; API port stays 8080. |
| `Session__AllowedOrigins__0` | Set the exact origin when browser cookie transport is enabled | `http://localhost:8080` | Browser login/refresh/logout origin, including scheme and port, with no path or trailing slash. The LAN launcher sets its printed origin. |
| `Session__BrowserRefreshCookie` | Optional | `false` | Enables the UI's opt-in refresh-cookie transport. Default UI sessions require sign-in after reload or failed renewal. |
| `JOBS_MAX_ATTEMPTS` | Optional | `5` | Job attempt budget used by API and worker; allowed range 1–100. |

The operator bootstrap validates the email and password and inserts only a new
account. Existing accounts are never modified, promoted, or given a new password.
If either operator variable is supplied, both must be valid. An existing administrator
email in company creation also keeps its password. Sources:
[OperatorBootstrap.cs](../../src/Reconciliation.Migrator/OperatorBootstrap.cs),
[MigrationCommands.cs](../../src/Reconciliation.Migrator/MigrationCommands.cs),
[CompanyAdminEndpoints.cs](../../src/Reconciliation.Core/Tenancy/CompanyAdminEndpoints.cs),
[JobOptions.cs](../../src/Reconciliation.Core/Jobs/JobOptions.cs).

Additional SAT and worker tuning variables are listed in `docker-compose.yml` and
the main README. SAT defaults to the local stub; this guide does not enable live SAT.
`APP_DB_PASSWORD` changes only the application role, not the synthetic PostgreSQL
owner credentials `cfdi` / `cfdi` in Compose. Those also need replacement outside demos.

## First start

1. Choose an operator email and password. In a Bash terminal, set both before
   the first migration; the password prompt hides input and avoids a literal
   password in shell history:

   ```bash
   export OPERATOR_EMAIL=operator@example.invalid
   read -r -s -p 'Operator password (at least 12 characters): ' OPERATOR_PASSWORD
   printf '\n'
   export OPERATOR_PASSWORD
   ```

2. Start exactly one mode. For local-only mode, generate/export a key and keep
   this terminal for later commands. Keep a private copy if it must survive the shell:

   ```bash
   export JWT_SIGNING_KEY="$(openssl rand -hex 32)"
   docker compose up --build --wait
   ```

   For LAN mode, run instead:

   ```bash
   ./scripts/demo-lan.sh
   ```

3. In another terminal with the same environment, check services:

   ```bash
   docker compose ps --all
   curl -fsS http://localhost:8080/health
   ```

   For LAN mode, the launcher does not export its child-process variables back to
   your shell. If it created `.env.demo`, use
   `docker compose --env-file .env.demo ps --all` instead. If you supplied an
   exported key, retain it. PostgreSQL, API, worker, and SAT stub should be healthy;
   **migrate is a one-shot service and should be Exited (0)**, not running/healthy.
   On laptop B, replace localhost in the health URL with the printed LAN address.

4. Open `http://localhost:8080` (local) or the launcher's printed URL (LAN).
   Sign in as the operator. An operator without memberships sees an empty company
   picker; that is expected. Company creation has no UI page.

5. Create the first company and its administrator with the following **two API calls**.
   Replace password placeholders with the chosen values using valid JSON escaping.
   Use a new administrator email for this example; its password must have at least
   12 characters. `AAA010101AAA` and the company name below are synthetic.
   Change `BASE` to the printed origin when running curl from laptop B.
   Cookie transport headers are deliberately absent so these are JSON-token calls.

   ```bash
   BASE=http://localhost:8080
   curl -fsS -X POST "$BASE/auth/tokens" \
     -H 'Content-Type: application/json' \
     -d '{"email":"operator@example.invalid","password":"<operator-password>"}'
   ```

   Copy `accessToken` from that response into `OPERATOR_TOKEN`; keep it private:

   ```bash
   OPERATOR_TOKEN='<accessToken from the preceding response>'
   curl -fsS -X POST "$BASE/companies" \
     -H "Authorization: Bearer $OPERATOR_TOKEN" \
     -H 'Content-Type: application/json' \
     -d '{"rfc":"AAA010101AAA","name":"Synthetic demo company","adminEmail":"admin@example.invalid","adminPassword":"<administrator-password>"}'
   ```

   Expect `201 Created` and the company's `id`, `rfc`, `name`, and `archivedAt`.
   No `X-Company-Id` header is needed for this operator-only creation call.
   Fields and authorization are defined in
   [Authentication.cs](../../src/Reconciliation.Core/Tenancy/Authentication.cs) and
   [CompanyAdminEndpoints.cs](../../src/Reconciliation.Core/Tenancy/CompanyAdminEndpoints.cs).

6. Sign out of the operator account. Sign in as `admin@example.invalid` with the
   administrator password. The new company should appear in the picker. Select it;
   the header shows its name and “Administrador” (or “Administrator” in English).
   Continue with the [Spanish](user-guide.es.md) or [English](user-guide.en.md) user guide.

## Network setup for laptop B

Use the same reachable LAN/subnet. Guest Wi-Fi or access-point client isolation
can block laptop-to-laptop connections even with matching network names.
Use laptop A's LAN address, not `localhost`, on laptop B. With multiple interfaces
or a VPN, set `LAN_IP` to laptop A's reachable LAN IPv4 before running the launcher.

If firewalld is in use on laptop A, the owner can allow the API port for the current
runtime firewall configuration (the launcher only prints this hint):

```bash
sudo firewall-cmd --add-port=8080/tcp
```

Plain HTTP is acceptable only for this trusted-LAN synthetic demo; passwords,
tokens, and data are unencrypted. Leave browser refresh cookies disabled for the
basic demo. The browser transport implementation sets Secure on HTTPS requests;
legacy global `Session__RefreshCookie` always sets Secure and can prevent cookie
renewal on plain HTTP. Do not rely on HTTP cookie behavior for deployment: use
HTTPS and the exact allowed origin. See
[ADR-004](../adr/ADR-004-session-renewal.md) and
[ADR-010](../adr/ADR-010-browser-ui-and-api-contract.md).

## Operating the server

Run from the same checkout/project and retain the original signing key and password
settings. The commands below use `docker compose` for local mode. For LAN mode,
if the launcher created `.env.demo`, add `--env-file .env.demo` immediately after
`docker compose` for inspection, logs, backup, stop, or reset commands. If `.env`
also holds custom values, load it first with `--env-file .env --env-file .env.demo`.
If you supplied `JWT_SIGNING_KEY` directly, keep it exported instead.
For starting/updating LAN mode, always use the launcher so API binding and origin
are reapplied.

| Operation | Local mode command | LAN mode |
|---|---|---|
| Inspect | `docker compose ps --all` | Add the env-file option if needed. |
| Follow logs | `docker compose logs -f api worker` | Add the env-file option if needed. Use Ctrl+C to stop following logs. |
| Stop, retain data | `docker compose down` | `./scripts/demo-lan.sh down` |
| Start again after stopping | `docker compose up -d --build --wait` | `./scripts/demo-lan.sh` |
| Restart API/worker without changing configuration | `docker compose restart api worker` | Add the env-file option if needed; this does not apply changed environment settings. |

For an update, first make a backup, retain the checkout's current revision for
recovery, and then run `git pull --ff-only`. Rebuild/start with
`docker compose up -d --build --wait` (local) or `./scripts/demo-lan.sh` (LAN).
Migrations run before the application services. Do not assume reverting code alone
undoes database migrations; retain and verify backups before updating.

### Backup and restore

Back up the PostgreSQL database stored in `pgdata` with a logical custom-format dump.
Choose a private directory outside the repository and a new destination filename:

```bash
backup="$HOME/reconciliation-demo-$(date +%Y%m%d-%H%M%S).dump"
(umask 077; set -o noclobber; docker compose exec -T postgres pg_dump -U cfdi -d cfdi -Fc > "$backup")
```

Check that the command exits successfully; a failed dump can leave a partial file.
The dump contains sensitive account/session and uploaded data. Keep it private.
This backs up database contents, not the separate `sat-recordings` volume, server
configuration, signing key, or PostgreSQL cluster roles. Retain those separately.

Restore with PostgreSQL 17 `pg_restore` into an **isolated, initialized installation**
with the matching schema version and required database roles. Stop API/worker writes
in that target; inspect the archive first with `pg_restore --list`. A restore into
an existing database needs an explicit replacement/cleanup plan; never restore over
the only copy. Verify restored memberships and reconciliation data before switching
users to it. The Compose PostgreSQL image contains `pg_dump` and `pg_restore`.

### Reset a disposable demo

**Destructive: `down -v` deletes the database and SAT-recording volumes, including all
companies, users, imports, and decisions. Back up anything needed first.**

```bash
docker compose down -v
```

For LAN mode, add the env-file option described above. Restart in the selected mode
with the operator variables set to bootstrap again. `.env.demo` is a host file and
is retained by `down -v`; keep it private. To merely stop, use `down` without `-v`.

## Troubleshooting

| Symptom | Check | Fix |
|---|---|---|
| Missing JWT key / “Set JWT_SIGNING_KEY…” | Is a key available in this shell or env file? | Export a retained random key of at least 32 bytes, or use the LAN launcher. Load `.env.demo` explicitly for raw Compose commands. |
| Port already in use | Is another stack/process using host 8080 or 5432? | Stop the conflicting process/stack or deliberately configure a separate port override; `API_BIND` changes address, not port. |
| Migrate failed / startup dependencies fail | `docker compose logs migrate`; distinguish Exited (0) from a nonzero exit. | Supply both valid operator variables, check database connectivity, fix the logged cause, then repeat the selected startup command. |
| UI loads but login fails | Wrong credentials? Cookie transport enabled with a mismatched origin? | Use the correct account; match origin exactly. For the basic HTTP demo leave browser cookie transport false. Legacy Secure-cookie transport can fail renewal over HTTP. |
| Laptop B cannot connect | Health URL, host firewall, network reachability/client isolation. | Use laptop A's LAN IP and LAN mode; allow 8080 on A if needed; use a network allowing peer access. PostgreSQL should remain unreachable on the LAN. |
| Wrong LAN IP printed | VPN/multiple network interfaces. | Set `LAN_IP` to A's reachable LAN IPv4 and rerun the launcher. |
| “operator already exists, not modified” | Does the email already exist, possibly as a non-operator? | Use the existing account's original credentials; bootstrap never resets passwords or promotes users. Bootstrap a new unused operator email if necessary. |

## Security notes

- Compose owner/application credentials are synthetic demo defaults; replace both outside demos.
- Use random signing keys and strong unique passwords; bootstrap requires at least 12 characters.
- HTTP in this demo provides no transport encryption; deployment requires HTTPS.
- Never import real client data; use synthetic or properly anonymised inputs only.
- Results have no fiscal or legal validity.
- Keep `.env.demo`, `.env`, tokens, and backups private; never commit or share them.
- API port 8080 is LAN-accessible in demo mode; PostgreSQL remains on loopback.
