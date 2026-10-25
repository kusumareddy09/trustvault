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
