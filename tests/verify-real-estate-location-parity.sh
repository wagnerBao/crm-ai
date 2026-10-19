#!/usr/bin/env bash
set -euo pipefail
analysis_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
backend_root="${1:-"${analysis_root}/../BACKEND_CRM_SAAS"}"
for policy in RealEstateLocationPolicy.cs RealEstatePropertyPolicy.cs; do
  cmp "${analysis_root}/src/CrmAi.Domain/${policy}" "${backend_root}/src/CrmRestApi.Domain/${policy}"
  shasum -a 256 "${analysis_root}/src/CrmAi.Domain/${policy}"
done
