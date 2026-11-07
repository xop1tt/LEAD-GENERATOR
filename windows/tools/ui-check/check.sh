#!/bin/bash
# Проверка C#-кода интерфейса на Linux в контейнере с .NET SDK (нужны docker и доступ к NuGet).
set -e
cd "$(dirname "$0")/../.."
mkdir -p tools/ui-check/obj
python3 tools/ui-check/gen_stubs.py src/Customers.App tools/ui-check/obj/stubs.g.cs
docker run --rm --network host \
  -e HTTPS_PROXY -e HTTP_PROXY -e NO_PROXY -e SSL_CERT_FILE=/etc/ssl/certs/ca-bundle.crt \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "${CA_BUNDLE:-/etc/ssl/certs/ca-certificates.crt}:/etc/ssl/certs/ca-bundle.crt:ro" \
  -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet msbuild tools/ui-check/ui-check.csproj -restore -t:Compile -p:Configuration=Release -nologo -v:m
