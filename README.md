# Game Store API

## Local secrets and configuration

JWT signing keys and the initial admin username/password are not stored in `appsettings.json`. For local development, store them with .NET User Secrets from the workspace root:

```powershell
dotnet user-secrets set "Jwt:Key" ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))) --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Username" "StoreAdmin" --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Email" "admin@example.com" --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Password" "Replace-with-a-unique-secret-593!" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Host" "smtp.example.com" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Port" "587" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Username" "smtp-account" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Password" "SMTP-provider-secret" --project GameStore.Api
dotnet user-secrets set "Email:FromAddress" "noreply@example.com" --project GameStore.Api
dotnet user-secrets set "ExternalAuth:Authority" "https://identity-provider.example.com" --project GameStore.Api
dotnet user-secrets set "ExternalAuth:ClientId" "oidc-client-id" --project GameStore.Api
dotnet user-secrets set "ExternalAuth:ClientSecret" "oidc-client-secret" --project GameStore.Api



dotnet user-secrets set "ExternalAuth:Google:ClientId" "YOUR_GOOGLE_CLIENT_ID"
dotnet user-secrets set "ExternalAuth:Google:ClientSecret" "YOUR_GOOGLE_SECRET"

dotnet user-secrets set "ExternalAuth:Microsoft:ClientId" "YOUR_MICROSOFT_CLIENT_ID"
dotnet user-secrets set "ExternalAuth:Microsoft:ClientSecret" "YOUR_MICROSOFT_SECRET"

```

Use the bootstrap username, email, and password only when creating the first admin account. Existing databases keep their current admin credentials; changing these secrets does not rename or reset an existing admin. User Secrets are for development only and are stored outside the repository. In deployed environments, supply `Jwt__Key`, `BootstrapAdmin__Username`, `BootstrapAdmin__Email`, and `BootstrapAdmin__Password` through the platform's secret manager/environment configuration. Production must configure SMTP delivery. Never commit real secrets.

The application validates that the JWT signing key is at least 32 bytes. `Jwt:Issuer`, `Jwt:Audience`, and the local SQLite connection string are non-secret settings and remain in `GameStore.Api/appsettings.json`.

Access tokens last 15 minutes by default. A refresh token expires after 30 minutes without being rotated, and its session has a hard seven-day maximum lifetime. These values can be overridden with `Jwt:AccessTokenMinutes`, `Jwt:RefreshTokenIdleMinutes`, and `Jwt:MaxSessionDays` (environment variables: `Jwt__AccessTokenMinutes`, `Jwt__RefreshTokenIdleMinutes`, and `Jwt__MaxSessionDays`). Access-token duration is limited to 1–60 minutes, refresh-token inactivity to 5 minutes–24 hours, and maximum session lifetime to 1–90 days.

## Refresh-token flow

Successful registration durably queues an email-confirmation message. Successful login creates a separate persistent session for that login/device. Each session stores its creation time, last successful refresh, absolute expiry, and revocation status; each rotation appends a refresh-token history row linked to that session ID. `POST /auth/refresh` validates the persisted session deadlines and rotates the token inside an immediate database transaction. Concurrent requests using the same token serialize; only one can replace it. Each refresh token is one-time-use and only its SHA-256 hash is stored.

In Development, refresh tokens are returned in JSON by default. Outside Development, the default is an `HttpOnly`, `Secure`, `SameSite=Strict` refresh cookie; the refresh token is omitted from the JSON response. Browser clients must send the `X-CSRF-TOKEN` response header value back on refresh/logout requests and include cookies. Set `Auth:UseRefreshTokenCookies=false` to explicitly use body tokens. Refreshing while active slides the 30-minute inactivity expiry, but never extends the seven-day absolute session lifetime. Do not refresh in the background while idle; sign the user out at the chosen idle timeout and call `POST /auth/logout`. Reuse of a rotated token revokes only that session. Access tokens are also rejected immediately after the session is revoked, rather than remaining valid until their 15-minute expiry.

Game and genre list results are cached in process for 60 seconds by their normalized filters. Successful game create/update/delete operations invalidate those entries. Configure `Cache:CatalogSeconds` to tune or disable this cache (`0` disables it). HTTP responses remain `private, no-store`; the cache is server-side only.

The refresh-token migrations create the session and token-history tables/indexes and backfill session records from existing token rows; older-format refresh credentials are invalidated and users may need to sign in again after upgrading. The Identity migration adds normalized unique usernames, unique normalized emails, security stamps, lockout/MFA fields, and Identity token/login tables. It normalizes legacy names; a pre-existing case-insensitive username collision must be resolved before migration. Existing databases apply pending migrations at application startup.

## Account security

This project is API-only and does not host a confirmation page. `Email:ConfirmationPageUrl` (`Email__ConfirmationPageUrl`) is optional. Without it, confirmation emails contain the full Base64url-encoded Identity token and the fields/endpoint a client needs to submit it. Submit `{ "email": "...", "token": "..." }` to `POST /auth/confirm-email`. This is an expiring token, not a short numeric code. Identity still validates ownership; receiving or submitting an arbitrary token never confirms an account. Tokens must not be logged. The resend endpoint remains available.

When a frontend exists, set `Email:ConfirmationPageUrl` to its absolute HTTPS URL, without credentials or an existing fragment (for example `https://your-store.example/confirm-email`). A supplied invalid URL stops startup; an absent URL does not. Emails then contain a clickable link with URL-encoded `email` and Base64url-encoded `token` parameters in the fragment (`#email=...&token=...`). The frontend reads `window.location.hash` using `URLSearchParams`, removes the fragment with `history.replaceState`, and submits `{ email, token }` as JSON to `POST /auth/confirm-email` after the user's confirmation action. Do not send tokens to analytics. Fragments keep these values out of the initial page request and referrer headers. A separately hosted frontend must also arrange its API origin/CORS policy.

The API decodes the Base64url token back to the original Identity token and calls `UserManager.ConfirmEmailAsync(user, decodedToken)`. Only successful Identity validation confirms the account; malformed or forged tokens return HTTP 400 without confirming it.

Email changes use a separate confirmation flow. `PUT /auth/me/email` sends a link to the proposed address without changing the current email, confirmation state, security stamp, or sessions. Queueing failure returns HTTP 503 and the user can keep signing in and retry the same request later; SMTP failures are retried by the worker. Change links include `purpose=email-change` and `userId` in the fragment; the frontend must send `{ userId, email, token }` to `POST /auth/email/confirm-change` instead of the registration-confirmation endpoint. Identity's email-change token is bound to the user and proposed address. The API validates it with `ChangeEmailAsync` and atomically commits the confirmed email and session revocation. Invalid, expired, replayed, or address-conflicting links leave the current account unchanged. Users sign in again after successful confirmation. Proposed addresses are not reserved; uniqueness is checked again when confirming. Requesting a change again resends a link; confirming one change invalidates the other outstanding change tokens through the security stamp.

Production registration commits the account and encrypted confirmation-message outbox row in one transaction. SMTP failures happen in the background and do not fail registration. If queue persistence fails, registration rolls back so retrying cannot silently duplicate an account. Confirmation resends keep a generic accepted response. Email-change requests queue delivery without changing the current address or sessions; queue failures return HTTP 503.

- Username comparisons use a single ASCII case-normalization strategy; the normalized value has a unique database index. Usernames are 5–20 letters, digits, periods, underscores, or hyphens.
- The auth baseline allows 10 requests per IP per five minutes. Password login, MFA login, password changes, MFA setup, and MFA disable each additionally allow five requests per IP per five minutes. Password-reset requests, reset submissions, confirmation resends, and email-change requests share three requests per IP per fifteen minutes. All attempts count; recovery does not reset an IP budget. HTTP 429 responses include Retry-After. Five incorrect passwords lock the account until recovery; confirming an unconfirmed email or completing a password reset clears the lockout. Passwords use ASP.NET Core Identity hashing and require at least 8 characters, uppercase, lowercase, and a digit.
- Registration requires a unique email and confirmation. `POST /auth/confirm-email` confirms it; `POST /auth/email/resend-confirmation` can resend instructions. `POST /auth/password/forgot` always returns the same response to avoid revealing whether an address is registered; `POST /auth/password/reset` consumes the emailed, single-use Identity token and clears lockout. `PUT /auth/me/email` proposes a new email; the current confirmed address stays active until the new address is confirmed.
- `PUT /auth/me/username` and `PUT /auth/me/password` update the signed-in account. Password changes and account security changes rotate the security stamp and revoke existing sessions.
- JWT validation checks the current user security stamp, lockout state, role version, and persisted session on each authenticated request. A role change invalidates older access tokens immediately; logging out or resetting a password also invalidates that session's access token rather than waiting for its normal expiry.
- Authenticator-app MFA is supported at `POST /auth/me/mfa/setup`, `/auth/me/mfa/enable`, and `/auth/me/mfa/disable`; MFA login completes at `POST /auth/login/mfa`. Reauthenticate after starting setup if the setup call invalidates the current session. Recovery codes are returned only once and should be stored securely; MFA challenge tokens are short-lived and single-use.
- Optional OpenID Connect external sign-in can be enabled by setting `ExternalAuth:Authority`, `ExternalAuth:ClientId`, and `ExternalAuth:ClientSecret` (environment names use double underscores). Configure the provider's redirect URI as `https://<api-host>/signin-oidc`. Start sign-in at `GET /auth/external/login`; link an identity while authenticated via `GET /auth/external/link`. Accounts are linked by provider subject, not by matching email; new external accounts require a provider-verified email.

Production startup requires `DataProtection:KeysPath` (`DataProtection__KeysPath`) pointing to a persistent key ring shared by all API instances. Identity email-confirmation/reset tokens and OIDC state depend on these keys. Store the directory on protected persistent storage and encrypt the key ring at rest with the hosting platform's key-management service. Development uses the local ephemeral key ring.

## Password screening, proxies, and pagination

Username creation and changes share one 5–20-character ASCII policy across HTTP DTO validation, services, Identity, and bootstrap setup; periods, underscores, and hyphens are allowed. All `/auth` endpoints apply DTO validation before their handlers. Invalid fields return HTTP 400 with field-level validation errors instead of reaching service code with missing values.

Bootstrap admin creation now uses `UserManager.CreateAsync`, including the normal strength and compromised-password validators. Invalid passwords prevent first-admin creation with a safe error-code-only diagnostic; an unavailable breach-check service does not block it. This does not reset credentials in existing databases. Configure screening explicitly for offline environments before initial setup.

Sensitive actions (username/email changes, MFA enable, external-identity linking, admin role changes, and user deletion) require authentication within the last five minutes. Access-token refresh does not restart that window. For an older session, call authenticated `POST /auth/reauthenticate` with `{ currentPassword, code? }`; MFA-enabled accounts also require an authenticator or recovery code. Send the returned `reauthenticationToken` in the `X-Reauthentication-Token` header. The proof expires after five minutes and is bound to the user, session, and security stamp. Missing/expired proof returns HTTP 403; failed reauthentication returns HTTP 401. Reauthentication attempts have the stricter five-attempt IP limit and count toward account lockout. External-only users can sign in again through their provider to create a recent session. Password changes, MFA setup, and MFA disable already demand credentials in their own requests. External linking carries and revalidates a protected recent-auth proof through the callback.

Every `/auth` response, including MFA secrets, recovery codes, validation errors, unauthorized responses, and rate-limit rejections, has `Cache-Control: private, no-store` and `Pragma: no-cache`. The OIDC callback is covered too. Cache prevention runs before authentication and rate limiting so early responses receive the same protection.

External sign-in requests `prompt=login` and `max_age=0` and checks the provider's signed `auth_time` claim. Providers must support active reauthentication and return this claim; missing, stale, or implausibly future authentication times are rejected. This follows the [OpenID Connect authentication-age requirements](https://openid.net/specs/openid-connect-core-1_0.html#AuthRequest), rather than treating a freshly issued token from an old provider session as fresh authentication.

Username and role changes now revoke all existing sessions and refresh credentials in the same transaction as the account update. MFA setup/enable, password changes/resets, and external-identity linking also commit their account, key/token, stamp, and revocation changes together; failed Identity steps or database updates roll the operation back. MFA setup signs out existing sessions, so sign in again before completing enable. Enabling MFA requires ten recovery codes to be generated successfully and rejects requests when MFA is already enabled.

MFA login challenges carry the account security stamp and are rejected after the stamp changes; challenges issued before this update require a fresh login. Admin demotion and deletion acquire SQLite's non-deferred serializable write transaction before checking the admin count, preventing concurrent requests from removing every admin. Self-deletion is checked using the stable user ID rather than the username.

Disabling MFA requires an authorized session, the current password, and a valid authenticator or recovery code. The endpoint retains the auth baseline and its stricter five-attempt limit per IP per five minutes. Recovery-code consumption, disabling MFA, authenticator-key reset, security-stamp rotation, and session/refresh-token revocation run in one database transaction. Every Identity result is checked and revocation is verified before commit; a later failure rolls back earlier changes, including recovery-code consumption. Invalid credentials return HTTP 400; failed security updates return HTTP 500 rather than reporting success. Diagnostic logs include operation names, user IDs, and Identity error codes, without passwords, MFA codes, or keys.

Rate-limit rejections emit warning event `RateLimitRejected` (ID 429) in the `GameStore.Api.Security.RateLimiting` log category. Events include only the route template, whether a client IP is available, and a trace identifier; they exclude raw IPs, query strings, request bodies, usernames, passwords, and tokens. Monitor rejection counts by route and the frequency of missing IPs. Load-test normal sign-in, MFA, refresh, registration, and recovery flows, including clients sharing one public IP, before tuning these initial limits. Limits are per application instance; multiple instances need a shared gateway or distributed limiting strategy. Account lockout complements the IP limits against password guessing across changing IPs.

Identity V3 PBKDF2 hashing uses 210,000 iterations, including bootstrap admin hashes. Older hashes remain compatible and Identity upgrades them during successful password verification. Authenticator MFA remains available through the existing setup and login endpoints.

Registration, password changes, and password reset reject a built-in common-password list and passwords found by Have I Been Pwned. The range API receives only a five-character SHA-1 hash prefix, with padded results compared locally; SHA-1 is never used for password storage. A five-second timeout, network failure, service error, or malformed response skips only the remote breach check, allowing registration and password updates to continue. Local strength rules and local block lists still apply, and confirmed breach matches are rejected. This availability policy also applies to password reset and bootstrap admin creation. Existing-account login does not call this service. Bootstrap admin creation uses the same password validators; choose a strong, unique bootstrap password.

Set `PasswordScreening:CheckCompromisedPasswords=false` explicitly for offline environments; the common-password list still applies. Extend the local list with `PasswordScreening:BlockedPasswords`.

Without configured proxies, rate limits use the connection's remote IP and forwarded headers are ignored. Configure `ReverseProxy:KnownProxies` with the exact IP addresses of your trusted proxies, for example `ReverseProxy__KnownProxies__0=10.0.0.10`. Only these addresses may supply `X-Forwarded-For` and `X-Forwarded-Proto`. One proxy hop is processed before rate limiting and authentication. The proxy must replace untrusted incoming forwarding headers; restrict direct access to the backend. Deployments with multiple proxy hops need an explicit review of the trust chain and forward limit. Verify in staging that separate real clients have separate rate budgets and that spoofed headers from direct clients do not change them.

Game and genre lists reject pages outside 1–1000, page sizes outside 1–100, and offsets above 10,000 with HTTP 400 before querying the catalog. For deeper browsing, introduce cursor pagination rather than increasing this bound.

## API documentation

### Durable email delivery and catalog search

The email worker polls every five seconds and drains up to 20 messages per cycle. A two-minute database lease prevents simultaneous claims across API instances and permits recovery after worker crashes. Transport attempts time out after 30 seconds; failures retry with exponential delays starting at 30 seconds and capped at one hour, for at most ten attempts. SMTP delivery is at least once: a crash after SMTP accepts a message but before the database marks it sent can cause a duplicate email, never a duplicate account. Keep the production Data Protection key ring persistent/shared, since queued payloads are encrypted with it.

Messages expire after the smaller of twelve hours or the configured Identity token lifetime. Expired/exhausted messages are not sent and require a fresh resend request. Payloads are scrubbed after delivery/expiry/final failure, and terminal metadata is removed after seven days. Monitor `EmailOutbox` status/attempts and safe message-ID/failure-type logs; never expose encrypted payloads through an admin response or log plaintext content. `Email:Outbox:Enabled=false` pauses the worker but continues queuing messages. Integration tests pause the worker and explicitly use fake transports.

The migration `AddEmailOutboxAndCatalogSearch` creates the outbox and SQLite FTS5 trigram search tables, backfills existing catalog rows, and adds triggers for game/genre insert, update, and delete. It is applied through the existing startup migration flow; take the normal database backup before deploying schema changes. Future SQLite table-rebuild migrations must preserve/recreate these manually defined search triggers. The design-time context factory lets migration tooling inspect the model without starting the web application or requiring authentication/email secrets.

Catalog searches are limited to 100 characters before normalization or cache allocation. Substrings of at least three Unicode characters use the [FTS5 trigram index](https://www.sqlite.org/fts5.html#the_trigram_tokenizer); shorter searches use escaped literal `LIKE` matching. Search text is quoted as a literal FTS phrase and SQL parameters are used, preserving case-insensitive substring behavior without exposing query operators. `Cache:CatalogMaxEntries` defaults to 512 entries (clamped to 1–4096), each containing at most one page. The dedicated catalog cache cannot grow without bound or impose entry-size rules on Identity/framework caches; `EntryCount` exposes its utilization. `Cache:CatalogSeconds=0` still disables caching.

Without a frontend URL, email-change messages include the token, proposed email, and user ID for `POST /auth/email/confirm-change` with `{ "userId": 123, "email": "...", "token": "..." }`. The current email stays active until Identity validates the change. There are no built-in HTML, JavaScript, or stylesheet routes.

### User pagination and authentication retention

`GET /auth/users` remains administrator-only and now returns `{ items, page, pageSize, totalItems, totalPages }` rather than an array. Clients must read `items`. Defaults are page 1 and pageSize 20; page is capped at 1000, pageSize at 100, and offset at 10000. Users are ordered by ID and only ID, username, and role are exposed.

An hourly cleanup worker deletes at most 500 retired sessions per pass, plus up to 1000 old orphan refresh-token rows. Sessions must have been revoked, absolutely expired, or idle-expired for at least seven days. All refresh history for live sessions is retained for replay detection; deleting an old rotated token alone would weaken that protection. The seven-day retention is an operational default, not a compliance guarantee. `Auth:CleanupEnabled=false` disables the worker for tests or externally scheduled maintenance. A backlog above these limits requires additional maintenance capacity.

### Rate-limit tuning and deployment

Configure `RateLimits:BaselinePermits` (10), `BaselineWindowSeconds` (300), `PasswordPermits` (5), `PasswordWindowSeconds` (300), `RecoveryPermits` (3), and `RecoveryWindowSeconds` (900). All are validated positive at startup, and changes require restart. Named auth and sensitive-route budgets still both apply. Tune against legitimate traffic, especially shared public IPs. A staging-only shared-IP burst probe is available: `k6 run -e BASE_URL=https://staging-api.example scripts/auth-rate-load.js`. Install k6 separately and use a disposable environment; the script uses a nonexistent account, submits no registration/recovery requests, and reports latency, safe status codes, throttling, and Retry-After checks. It is a burst probe, not a complete production capacity assessment. Run representative successful customer flows and separate-client tests before rollout.

For multiple API instances, set `Deployment:MultipleInstances=true`. This disables catalog response caching so local invalidation cannot leave another replica serving stale catalog entries. Startup also requires `Deployment:SharedRateLimitingEnforced=true`: set this only after configuring a shared gateway/service to enforce aggregate limits across replicas. This declaration does not install or verify that infrastructure. Keep the local limiters as additional protection, restrict direct backend access, configure trusted proxy addresses, and share protected Data Protection keys. Independent per-replica gateway limiters are insufficient. SQLite still needs a supported single-host storage arrangement; these options do not make a database file safe for arbitrary network-mounted replicas. Larger deployments should migrate to a suitable shared database before scaling across hosts.

Integration factories use per-host settings, an isolated in-memory SQLite database, fake breach/email services, and independent ephemeral Data Protection keys. They no longer mutate process environment variables or depend on the developer's key-ring directory. Cookie-mode tests override only their own host. Background delivery and cleanup are disabled unless explicitly exercised.

Run the API in the Development environment and open `https://localhost:7045/swagger` or `http://localhost:5019/swagger`. The OpenAPI document is available at `/swagger/v1/swagger.json`. Swagger UI and its document are enabled only in Development. Use the **Authorize** control to supply a bearer token; protected operations are marked as requiring one.

## Tests

Run API integration tests from the workspace root:

```powershell
dotnet test GameStore.Api.Tests/GameStore.Api.Tests.csproj
```

Integration tests cover token rotation and expiry, concurrent refresh, email confirmation/recovery, account lockout, role/password revocation, MFA recovery codes, rate limiting, and session isolation.

### CI and repository hygiene

`.github/workflows/tests.yml` runs the full integration suite on pushes and pull requests using .NET 9 on Linux, then builds the maintenance tool. Failures keep their nonzero exit code. TRX reports and hang diagnostics are uploaded for seven days even after failures; process memory dumps and environment dumps are not collected. The workflow has read-only repository permissions and requires no production secrets. It must run on your GitHub repository before its remote status can be confirmed.

Build output (`bin`, `obj`), IDE state, local SQLite files/sidecars, backups, and common secret-file formats are ignored. Previously tracked build output and the local `GameStore.db` have been removed from the Git index while remaining on disk. Their removal is staged; commit it with the source changes. New checkouts create the schema from migrations and provision the first admin using the documented bootstrap configuration. Untracking files does not erase earlier Git history; historical database copies still need your normal sensitive-data handling.

### Operational health

`GET /health/live` returns HTTP 200 when the process can respond and deliberately ignores dependency failures. `GET /health/ready` reads a small projection from the mapped `Genres` table and returns HTTP 503 if the database is unavailable or that schema is missing. Both endpoints return only a terse status, disable response caching, and require no login so infrastructure can probe them. Restrict them at the gateway if probes should be private. Readiness checks are tagged separately and configured with a three-second timeout. They do not test SMTP, free disk space, all tables, or write capability; an SMTP outage should not stop registration because delivery is queued. Use separate alerts for outbox backlog and storage capacity.

### Database backup and restore rehearsal

The maintenance console shares the API's SQLite backup implementation without starting its web host. Run from the repository root with a current migrated database and an existing private destination directory:

```powershell
dotnet run --project tools/GameStore.Maintenance -- backup GameStore.Api/GameStore.db C:/PrivateBackups/gamestore-20261025.db
dotnet run --project tools/GameStore.Maintenance -- verify C:/PrivateBackups/gamestore-20261025.db
```

Use a new filename for each snapshot. The source must exist, and existing destinations are never overwritten. The tool uses SQLite's [online backup API](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup), including committed WAL data. That operation can block writers briefly, so schedule it during quieter periods. It then reopens the snapshot and checks integrity, foreign keys, required application tables, and all six catalog-search triggers. Invalid snapshots cause a nonzero exit code; failed files are retained and must not be treated as verified backups. Checks target the current application schema; take a protected raw database backup with tooling compatible with the old schema before upgrading older deployments.

Schedule backups with your hosting platform, keep encrypted off-host copies with a retention policy, and protect access as strictly as the live database. Database backups do not include Data Protection keys: back up the protected key ring separately through your platform's key-management process. Losing the keys makes queued email payloads unreadable and invalidates protected Identity tokens even when the database is intact. This console does not configure a scheduler, off-site storage, or key backups.

To rehearse restoration, copy a verified snapshot to a separate private directory, use a compatible application build and the protected key ring, configure an isolated connection string, and prevent SMTP/outbox delivery during the rehearsal. Check user/session counts, catalog search, and account flows using test accounts. Automated tests restore a snapshot into a separate SQLite connection, verify account/session/outbox preservation, exercise search-trigger updates, and confirm that the source remains unchanged. They do not replace a production recovery drill. For an actual recovery, stop the API and workers, preserve the damaged database and its sidecars, restore using your host's procedure, and validate readiness and account flows before reopening traffic. Do not replace a database under running SQLite connections.

### Authentication service responsibilities

`AuthService` remains the endpoint-facing coordinator for registration, primary login, email confirmation/change, and password recovery. It delegates persistent session creation, refresh rotation/replay handling, revocation, and signed-token generation to `AuthSessionService`; authenticator/challenge/recovery-code operations to `MfaService`; and confirmation email construction/queuing to `AccountEmailService`. All four are scoped and share the same `GameStoreContext` and Identity manager, so this separation preserves the existing atomic security transactions and public endpoint contracts. The outbox worker still owns SMTP retries. File-level and operation comments explain the security boundaries.
