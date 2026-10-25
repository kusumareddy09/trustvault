# Hackathon deployment

Deploy one Railway service from the GitHub repository. The API Docker image builds the Angular app and serves it from the same origin, so production needs no separate frontend URL or CORS allowlist. The frontend continues to use `http://localhost:5099` when opened on localhost.

## Deploy

1. Push the deployment changes to GitHub after reviewing them.
2. In Railway, create a project from the `kusumareddy09/trustvault` GitHub repository. Railway uses `railway.json` and `src/TrustVault.Api/Dockerfile`.
3. Add one persistent volume mounted at `/data`.
4. Set these service variables in Railway:

   - `ASPNETCORE_ENVIRONMENT=Production`
   - `ConnectionStrings__TrustVault=Data Source=/data/trustvault.db`
   - `Storage__DocumentsPath=/data/PrivateDocuments`
   - `Jwt__Key`: unique, randomly generated signing key with at least 32 bytes of entropy
   - `Email__SmtpHost`, `Email__SmtpPort`, `Email__Username`, `Email__Password`, `Email__From`: SMTP settings for password-reset email
   - `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`: trust Railway's forwarded HTTPS scheme so redirects do not loop
   - `Jwt__Issuer` and `Jwt__Audience` are optional; defaults are `TrustVault` and `TrustVault.Client`.

   Railway supplies `PORT`; the container binds to it. Do not set `Jwt__Key` to the development fallback or commit any production credentials.
5. Generate a public domain in Railway and open it. The `/health` endpoint is used for deployment health checks.

SQLite and uploaded files share the `/data` volume and persist across restarts. The container fixes volume ownership before running the API as its unprivileged app user. Back up the volume. This setup is intended for one API instance; horizontal scaling requires a shared database and object storage.

Production account registration is currently disabled, and the development demo accounts are not seeded in Production. Before using the deployed app, provision an initial owner account through a reviewed bootstrap process; do not enable public registration or reuse demo credentials as a shortcut.

## Render

Deploy this as one Docker **Web Service** so the API and Angular frontend share an origin:

1. Create a Web Service from the repository, leave the root directory at the repository root, and set the Dockerfile path to `src/TrustVault.Api/Dockerfile`.
2. Add a persistent disk mounted at `/data`.
3. Set `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `ConnectionStrings__TrustVault=Data Source=/data/trustvault.db`, `Storage__DocumentsPath=/data/PrivateDocuments`, and a unique `Jwt__Key` with at least 32 bytes of entropy. Configure SMTP variables only if password-reset email is needed.
4. Set `Bootstrap__OwnerEmail` to the account email you want and `Bootstrap__OwnerPassword` to a unique password of 12–128 characters using Render's secret environment-variable settings. On startup, the API creates this Owner only if that email is not already in the database; it never resets an existing account's password. The API remains available when both bootstrap variables are unset, but no account is created. This does not enable public registration.
5. Wait for the service to become healthy, sign in with that email and password, then remove both bootstrap variables from Render and redeploy. Keep the disk attached; it stores accounts and private uploads. The service uses Render's `PORT` setting.

Bootstrap settings do not change or reset any existing password. If sign-in still returns 401, verify the account email and password against that persistent database; do not delete the database to retry.
