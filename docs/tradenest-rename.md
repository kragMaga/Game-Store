# TradeNest rename

The API project is TradeNest.Api; tests are TradeNest.Api.Tests; the maintenance
project is tools/TradeNest.Maintenance. The EF context is TradeNestContext and the
integration host is TradeNestWebApplicationFactory. Namespaces, project references,
CI commands, frontend titles/branding, package name, account emails and newly
registered authenticator labels use TradeNest. Migrations keep their original IDs:
a namespace/type rename must not replay schema changes against existing data.

## Compatibility retained intentionally

A product rename does not require rotating encryption or authentication identities.
LegacyCompatibility centralizes values used by existing encrypted payloads, cookies,
Identity MFA challenge rows and local database access. Comments explain why they
remain. Do not mechanically rename these values without a separate migration.

- The SQLite file remains GameStore.db. Moving/renaming a live SQLite database and
  its WAL sidecars is a separate maintenance task. The configured default still
  opens the original file, now within TradeNest.Api; no new empty database is created
  just because the product name changed.
- ConnectionStrings:TradeNest is the new configuration key. An explicitly supplied
  ConnectionStrings:GameStore is still accepted and takes precedence over the new
  default, preserving existing user-secrets/environment deployments. Once migrated,
  remove that legacy override to use the new key. Supply a single intended value.
- The csproj UserSecretsId remains unchanged so existing local JWT, SMTP and OIDC
  settings remain available without copying secrets into source control.
- Default JWT issuer/audience remain unchanged. They are protocol identifiers, not
  UI labels; changing their configuration would invalidate outstanding access tokens.
- Refresh/antiforgery cookie names, MFA login-provider names and Data Protection
  purpose strings remain unchanged, preserving existing security records.
- Production keeps its existing Data Protection application name and key-ring path.
  Development without an explicit key path preserves the previous API content-root
  discriminator when resolving encrypted data. Keep the same host account/key ring.

No live database migration was executed for this rename. Existing database files
were kept; schema/stock/customer data and migration IDs were not rewritten.
The repository's enclosing workspace folder has not been renamed.

## Commands

```powershell
dotnet test TradeNest.Api.Tests/TradeNest.Api.Tests.csproj
dotnet run --project TradeNest.Api --launch-profile http
dotnet run --project tools/TradeNest.Maintenance -- verify C:/PrivateBackups/snapshot.db
```

The frontend remains in frontend/. Start it with npm run dev from that directory.
Its README uses the new API project name. Reopen the renamed projects in your IDE
if old tabs or cached launch configurations still refer to the former paths. Update
external absolute paths (deployment scripts, database overrides, or IDE tasks) to
point to the renamed folders; those settings are outside this repository.

Windows locked the maintenance project's old generated cache directory during the
rename. Its source was moved to the new project; the old folder contains only
ignored package/build caches and is no longer referenced by the application or CI.
