#!/usr/bin/env bash
# Write the normalized admin OpenAPI contract to contracts/openapi/admin-v1.json.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

UPDATE_ADMIN_OPENAPI=1 dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter "FullyQualifiedName~AdminOpenApiIntegrationTests.CommittedContract_MatchesNormalizedRuntimeDocument" \
  --nologo
