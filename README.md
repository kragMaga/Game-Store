# Game Store API

## Local secrets and configuration

JWT signing keys and the initial admin username/password are not stored in `appsettings.json`. For local development, store them with .NET User Secrets from the workspace root:

```powershell
dotnet user-secrets set "Jwt:Key" ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))) --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Username" "Choose-a-unique-admin-name" --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Email" "admin@example.com" --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Password" "Replace-with-a-strong-local-password" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Host" "smtp.example.com" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Port" "587" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Username" "smtp-account" --project GameStore.Api
dotnet user-secrets set "Email:Smtp:Password" "SMTP-provider-secret" --project GameStore.Api
dotnet user-secrets set "Email:FromAddress" "noreply@example.com" --project GameStore.Api
dotnet user-secrets set "ExternalAuth:Authority" "https://identity-provider.example.com" --project GameStore.Api
dotnet user-secrets set "ExternalAuth:ClientId" "oidc-client-id" --project GameStore.Api
dotnet user-secrets set "ExternalAuth:ClientSecret" "oidc-client-secret" --project GameStore.Api
```

Use the bootstrap username, email, and password only when creating the first admin account. Existing databases keep their current admin credentials; changing these secrets does not rename or reset an existing admin. User Secrets are for development only and are stored outside the repository. In deployed environments, supply `Jwt__Key`, `BootstrapAdmin__Username`, `BootstrapAdmin__Email`, and `BootstrapAdmin__Password` through the platform's secret manager/environment configuration. Production must configure SMTP delivery. Never commit real secrets.

The application validates that the JWT signing key is at least 32 bytes. `Jwt:Issuer`, `Jwt:Audience`, and the local SQLite connection string are non-secret settings and remain in `GameStore.Api/appsettings.json`.

Access tokens last 15 minutes by default. A refresh token expires after 30 minutes without being rotated, and its session has a hard seven-day maximum lifetime. These values can be overridden with `Jwt:AccessTokenMinutes`, `Jwt:RefreshTokenIdleMinutes`, and `Jwt:MaxSessionDays` (environment variables: `Jwt__AccessTokenMinutes`, `Jwt__RefreshTokenIdleMinutes`, and `Jwt__MaxSessionDays`). Access-token duration is limited to 1–60 minutes, refresh-token inactivity to 5 minutes–24 hours, and maximum session lifetime to 1–90 days.

## Refresh-token flow

Successful registration sends an email-confirmation message. Successful login creates a separate persistent session for that login/device. Each session stores its creation time, last successful refresh, absolute expiry, and revocation status; each rotation appends a refresh-token history row linked to that session ID. `POST /auth/refresh` validates the persisted session deadlines and rotates the token inside an immediate database transaction. Concurrent requests using the same token serialize; only one can replace it. Each refresh token is one-time-use and only its SHA-256 hash is stored.

In Development, refresh tokens are returned in JSON by default. Outside Development, the default is an `HttpOnly`, `Secure`, `SameSite=Strict` refresh cookie; the refresh token is omitted from the JSON response. Browser clients must send the `X-CSRF-TOKEN` response header value back on refresh/logout requests and include cookies. Set `Auth:UseRefreshTokenCookies=false` to explicitly use body tokens. Refreshing while active slides the 30-minute inactivity expiry, but never extends the seven-day absolute session lifetime. Do not refresh in the background while idle; sign the user out at the chosen idle timeout and call `POST /auth/logout`. Reuse of a rotated token revokes only that session. Access tokens are also rejected immediately after the session is revoked, rather than remaining valid until their 15-minute expiry.

Game and genre list results are cached in process for 60 seconds by their normalized filters. Successful game create/update/delete operations invalidate those entries. Configure `Cache:CatalogSeconds` to tune or disable this cache (`0` disables it). HTTP responses remain `private, no-store`; the cache is server-side only.

The refresh-token migrations create the session and token-history tables/indexes and backfill session records from existing token rows; older-format refresh credentials are invalidated and users may need to sign in again after upgrading. The Identity migration adds normalized unique usernames, unique normalized emails, security stamps, lockout/MFA fields, and Identity token/login tables. It normalizes legacy names; a pre-existing case-insensitive username collision must be resolved before migration. Existing databases apply pending migrations at application startup.

## Account security

- Username comparisons use a single ASCII case-normalization strategy; the normalized value has a unique database index. Usernames are 5–20 letters, digits, periods, underscores, or hyphens.
- Login, registration, refresh, MFA, and recovery routes are limited to 30 requests per IP per minute. Five incorrect passwords lock the account until recovery; confirming an unconfirmed email or completing a password reset clears the lockout. Passwords use ASP.NET Core Identity hashing and require at least 8 characters, uppercase, lowercase, and a digit.
- Registration requires a unique email and confirmation. `POST /auth/confirm-email` confirms it; `POST /auth/email/resend-confirmation` can resend instructions. `POST /auth/password/forgot` always returns the same response to avoid revealing whether an address is registered; `POST /auth/password/reset` consumes the emailed, single-use Identity token and clears lockout. `PUT /auth/me/email` changes the email and requires confirmation again.
- `PUT /auth/me/username` and `PUT /auth/me/password` update the signed-in account. Password changes and account security changes rotate the security stamp and revoke existing sessions.
- JWT validation checks the current user security stamp, lockout state, role version, and persisted session on each authenticated request. A role change invalidates older access tokens immediately; logging out or resetting a password also invalidates that session's access token rather than waiting for its normal expiry.
- Authenticator-app MFA is supported at `POST /auth/me/mfa/setup`, `/auth/me/mfa/enable`, and `/auth/me/mfa/disable`; MFA login completes at `POST /auth/login/mfa`. Reauthenticate after starting setup if the setup call invalidates the current session. Recovery codes are returned only once and should be stored securely; MFA challenge tokens are short-lived and single-use.
- Optional OpenID Connect external sign-in can be enabled by setting `ExternalAuth:Authority`, `ExternalAuth:ClientId`, and `ExternalAuth:ClientSecret` (environment names use double underscores). Configure the provider's redirect URI as `https://<api-host>/signin-oidc`. Start sign-in at `GET /auth/external/login`; link an identity while authenticated via `GET /auth/external/link`. Accounts are linked by provider subject, not by matching email; new external accounts require a provider-verified email.

Production startup requires `DataProtection:KeysPath` (`DataProtection__KeysPath`) pointing to a persistent key ring shared by all API instances. Identity email-confirmation/reset tokens and OIDC state depend on these keys. Store the directory on protected persistent storage and encrypt the key ring at rest with the hosting platform's key-management service. Development uses the local ephemeral key ring.

## API documentation

Run the API in the Development environment and open `https://localhost:7045/swagger` or `http://localhost:5019/swagger`. The OpenAPI document is available at `/swagger/v1/swagger.json`. Swagger UI and its document are enabled only in Development. Use the **Authorize** control to supply a bearer token; protected operations are marked as requiring one.

## Tests

Run API integration tests from the workspace root:

```powershell
dotnet test GameStore.Api.Tests/GameStore.Api.Tests.csproj
```

Integration tests cover token rotation and expiry, concurrent refresh, email confirmation/recovery, account lockout, role/password revocation, MFA recovery codes, rate limiting, and session isolation.
