# Game Store API

## Local secrets and configuration

JWT signing keys and the initial admin password are not stored in `appsettings.json`. For local development, store them with .NET User Secrets from the workspace root:

```powershell
dotnet user-secrets set "Jwt:Key" ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))) --project GameStore.Api
dotnet user-secrets set "BootstrapAdmin:Password" "Replace-with-a-strong-local-password" --project GameStore.Api
```

Use the bootstrap password only when creating the first admin account. Existing databases keep their current admin credentials. User Secrets are for development only and are stored outside the repository. In deployed environments, supply `Jwt__Key` and, when initializing an empty user database, `BootstrapAdmin__Password` through the platform's secret manager/environment configuration. Never commit real secrets.

The application validates that the JWT signing key is at least 32 bytes. `Jwt:Issuer`, `Jwt:Audience`, and the local SQLite connection string are non-secret settings and remain in `GameStore.Api/appsettings.json`.

## API documentation

Run the API in the Development environment and open `https://localhost:7045/swagger` or `http://localhost:5019/swagger`. The OpenAPI document is available at `/swagger/v1/swagger.json`. Swagger UI and its document are enabled only in Development. Use the **Authorize** control to supply a bearer token; protected operations are marked as requiring one.

## Tests

Run API integration tests from the workspace root:

```powershell
dotnet test GameStore.Api.Tests/GameStore.Api.Tests.csproj
```
