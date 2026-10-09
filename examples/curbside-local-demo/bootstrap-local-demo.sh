#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SECRETS_DIR="$EXAMPLE_DIR/secrets"
readonly ENV_FILE="$EXAMPLE_DIR/.env"
readonly CONTAINER_NAME="drasi-sentinel-curbside-local-demo"
readonly MYSQL_CONTAINER="drasi-sentinel-curbside-mysql"
readonly CONTAINER_LABEL_KEY="com.drasi-sentinel.example"
readonly CONTAINER_LABEL_VALUE="curbside-local-demo"
readonly POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:16}"
readonly MYSQL_IMAGE="${MYSQL_IMAGE:-mysql:8.0}"
readonly POSTGRES_PORT=5433
readonly MYSQL_PORT=3309
readonly POSTGRES_PASSWORD="sentinel-demo"
readonly MYSQL_PASSWORD="sentinel-demo"
readonly MYSQL_DATABASE="physical"
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
	password="$(docker container inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$CONTAINER_NAME" |
		grep -Fx 'POSTGRES_PASSWORD=sentinel-demo' || true)"
	[[ "$port" == "127.0.0.1:$POSTGRES_PORT" && "$password" == "POSTGRES_PASSWORD=sentinel-demo" ]] ||
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
			SENTINEL_MYSQL_PASSWORD="$MYSQL_PASSWORD" \
			python3 - "$SECRETS_JSON" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({
        "sentinel-connection-string": os.environ["SENTINEL_CONNECTION_STRING"],
        "postgres-password": os.environ["SENTINEL_POSTGRES_PASSWORD"],
        "mysql-password": os.environ["SENTINEL_MYSQL_PASSWORD"],
    }, handle)
    handle.write("\n")
PY
	)
	chmod 600 "$SECRETS_JSON"
	rm -f -- \
		"$SECRETS_DIR/postgres.connection-string" \
		"$SECRETS_DIR/postgres.password" \
		"$SECRETS_DIR/mysql.password"
}

psql_db() {
	local database="$1"
	shift
	docker exec -i "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d "$database" "$@"
}

reset_replication_slots() {
	if pgrep -f 'curbside-local-demo/sentinel.yaml' >/dev/null 2>&1; then
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

initialize_retail() {
	psql_db retail <<'EOF' >/dev/null
CREATE TABLE IF NOT EXISTS orders (
	id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
	customer_name text NOT NULL,
	driver_name text NOT NULL,
	plate text NOT NULL,
	status text NOT NULL
);
ALTER TABLE orders REPLICA IDENTITY FULL;
TRUNCATE orders RESTART IDENTITY;
INSERT INTO orders (customer_name, driver_name, plate, status) VALUES
	('Sophia Carter', 'Elijah Brooks', 'A1234', 'preparing'),
	('Mason Rivera', 'Isabella Reed', 'B5678', 'preparing'),
	('Ava Mitchell', 'Liam Bennett', 'C9876', 'preparing'),
	('Noah Patel', 'Pat Ellis', 'F1000', 'ready');
EOF
	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d retail -tAc \
		"SELECT 1 FROM pg_publication WHERE pubname = 'drasi_retail_publication'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
			-c 'CREATE PUBLICATION drasi_retail_publication FOR TABLE orders' >/dev/null
	fi
	if [[ "$(docker exec "$CONTAINER_NAME" psql -U sentinel -d retail -tAc \
		"SELECT 1 FROM pg_replication_slots WHERE slot_name = 'drasi_retail_slot'")" != "1" ]]; then
		docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
			-c "SELECT pg_create_logical_replication_slot('drasi_retail_slot', 'pgoutput')" >/dev/null
	fi
}

drop_physical_postgres_database() {
	docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -c \
		"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'physical' AND pid <> pg_backend_pid()" >/dev/null || true
	docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
		-c 'DROP DATABASE IF EXISTS physical' >/dev/null
}

mysql_container_exists() {
	docker container inspect "$MYSQL_CONTAINER" >/dev/null 2>&1
}

require_owned_container() {
	local name="$1"
	local label
	label="$(docker container inspect --format "{{ index .Config.Labels \"$CONTAINER_LABEL_KEY\" }}" "$name")"
	[[ "$label" == "$CONTAINER_LABEL_VALUE" ]] ||
		fail "Container $name exists but is not owned by this demo."
}

named_container_is_running() {
	[[ "$(docker container inspect --format '{{.State.Running}}' "$1")" == "true" ]]
}

require_available_mysql_port() {
	if lsof -nP -iTCP:"$MYSQL_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
		fail "MySQL port $MYSQL_PORT is already in use on 127.0.0.1. Stop the process using it, or run '$0 stop' if it belongs to this demo."
	fi
}

require_expected_mysql_configuration() {
	local port password command
	port="$(docker container inspect --format '{{with index .NetworkSettings.Ports "3306/tcp"}}{{(index . 0).HostIp}}:{{(index . 0).HostPort}}{{end}}' "$MYSQL_CONTAINER")"
	password="$(docker container inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$MYSQL_CONTAINER" |
		grep -Fx "MYSQL_PASSWORD=$MYSQL_PASSWORD" || true)"
	command="$(docker container inspect --format '{{json .Config.Cmd}}' "$MYSQL_CONTAINER")"
	[[ "$port" == "127.0.0.1:$MYSQL_PORT" && "$password" == "MYSQL_PASSWORD=$MYSQL_PASSWORD" ]] ||
		fail "Container $MYSQL_CONTAINER does not use this demo's fixed MySQL settings. Run '$0 stop' and start again."
	[[ "$command" == *"--binlog-format=ROW"* && "$command" == *"--gtid-mode=ON"* && "$command" == *"--binlog-row-image=FULL"* ]] ||
		fail "Container $MYSQL_CONTAINER is not running with ROW binlog and GTID. Run '$0 stop' and start again."
}

wait_for_mysql() {
	local attempt mode format
	for attempt in {1..90}; do
		mode="$(docker exec -e MYSQL_PWD="$MYSQL_PASSWORD" "$MYSQL_CONTAINER" \
			mysql -uroot -Nse 'SELECT @@gtid_mode' 2>/dev/null | tr -d '[:space:]' || true)"
		format="$(docker exec -e MYSQL_PWD="$MYSQL_PASSWORD" "$MYSQL_CONTAINER" \
			mysql -uroot -Nse 'SELECT @@binlog_format' 2>/dev/null | tr -d '[:space:]' || true)"
		if [[ "$mode" == "ON" && "$format" == "ROW" ]]; then
			return
		fi
		sleep 2
	done
	fail "MySQL did not become ready with ROW binlog and GTID. Inspect with: docker logs $MYSQL_CONTAINER"
}

grant_mysql_replication() {
	docker exec -e MYSQL_PWD="$MYSQL_PASSWORD" "$MYSQL_CONTAINER" mysql -uroot -e \
		"GRANT SELECT, REPLICATION SLAVE, REPLICATION CLIENT ON *.* TO 'sentinel'@'%'; FLUSH PRIVILEGES;"
}

initialize_vehicles() {
	docker exec -i -e MYSQL_PWD="$MYSQL_PASSWORD" "$MYSQL_CONTAINER" \
		mysql -usentinel "$MYSQL_DATABASE" <<'EOF'
CREATE TABLE IF NOT EXISTS vehicles (
	plate varchar(32) NOT NULL,
	driver_name varchar(255) NOT NULL,
	customer_name varchar(255) NOT NULL,
	make varchar(255) NOT NULL,
	model varchar(255) NOT NULL,
	color varchar(255) NOT NULL,
	location varchar(255) NOT NULL,
	PRIMARY KEY (plate)
);
TRUNCATE TABLE vehicles;
INSERT INTO vehicles (plate, driver_name, customer_name, make, model, color, location) VALUES
	('A1234', 'Elijah Brooks', 'Sophia Carter', 'Toyota', 'Camry', 'Blue', 'Parking'),
	('B5678', 'Isabella Reed', 'Mason Rivera', 'Ford', 'F-150', 'Red', 'Parking'),
	('C9876', 'Liam Bennett', 'Ava Mitchell', 'Honda', 'Civic', 'Black', 'Parking'),
	('F1000', 'Pat Ellis', 'Noah Patel', 'Nissan', 'Leaf', 'White', 'Parking'),
	('G2000', 'Sam Ortiz', 'Riley Chen', 'Nissan', 'Leaf', 'White', 'Curbside');
EOF
}

stop_demo() {
	require_docker
	if container_exists; then
		require_demo_container
		docker rm -f "$CONTAINER_NAME" >/dev/null
	fi
	if mysql_container_exists; then
		require_owned_container "$MYSQL_CONTAINER"
		docker rm -f "$MYSQL_CONTAINER" >/dev/null
	fi
	rm -f -- \
		"$SECRETS_JSON" \
		"$SECRETS_DIR/postgres.connection-string" \
		"$SECRETS_DIR/postgres.password" \
		"$SECRETS_DIR/mysql.password"
	printf 'Stopped the local PostgreSQL and MySQL containers and removed this demo’s generated files.\n'
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

	if mysql_container_exists; then
		require_owned_container "$MYSQL_CONTAINER"
		require_expected_mysql_configuration
		if ! named_container_is_running "$MYSQL_CONTAINER"; then
			require_available_mysql_port
			docker start "$MYSQL_CONTAINER" >/dev/null
		fi
	else
		require_available_mysql_port
		docker run -d --name "$MYSQL_CONTAINER" \
			--label "$CONTAINER_LABEL_KEY=$CONTAINER_LABEL_VALUE" \
			-e MYSQL_ROOT_PASSWORD="$MYSQL_PASSWORD" \
			-e MYSQL_DATABASE="$MYSQL_DATABASE" \
			-e MYSQL_USER=sentinel \
			-e MYSQL_PASSWORD="$MYSQL_PASSWORD" \
			-p "127.0.0.1:$MYSQL_PORT:3306" \
			"$MYSQL_IMAGE" \
			--server-id=1 \
			--log-bin=mysql-bin \
			--binlog-format=ROW \
			--binlog-row-image=FULL \
			--binlog-row-metadata=FULL \
			--gtid-mode=ON \
			--enforce-gtid-consistency=ON >/dev/null
	fi

	wait_for_postgres
	wait_for_mysql
	ensure_database retail
	reset_replication_slots
	drop_physical_postgres_database
	reset_sentinel_state
	initialize_retail
	grant_mysql_replication
	initialize_vehicles
	write_secret_files

	cat <<EOF
Local curbside resources are ready.

PostgreSQL listens only on 127.0.0.1:$POSTGRES_PORT. Database retail holds orders.
MySQL listens only on 127.0.0.1:$MYSQL_PORT. Database physical holds vehicles.
Database sentinel stores Sentinel task state.
sentinel.yaml declares the PostgreSQL and MySQL source and bootstrap plugins.
Sentinel downloads and installs those refs when the host starts.

Orders start as preparing. Vehicles start in Parking. Plates are A1234, B5678, and C9876.

From $EXAMPLE_DIR, run ./demo.sh.
EOF
}

case "${1:-start}" in
	start) start_demo ;;
	stop) stop_demo ;;
	*) fail "Usage: $0 [start|stop]" ;;
esac
