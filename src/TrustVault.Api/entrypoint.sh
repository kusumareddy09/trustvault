#!/bin/sh
set -eu

mkdir -p /data/PrivateDocuments
chown -R app:app /data

exec gosu app dotnet /app/TrustVault.Api.dll \
    --urls "http://0.0.0.0:${PORT:-8080}"
