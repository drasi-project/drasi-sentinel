#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly DEMO="$ROOT/examples/accounting-local-demo/demo.sh"

bash -n "$DEMO"
"$DEMO" --preflight >/dev/null
printf 'Accounting streaming demo static probe passed.\n'
