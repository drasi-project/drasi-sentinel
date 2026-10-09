#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ROOT_DIR="$(cd "$EXAMPLE_DIR/../.." && pwd)"
readonly BOOTSTRAP="$EXAMPLE_DIR/bootstrap-local-demo.sh"
readonly MANIFEST="$EXAMPLE_DIR/sentinel.yaml"
readonly HOST_PROJECT="$ROOT_DIR/src/Drasi.Sentinel.Host/Drasi.Sentinel.Host.csproj"
readonly INSPECTOR_DIR="$ROOT_DIR/a2a-inspector"
readonly INSPECTOR_RUNNER="$INSPECTOR_DIR/scripts/run.sh"
readonly CONTAINER_NAME="drasi-sentinel-curbside-local-demo"
readonly MYSQL_CONTAINER="drasi-sentinel-curbside-mysql"
readonly HOST_LOG="$EXAMPLE_DIR/secrets/host.log"
readonly SENTINEL_CONNECTION="$EXAMPLE_DIR/secrets/sentinel-connection.txt"
readonly INSPECTOR_PORT=8083

host_pid=""
inspector_pid=""

# shellcheck source=prompts.sh
source "$EXAMPLE_DIR/prompts.sh"

choose_port() {
	python3 - <<'PY'
import socket

with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
}

stop_tree() {
	local pid="$1"
	local child
	[[ -n "$pid" ]] || return
	while read -r child; do
		[[ -n "$child" ]] || continue
		stop_tree "$child"
	done < <(pgrep -P "$pid" || true)
	kill "$pid" >/dev/null 2>&1 || true
}

stop_process() {
	local pid="$1"
	[[ -n "$pid" ]] || return
	stop_tree "$pid"
	for _ in {1..10}; do
		kill -0 "$pid" >/dev/null 2>&1 || break
		sleep 1
	done
	if kill -0 "$pid" >/dev/null 2>&1; then
		kill -9 "$pid" >/dev/null 2>&1 || true
	fi
	wait "$pid" 2>/dev/null || true
}

cleanup() {
	local status=$?
	stop_process "$host_pid"
	stop_process "$inspector_pid"
	"$BOOTSTRAP" stop >/dev/null 2>&1 || true
	exit "$status"
}

if [[ "${1:-}" == "--preflight" ]]; then
	bash -n "$BOOTSTRAP"
	bash -n "$EXAMPLE_DIR/prompts.sh"
	[[ -f "$MANIFEST" && -f "$HOST_PROJECT" && -x "$INSPECTOR_RUNNER" ]]
	printf 'Demo preflight passed.\n'
	exit 0
fi

if [[ -f "$EXAMPLE_DIR/.env" ]]; then
	set -a
	# shellcheck source=/dev/null
	source "$EXAMPLE_DIR/.env"
	set +a
fi
[[ -n "${AZURE_OPENAI_ENDPOINT:-}" && -n "${AZURE_OPENAI_MODEL:-}" ]] || {
	printf 'Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_MODEL before running this demo.\n' >&2
	exit 2
}
[[ -x "$INSPECTOR_RUNNER" ]] || {
	printf 'A2A Inspector runner not found at %s.\n' "$INSPECTOR_RUNNER" >&2
	exit 2
}

trap cleanup EXIT INT TERM
printf 'Preparing local PostgreSQL and MySQL.\n'
"$BOOTSTRAP" start >/dev/null
printf 'Starting Sentinel and A2A Inspector. Plugin refs in sentinel.yaml are installed on startup.\n'
rm -f "$SENTINEL_CONNECTION"
host_url="http://127.0.0.1:$(choose_port)"
(
	cd "$EXAMPLE_DIR"
	exec env ASPNETCORE_URLS="$host_url" dotnet run --no-launch-profile --project "$HOST_PROJECT" -- "$MANIFEST"
) >"$HOST_LOG" 2>&1 &
host_pid=$!

(
	cd "$INSPECTOR_DIR"
	exec env A2A_INSPECTOR_PORT="$INSPECTOR_PORT" bash "$INSPECTOR_RUNNER"
) >"$EXAMPLE_DIR/secrets/inspector.log" 2>&1 &
inspector_pid=$!

for _ in {1..180}; do
	curl --silent --fail "$host_url/healthz" >/dev/null && break
	kill -0 "$host_pid" >/dev/null 2>&1 || { cat "$HOST_LOG" >&2; exit 1; }
	sleep 1
done
curl --silent --fail "$host_url/healthz" >/dev/null || { cat "$HOST_LOG" >&2; exit 1; }

(
	umask 077
	printf 'agent_card_url=%s\nagent_endpoint=%s\n' \
		"$host_url/.well-known/agent-card.json" "$host_url/a2a/" > "$SENTINEL_CONNECTION"
)
chmod 600 "$SENTINEL_CONNECTION"

for _ in {1..90}; do
	curl --silent --fail "http://127.0.0.1:$INSPECTOR_PORT/" >/dev/null && break
	kill -0 "$inspector_pid" >/dev/null 2>&1 || {
		cat "$EXAMPLE_DIR/secrets/inspector.log" >&2
		exit 1
	}
	sleep 1
done
curl --silent --fail "http://127.0.0.1:$INSPECTOR_PORT/" >/dev/null || {
	cat "$EXAMPLE_DIR/secrets/inspector.log" >&2
	exit 1
}

cat <<EOF
Connect A2A Inspector to:
  Inspector UI: http://127.0.0.1:$INSPECTOR_PORT/
  Agent Card URL: $host_url/.well-known/agent-card.json
  Agent endpoint: $host_url/a2a/

The catalog has a PostgreSQL orders source, a MySQL vehicles source, and no queries.
Start a new task for each prompt.
One task watches one query. Click Send streaming, then wait until Inspector
shows that the subscription is active before you start the next task.

1. Orders still preparing
$PROMPT_PREPARING

2. Orders ready
$PROMPT_READY

3. Vehicles in Parking
$PROMPT_PARKING

4. Vehicles at Curbside
$PROMPT_CURBSIDE

5. Ready order whose vehicle is at the curbside
$PROMPT_MATCHED

6. Vehicle at the curbside for 10 seconds while the order status is preparing
$PROMPT_DELAYED

The prompts describe the situation. They do not include Cypher.

After the ready-order task is active, mark Sophia Carter ready:

docker exec $CONTAINER_NAME psql -v ON_ERROR_STOP=1 -U sentinel -d retail \\
  -c "UPDATE orders SET status = 'ready' WHERE plate = 'A1234'"

That order leaves the preparing watch and enters the ready watch.

After the matched task is active, move the same vehicle to the curbside:

docker exec -e MYSQL_PWD=sentinel-demo $MYSQL_CONTAINER mysql -usentinel physical \\
  -e "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'A1234'"

The matched watch emits an Add for plate A1234.

For the delay watch, leave Mason Rivera preparing and move plate B5678:

docker exec -e MYSQL_PWD=sentinel-demo $MYSQL_CONTAINER mysql -usentinel physical \\
  -e "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'B5678'"

The delay diff arrives after 10 seconds, not on the update.

EOF
wait "$host_pid"
