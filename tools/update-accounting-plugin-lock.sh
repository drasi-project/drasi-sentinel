#!/usr/bin/env bash
set -euo pipefail

readonly ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly EXAMPLE_DIR="$ROOT_DIR/examples/accounting-local-demo"
readonly SOURCE_REFERENCE="ghcr.io/drasi-project/source/postgres:0.2.7"
readonly BOOTSTRAP_REFERENCE="ghcr.io/drasi-project/bootstrap/postgres:0.2.10"
readonly LOCK_FILE="$EXAMPLE_DIR/plugins.lock"

dotnet run --project "$ROOT_DIR/tools/PluginLock/PluginLock.csproj" -- \
	"$EXAMPLE_DIR" "$SOURCE_REFERENCE" "$BOOTSTRAP_REFERENCE"

for expected in \
	'version = "0.2.7"' \
	'digest = "sha256:ac123ecb01d4c86d290db391e30436a1c34123705a9b175a866ac40c0c7285d8"' \
	'version = "0.2.10"' \
	'digest = "sha256:496c315e8c6611712fcfd4398bfbb8771d8765891a36932bbdf536333121b848"' \
	'sdk_version = "0.10.0"' \
	'core_version = "0.5.7"' \
	'lib_version = "0.8.9"'
do
	grep -F -- "$expected" "$LOCK_FILE" >/dev/null ||
		{ printf 'error: generated plugin lock is not compatible with Drasi .NET 0.1.0.\n' >&2; exit 1; }
done
