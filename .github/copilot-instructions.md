# Copilot instructions

## Project overview

TrustVault has an ASP.NET Core API and a separate Angular web client:

- `src\TrustVault.Api` is a .NET 10 minimal API. Most endpoints and startup configuration are in `Program.cs`; password reset endpoints are mapped from `PasswordResetEndpoints.cs`. `Data\AppDbContext.cs` defines the EF Core entities and relationships.
- `src\trustvault-web` is an Angular 21 standalone application. The current UI is centered in `src\app\app.ts`, `app.html`, and `app.css`; `app.routes.ts` is currently empty. The client calls the API at `http://localhost:5099` using Angular `HttpClient`.
- The API stores relational data in SQLite and uploaded file content under `src\TrustVault.Api\PrivateDocuments`, using a storage key rather than a public web path.

Authentication is JWT-based. Document access is determined by ownership and active `DocumentGrant` records (`Read` or `Edit`); revocation and role checks are enforced by API endpoints, not just by the UI. Preserve these checks when changing document operations. Related actions are recorded as `AuditEvent` rows, with UTC timestamps and request IDs where available.

The API initializes SQLite with `EnsureCreated` and includes startup schema-upgrade logic in `Program.cs`; there is no migrations workflow in the project. Coordinate entity changes with that initialization path.

## Build, test, and formatting

Run API commands from the repository root:

```powershell
dotnet build src\TrustVault.Api\TrustVault.Api.csproj
dotnet test tests\tests\TrustVault.Api.Tests\TrustVault.Api.Tests.csproj
dotnet test tests\tests\TrustVault.Api.Tests\TrustVault.Api.Tests.csproj --filter "FullyQualifiedName~TrustVault.Api.Tests.HealthEndpointTests.Health_ReturnsHealthyResponse"
```

The test project above contains the API integration test. `tests\TrustVault.Api.Tests` is a separate stub project.

Run web commands from `src\trustvault-web` (install dependencies first with `npm ci` when needed):

```powershell
npm run build
npm test -- --watch=false
npm test -- --include src/app/app.spec.ts --filter "App should create the app" --watch=false
npx prettier --check "src/**/*.{ts,html,css,json}"
```

There is no dedicated lint script in the web package; Prettier is configured for formatting checks.

## Code conventions and integration points

- Keep frontend TypeScript consistent with `src\trustvault-web\.editorconfig`: two-space indentation and single quotes.
- The Angular client currently keeps API request logic and view state in the root standalone `App` component. API response shapes are declared as local TypeScript types there; keep them aligned with endpoint response fields.
- Backend endpoints use minimal-API route handlers in `Program.cs`, with request records near the end of that file. Password reset routes are the exception and belong in `PasswordResetEndpoints.cs`.
- For authenticated document endpoints, derive the user identity from the JWT claims and validate ownership or an active grant in the API. Keep file operations confined to the private storage directory.
- Keep audit records consistent with the operation outcome; document deletion and permission changes also affect grants or audit-event references.
