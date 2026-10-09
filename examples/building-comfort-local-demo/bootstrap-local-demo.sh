#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SECRETS_DIR="$EXAMPLE_DIR/secrets"
readonly ENV_FILE="$EXAMPLE_DIR/.env"
readonly CONTAINER_NAME="drasi-sentinel-building-comfort"
readonly CONTAINER_LABEL_KEY="com.drasi-sentinel.example"
readonly CONTAINER_LABEL_VALUE="building-comfort-local-demo"
readonly POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:16}"
readonly POSTGRES_PORT=5732
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
	if [[ -f "$ENV_FILE" ]]; then
		local line key value
		while IFS= read -r line || [[ -n "$line" ]]; do
			line="${line%$'\r'}"
			[[ -z "${line//[[:space:]]/}" || "$line" == \#* ]] && continue
			key="${line%%=*}"
			value="${line#*=}"
			[[ "$key" != "$line" && -n "$value" ]] || fail "$ENV_FILE must use NAME=value entries."
			case "$key" in
				AZURE_OPENAI_ENDPOINT) export AZURE_OPENAI_ENDPOINT="$value" ;;
				AZURE_OPENAI_MODEL) export AZURE_OPENAI_MODEL="$value" ;;
				*) fail "$ENV_FILE may contain only AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_MODEL." ;;
			esac
		done < "$ENV_FILE"
	fi
	[[ -n "${AZURE_OPENAI_ENDPOINT:-}" && -n "${AZURE_OPENAI_MODEL:-}" ]] ||
		fail "Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_MODEL in $ENV_FILE or the environment."
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
	password="$(docker exec "$CONTAINER_NAME" printenv POSTGRES_PASSWORD 2>/dev/null || true)"
	[[ "$port" == "127.0.0.1:$POSTGRES_PORT" && "$password" == "$POSTGRES_PASSWORD" ]] ||
		fail "Container $CONTAINER_NAME does not use this demo's fixed PostgreSQL settings. Run '$0 stop' and start again."
}

require_available_postgres_port() {
	if lsof -nP -iTCP:"$POSTGRES_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
		fail "PostgreSQL port $POSTGRES_PORT is already in use on 127.0.0.1. Stop the process using it, or run '$0 stop' if it belongs to this demo."
	fi
}

wait_for_postgres() {
	local attempt wal
	for attempt in {1..60}; do
		wal="$(docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc 'SHOW wal_level' 2>/dev/null | tr -d '[:space:]' || true)"
		if [[ "$wal" == "logical" ]]; then
			sleep 1
			wal="$(docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc 'SHOW wal_level' 2>/dev/null | tr -d '[:space:]' || true)"
			[[ "$wal" == "logical" ]] && return
		fi
		sleep 1
	done
	fail "PostgreSQL did not become ready with wal_level=logical. Inspect with: docker logs $CONTAINER_NAME"
}

write_secret_files() {
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
}

psql_db() {
	local database="$1"
	docker exec -i "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d "$database"
}

reset_replication_slots() {
	if pgrep -f 'building-comfort-local-demo/sentinel.yaml' >/dev/null 2>&1; then
		fail "A Sentinel host for this demo is already running. Stop it before starting again."
	fi
	docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -c \
		"SELECT pg_terminate_backend(active_pid) FROM pg_replication_slots WHERE active_pid IS NOT NULL" >/dev/null || true
	sleep 1
	docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel -c \
		"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots" >/dev/null
}

ensure_database() {
	local name="$1"
	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc \
		"SELECT 1 FROM pg_database WHERE datname = '$name'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
			-c "CREATE DATABASE $name" >/dev/null
	fi
}

reset_sentinel_state() {
	psql_db sentinel <<'EOF' >/dev/null
DO $$
BEGIN
	IF to_regclass('public.sentinel_task_events') IS NOT NULL THEN
		TRUNCATE sentinel_task_events, sentinel_subscriptions, sentinel_conversation_tasks, sentinel_authored_queries;
	END IF;
END $$;
EOF
}

initialize_building() {
	psql_db building_comfort <<'EOF' >/dev/null
CREATE TABLE IF NOT EXISTS "Building" (
	id varchar(50) PRIMARY KEY,
	name varchar(100) NOT NULL
);
CREATE TABLE IF NOT EXISTS "Floor" (
	id varchar(50) PRIMARY KEY,
	name varchar(100) NOT NULL,
	building_id varchar(50) NOT NULL REFERENCES "Building"(id)
);
CREATE TABLE IF NOT EXISTS "Room" (
	id varchar(50) PRIMARY KEY,
	name varchar(100) NOT NULL,
	temperature integer NOT NULL,
	humidity integer NOT NULL,
	co2 integer NOT NULL,
	floor_id varchar(50) NOT NULL REFERENCES "Floor"(id)
);
ALTER TABLE "Building" REPLICA IDENTITY FULL;
ALTER TABLE "Floor" REPLICA IDENTITY FULL;
ALTER TABLE "Room" REPLICA IDENTITY FULL;
TRUNCATE "Room", "Floor", "Building";
INSERT INTO "Building" (id, name) VALUES ('building_01', 'Building 01');
INSERT INTO "Floor" (id, name, building_id) VALUES
	('floor_01_01', 'Floor 01', 'building_01'),
	('floor_01_02', 'Floor 02', 'building_01'),
	('floor_01_03', 'Floor 03', 'building_01');
INSERT INTO "Room" (id, name, temperature, humidity, co2, floor_id) VALUES
	('room_01_01_01', 'Room 01', 70, 40, 10, 'floor_01_01'),
	('room_01_01_02', 'Room 02', 70, 40, 10, 'floor_01_01'),
	('room_01_01_03', 'Room 03', 70, 40, 10, 'floor_01_01'),
	('room_01_02_01', 'Room 01', 70, 40, 10, 'floor_01_02'),
	('room_01_02_02', 'Room 02', 70, 40, 10, 'floor_01_02'),
	('room_01_02_03', 'Room 03', 70, 40, 10, 'floor_01_02'),
	('room_01_03_01', 'Room 01', 70, 40, 10, 'floor_01_03'),
	('room_01_03_02', 'Room 02', 70, 40, 10, 'floor_01_03'),
	('room_01_03_03', 'Room 03', 70, 40, 10, 'floor_01_03');
EOF
	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d building_comfort -tAc \
		"SELECT 1 FROM pg_publication WHERE pubname = 'drasi_building_comfort_pub'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d building_comfort \
			-c 'CREATE PUBLICATION drasi_building_comfort_pub FOR TABLE "Building", "Floor", "Room"' >/dev/null
	fi
	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d building_comfort -tAc \
		"SELECT 1 FROM pg_replication_slots WHERE slot_name = 'drasi_building_comfort_slot'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d building_comfort \
			-c "SELECT pg_create_logical_replication_slot('drasi_building_comfort_slot', 'pgoutput')" >/dev/null
	fi
}

stop_demo() {
	require_docker
	if container_exists; then
		require_demo_container
		docker rm -f "$CONTAINER_NAME" >/dev/null
	fi
	rm -f -- "$SECRETS_JSON"
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
			-p "127.0.0.1:$POSTGRES_PORT:5432" \
			"$POSTGRES_IMAGE" -c wal_level=logical -c max_replication_slots=10 -c max_wal_senders=10 >/dev/null
	fi

	wait_for_postgres
	ensure_database building_comfort
	reset_replication_slots
	reset_sentinel_state
	initialize_building
	write_secret_files

	cat <<EOF
Local building-comfort resources are ready.

PostgreSQL listens only on 127.0.0.1:$POSTGRES_PORT. Database building_comfort holds Building, Floor, and Room.
Database sentinel stores Sentinel task state.
Every room starts at temperature 70, humidity 40, and co2 10, which is comfortable.
sentinel.yaml declares the PostgreSQL source and bootstrap plugins.
Sentinel downloads and installs those refs when the host starts.

From $EXAMPLE_DIR, run ./demo.sh.
EOF
}

case "${1:-start}" in
	start) start_demo ;;
	stop) stop_demo ;;
	*) fail "Usage: $0 [start|stop]" ;;
esac
