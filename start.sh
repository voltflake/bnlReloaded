#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
dotnet build -c Debug BNLReloadedServer/BNLReloadedServer.csproj
exec ./BNLReloadedServer/bin/Debug/net10.0/BNLReloadedServer
