#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
dotnet run --project BNLReloadedServer/BNLReloadedServer.csproj
