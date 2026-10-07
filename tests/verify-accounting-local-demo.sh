#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly EXAMPLE="$ROOT/examples/accounting-local-demo"
readonly BOOTSTRAP="$EXAMPLE/bootstrap-local-demo.sh"
readonly DEMO="$EXAMPLE/demo.sh"
readonly STREAMING_CLIENT="$ROOT/tests/a2a-streaming-client.sh"

bash -n "$BOOTSTRAP"
bash -n "$DEMO"
test -x "$DEMO"
"$DEMO" --preflight >/dev/null

for expected in \
	'AZURE_OPENAI_ENDPOINT' \
	'AZURE_OPENAI_MODEL' \
	'POSTGRES_PASSWORD="sentinel-demo"' \
	'-p 127.0.0.1:5432:5432' \
	'PostgreSQL port $POSTGRES_PORT is already in use' \
	'chmod 600' \
	'docker rm -f' \
	'wal_level=logical' \
	'CREATE PUBLICATION drasi_accounting_publication' \
	'pg_create_logical_replication_slot' \
	'REPLICA IDENTITY FULL' \
	'postgres.connection-string' \
	'postgres.password'
do
	grep -F -- "$expected" "$BOOTSTRAP" >/dev/null
done

for obsolete in \
	'openssl' \
	' rand ' \
	'sentinel_cdc' \
	'CDC_' \
	'container_port' \
	'local-demo.env' \
	'postgres.cdc'
do
	if grep -F -- "$obsolete" "$BOOTSTRAP" >/dev/null; then
		echo "The bootstrap must not contain obsolete dynamic PostgreSQL setup: $obsolete" >&2
		exit 1
	fi
done

for expected in \
	'bootstrap-local-demo.sh' \
	'--preflight' \
	'sentinel.yaml' \
	'/healthz' \
	'A2A_INSPECTOR_PORT=8081' \
	'Connect A2A Inspector to:' \
	'Agent Card URL:' \
	'Agent endpoint:' \
	'trap cleanup EXIT' \
	'WHERE state = '\''Active'\'''
do
	grep -F -- "$expected" "$DEMO" >/dev/null
done

for expected in \
	'wait_for_active_subscription' \
	'wait_for_diffs' \
	'assert_diff_count' \
	'request_current_results' \
	'current_query_results' \
	'CURRENT_BEFORE' \
	'CURRENT_AFTER' \
	'"data":{"type":"native_query_diff"' \
	'"queryId":"invoice-paid"' \
	'"reactionId":"notify-account-owner"' \
	'assert_diff_type_count Update 3' \
	'assert_diff_type_count Delete 1' \
	'assert_diff_type_count Add 1' \
	'UPDATE invoices SET paid = false' \
	'DELETE FROM invoices' \
	'INSERT INTO invoices'
do
	grep -F -- "$expected" "$STREAMING_CLIENT" >/dev/null
done

if grep -F -- 'python3 -m json.tool' "$DEMO"; then
	echo "The user demo must not depend on JSON pretty-printing." >&2
	exit 1
fi
if grep -F -- '/demo/accounting/change' "$DEMO"; then
	echo "The user demo must not use the removed demo endpoint." >&2
	exit 1
fi
if grep -Eiq 'curl.*(/a2a/|--data|--request|--config)' "$DEMO"; then
	echo "The user demo must not contain an A2A curl client." >&2
	exit 1
fi
if grep -F -- '"jsonrpc"' "$DEMO"; then
	echo "The user demo must not contain A2A JSON-RPC." >&2
	exit 1
fi
if grep -F -- 'embedded source' "$BOOTSTRAP"; then
	echo "The bootstrap must not use an embedded source." >&2
	exit 1
fi
if grep -Eiq '(^|[^[:alnum:]])curl([^[:alnum:]]|$)' "$BOOTSTRAP"; then
	echo "The bootstrap must not contain a protocol client." >&2
	exit 1
fi

for expected in \
	'kind: postgres' \
	'slotName: drasi_accounting_slot' \
	'publicationName: drasi_accounting_publication' \
	'keyColumns: [invoice_id]' \
	'MATCH (invoice:invoices)' \
	'invoice.paid AS paid'
do
	grep -F -- "$expected" "$EXAMPLE/sentinel.yaml" >/dev/null
done

for expected in \
	'port: 5432' \
	'user: sentinel' \
	'path: secrets/postgres.password'
do
	grep -F -- "$expected" "$EXAMPLE/sentinel.yaml" >/dev/null
done

for expected in \
	'0.2.7' \
	'ac123ecb01d4c86d290db391e30436a1c34123705a9b175a866ac40c0c7285d8' \
	'0.2.10' \
	'496c315e8c6611712fcfd4398bfbb8771d8765891a36932bbdf536333121b848' \
	'sdk_version = "0.10.0"' \
	'core_version = "0.5.7"' \
	'lib_version = "0.8.9"'
do
	grep -F -- "$expected" "$EXAMPLE/plugins.lock" >/dev/null
done

python3 -c 'import sys; compile(open(sys.argv[1], encoding="utf-8").read(), sys.argv[1], "exec")' \
	"$EXAMPLE/capture_receiver.py"

echo "accounting local-demo static checks passed"
