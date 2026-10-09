#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SECRETS_DIR="$EXAMPLE_DIR/secrets"
readonly ENV_FILE="$EXAMPLE_DIR/.env"
readonly CONTAINER_NAME="drasi-sentinel-accounting-local-demo"
readonly CONTAINER_LABEL_KEY="com.drasi-sentinel.example"
readonly CONTAINER_LABEL_VALUE="accounting-local-demo"
readonly POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:16}"
readonly POSTGRES_PORT=5432
readonly POSTGRES_PASSWORD="sentinel-demo"
readonly SECRETS_JSON="$SECRETS_DIR/secrets.json"

fail() {
	printf 'error: %s\n' "$*" >&2
	exit 1
}

require_docker() {
	command -v docker >/dev/null 2>&1 || fail "Docker is required."
	docker info >/dev/null 2>&1 || fail "Docker is not running or is not accessible."
}

load_agent_environment() {
	[[ -f "$ENV_FILE" ]] || fail "Create $ENV_FILE from .env.example first."

	local line key value
	local endpoint=""
	local model=""
	while IFS= read -r line || [[ -n "$line" ]]; do
		line="${line%$'\r'}"
		[[ -z "${line//[[:space:]]/}" || "$line" == \#* ]] && continue
		key="${line%%=*}"
		value="${line#*=}"
		[[ "$key" != "$line" && -n "$value" ]] || fail "$ENV_FILE must use NAME=value entries."
		case "$key" in
			AZURE_OPENAI_ENDPOINT) endpoint="$value" ;;
			AZURE_OPENAI_MODEL) model="$value" ;;
			*) fail "$ENV_FILE may contain only AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_MODEL." ;;
		esac
	done < "$ENV_FILE"

	[[ -n "$endpoint" ]] || fail "$ENV_FILE must set AZURE_OPENAI_ENDPOINT."
	[[ -n "$model" ]] || fail "$ENV_FILE must set AZURE_OPENAI_MODEL."
}

container_exists() {
	docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1
}

require_demo_container() {
	local label
	label="$(docker container inspect --format "{{ index .Config.Labels \"$CONTAINER_LABEL_KEY\" }}" "$CONTAINER_NAME")"
	[[ "$label" == "$CONTAINER_LABEL_VALUE" ]] ||
		fail "Container $CONTAINER_NAME exists but is not owned by this demo."
}

container_is_running() {
	[[ "$(docker container inspect --format '{{.State.Running}}' "$CONTAINER_NAME")" == "true" ]]
}

require_expected_container_configuration() {
	local port password
	port="$(docker container inspect --format '{{with index .NetworkSettings.Ports "5432/tcp"}}{{(index . 0).HostIp}}:{{(index . 0).HostPort}}{{end}}' "$CONTAINER_NAME")"
	password="$(docker container inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$CONTAINER_NAME" |
		grep -Fx 'POSTGRES_PASSWORD=sentinel-demo' || true)"
	[[ "$port" == "127.0.0.1:5432" && "$password" == "POSTGRES_PASSWORD=sentinel-demo" ]] ||
		fail "Container $CONTAINER_NAME does not use this demo's fixed PostgreSQL settings. Run '$0 stop' and start again."
}

require_available_postgres_port() {
	if lsof -nP -iTCP:"$POSTGRES_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
		fail "PostgreSQL port $POSTGRES_PORT is already in use on 127.0.0.1. Stop the process using it, or run '$0 stop' if it belongs to this demo."
	fi
}

wait_for_postgres() {
	local attempt
	for attempt in {1..30}; do
		if docker exec "$CONTAINER_NAME" pg_isready -U sentinel -d sentinel >/dev/null 2>&1; then
			return
		fi
		sleep 1
	done
	fail "PostgreSQL did not become ready. Inspect with: docker logs $CONTAINER_NAME"
}

write_postgres_files() {
	(
		umask 077
		SENTINEL_CONNECTION_STRING="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=sentinel;Username=sentinel;Password=$POSTGRES_PASSWORD;SSL Mode=Disable" \
			SENTINEL_POSTGRES_PASSWORD="$POSTGRES_PASSWORD" \
			python3 - "$SECRETS_JSON" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({
        "sentinel-connection-string": os.environ["SENTINEL_CONNECTION_STRING"],
        "postgres-password": os.environ["SENTINEL_POSTGRES_PASSWORD"],
    }, handle)
    handle.write("\n")
PY
	)
	chmod 600 "$SECRETS_JSON"
	rm -f -- "$SECRETS_DIR/postgres.connection-string" "$SECRETS_DIR/postgres.password"
}

initialize_accounting_database() {
	docker exec -i "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel <<'EOF' >/dev/null
CREATE TABLE IF NOT EXISTS invoices (
	invoice_id text PRIMARY KEY,
	account_id text NOT NULL,
	amount integer NOT NULL,
	paid boolean NOT NULL
);
ALTER TABLE invoices REPLICA IDENTITY FULL;
INSERT INTO invoices (invoice_id, account_id, amount, paid)
VALUES ('invoice-demo-001', 'account-200', 12500, false)
ON CONFLICT (invoice_id) DO UPDATE
SET account_id = EXCLUDED.account_id,
    amount = EXCLUDED.amount,
    paid = false;
EOF

	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc \
		"SELECT 1 FROM pg_publication WHERE pubname = 'drasi_accounting_publication'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
			-c 'CREATE PUBLICATION drasi_accounting_publication FOR TABLE invoices' >/dev/null
	fi
	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc \
		"SELECT 1 FROM pg_replication_slots WHERE slot_name = 'drasi_accounting_slot'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
			-c "SELECT pg_create_logical_replication_slot('drasi_accounting_slot', 'pgoutput')" >/dev/null
	fi
}

stop_demo() {
	require_docker
	if container_exists; then
		require_demo_container
		docker rm -f "$CONTAINER_NAME" >/dev/null
	fi
	rm -f -- "$SECRETS_JSON" "$SECRETS_DIR/postgres.connection-string" "$SECRETS_DIR/postgres.password"
	printf 'Stopped the local PostgreSQL container and removed this demo’s generated files.\n'
}

start_demo() {
	require_docker
	load_agent_environment
	mkdir -p "$SECRETS_DIR"

	if container_exists; then
		require_demo_container
		require_expected_container_configuration
		if ! container_is_running; then
			require_available_postgres_port
			docker start "$CONTAINER_NAME" >/dev/null
		fi
	else
		require_available_postgres_port
		docker run -d --name "$CONTAINER_NAME" \
			--label "$CONTAINER_LABEL_KEY=$CONTAINER_LABEL_VALUE" \
			-e POSTGRES_USER=sentinel \
			-e POSTGRES_PASSWORD="$POSTGRES_PASSWORD" \
			-e POSTGRES_DB=sentinel \
			-p 127.0.0.1:5432:5432 \
			"$POSTGRES_IMAGE" -c wal_level=logical -c max_replication_slots=10 -c max_wal_senders=10 >/dev/null
	fi

	wait_for_postgres
	[[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc 'SHOW wal_level')" == "logical" ]] ||
		fail "The existing PostgreSQL container was not started with logical replication. Run '$0 stop' before bootstrapping again."
	initialize_accounting_database
	write_postgres_files

	cat <<EOF
Local accounting resources are ready.

PostgreSQL listens only on 127.0.0.1:$POSTGRES_PORT. The owner-only
secrets/secrets.json file holds the connection string and source password.

The sentinel PostgreSQL user stores Sentinel's task and subscription state and
is also used for PostgreSQL CDC. It runs with logical replication, an invoices
primary key, the drasi_accounting_publication publication, and the
drasi_accounting_slot slot.

From $EXAMPLE_DIR, run ./demo.sh. It starts the receiver and host, prints the
A2A Inspector connection details, and waits for an active subscription.

To remove only this demo's container and generated files:
  ./bootstrap-local-demo.sh stop
EOF
}

case "${1:-start}" in
	start) start_demo ;;
	stop) stop_demo ;;
	*) fail "Usage: $0 [start|stop]" ;;
esac
