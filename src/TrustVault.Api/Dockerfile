FROM node:22-alpine AS frontend-build
WORKDIR /web

COPY src/trustvault-web/package.json src/trustvault-web/package-lock.json ./
RUN npm ci

COPY src/trustvault-web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

COPY src/TrustVault.Api/TrustVault.Api.csproj src/TrustVault.Api/
RUN dotnet restore src/TrustVault.Api/TrustVault.Api.csproj

COPY src/TrustVault.Api/ src/TrustVault.Api/
RUN dotnet publish src/TrustVault.Api/TrustVault.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=api-build /app/publish ./
COPY --from=frontend-build /web/dist/trustvault-web/browser/ ./wwwroot/
COPY src/TrustVault.Api/entrypoint.sh ./
RUN apt-get update \
    && apt-get install -y --no-install-recommends gosu \
    && rm -rf /var/lib/apt/lists/* \
    && chmod +x ./entrypoint.sh

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["./entrypoint.sh"]
