#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ROOT_DIR="$(cd "$EXAMPLE_DIR/../.." && pwd)"
readonly BOOTSTRAP="$EXAMPLE_DIR/bootstrap-local-demo.sh"
readonly MANIFEST="$EXAMPLE_DIR/sentinel.yaml"
readonly HOST_PROJECT="$ROOT_DIR/src/Drasi.Sentinel.Host/Drasi.Sentinel.Host.csproj"
readonly INSPECTOR_DIR="$ROOT_DIR/a2a-inspector"
readonly INSPECTOR_RUNNER="$INSPECTOR_DIR/scripts/run.sh"
readonly CONTAINER_NAME="drasi-sentinel-building-comfort"
readonly HOST_LOG="$EXAMPLE_DIR/secrets/host.log"
readonly INSPECTOR_LOG="$EXAMPLE_DIR/secrets/inspector.log"
readonly SENTINEL_CONNECTION="$EXAMPLE_DIR/secrets/sentinel-connection.txt"
readonly INSPECTOR_PORT=8085

host_pid=""
inspector_pid=""
host_log_lines=0
inspector_log_lines=0

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

listener_on() {
	command -v lsof >/dev/null 2>&1 || return 0
	lsof -nP -iTCP:"$1" -sTCP:LISTEN -t 2>/dev/null | head -1 || true
}

dump_log() {
	printf '\n--- %s ---\n' "$1" >&2
	if [[ -f "$1" ]]; then
		tail -n 40 "$1" >&2
	else
		printf 'missing\n' >&2
	fi
}

show_new_lines() {
	local log="$1"
	local prefix="$2"
	local seen_name="$3"
	local lines seen
	[[ -f "$log" ]] || return 0
	lines="$(wc -l <"$log" | tr -d '[:space:]')"
	seen="${!seen_name}"
	[[ "$lines" -gt "$seen" ]] || return 0
	tail -n +"$((seen + 1))" "$log" | sed "s/^/[$prefix] /" >&2
	printf -v "$seen_name" '%s' "$lines"
}

wait_for_url() {
	local url="$1"
	local pid="$2"
	local log="$3"
	local prefix="$4"
	local seen_name="$5"
	local seconds="$6"
	local started=$SECONDS
	local last_note=$SECONDS
	printf 'Waiting for %s at %s (pid %s). Log: %s\n' "$prefix" "$url" "$pid" "$log" >&2
	while (( SECONDS - started < seconds )); do
		show_new_lines "$log" "$prefix" "$seen_name"
		if ! kill -0 "$pid" >/dev/null 2>&1; then
			printf '%s process %s exited before %s responded.\n' "$prefix" "$pid" "$url" >&2
			dump_log "$log"
			exit 1
		fi
		if [[ "$prefix" == "Inspector" && -f "$log" ]] && grep -E 'Address already in use|died unexpectedly' "$log" >/dev/null; then
			printf 'Inspector failed while starting. The UI process can stay alive after the backend dies.\n' >&2
			dump_log "$log"
			exit 1
		fi
		if curl --silent --fail --max-time 2 --connect-timeout 1 "$url" >/dev/null; then
			printf '%s is up: %s\n' "$prefix" "$url" >&2
			return 0
		fi
		if (( SECONDS - last_note >= 5 )); then
			printf 'Still waiting for %s (%ss). New %s log lines are printed as they arrive.\n' "$url" "$((SECONDS - started))" "$log" >&2
			last_note=$SECONDS
		fi
		sleep 1
	done
	printf 'Timed out after %ss waiting for %s.\n' "$seconds" "$url" >&2
	dump_log "$log"
	exit 1
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

inspector_holder="$(listener_on "$INSPECTOR_PORT")"
if [[ -n "$inspector_holder" ]]; then
	printf 'Inspector port %s is already taken by PID %s.\n' "$INSPECTOR_PORT" "$inspector_holder" >&2
	ps -p "$inspector_holder" -o pid=,etime=,command= >&2 || true
	printf 'That listener is not this demo. Stop it, then run ./demo.sh again.\n' >&2
	exit 1
fi

trap cleanup EXIT INT TERM
printf 'Preparing local PostgreSQL.\n'
"$BOOTSTRAP" start
host_url="http://127.0.0.1:$(choose_port)"
printf 'Starting Sentinel at %s\n' "$host_url"
printf 'Host log: %s\n' "$HOST_LOG"
printf 'Starting A2A Inspector at http://127.0.0.1:%s/\n' "$INSPECTOR_PORT"
printf 'Inspector log: %s\n' "$INSPECTOR_LOG"
rm -f "$SENTINEL_CONNECTION"
: >"$HOST_LOG"
: >"$INSPECTOR_LOG"
(
	cd "$EXAMPLE_DIR"
	exec env ASPNETCORE_URLS="$host_url" dotnet run --no-launch-profile --project "$HOST_PROJECT" -- "$MANIFEST"
) >"$HOST_LOG" 2>&1 &
host_pid=$!

(
	cd "$INSPECTOR_DIR"
	exec env A2A_INSPECTOR_PORT="$INSPECTOR_PORT" bash "$INSPECTOR_RUNNER"
) >"$INSPECTOR_LOG" 2>&1 &
inspector_pid=$!

wait_for_url "$host_url/healthz" "$host_pid" "$HOST_LOG" "Sentinel" host_log_lines 180

(
	umask 077
	printf 'agent_card_url=%s\nagent_endpoint=%s\n' \
		"$host_url/.well-known/agent-card.json" "$host_url/a2a/" > "$SENTINEL_CONNECTION"
)
chmod 600 "$SENTINEL_CONNECTION"

wait_for_url "http://127.0.0.1:$INSPECTOR_PORT/" "$inspector_pid" "$INSPECTOR_LOG" "Inspector" inspector_log_lines 90

cat <<EOF
Connect A2A Inspector to:
  Inspector UI: http://127.0.0.1:$INSPECTOR_PORT/
  Agent Card URL: $host_url/.well-known/agent-card.json
  Agent endpoint: $host_url/a2a/

The catalog has a PostgreSQL building source and no queries.
Start a new task for each prompt.
One task watches one query. Click Send streaming, then wait until Inspector
shows that the subscription is active before you start the next task.

1. Uncomfortable room
$PROMPT_ROOM

2. Uncomfortable floor
$PROMPT_FLOOR

3. Uncomfortable building
$PROMPT_BUILDING

The prompts describe the comfort formula. They do not include Cypher.

After the watches are active, push Floor 01 out of the comfortable band:

docker exec $CONTAINER_NAME psql -v ON_ERROR_STOP=1 -U sentinel -d building_comfort \\
  -c "UPDATE \\"Room\\" SET temperature = 40, humidity = 20, co2 = 700 WHERE floor_id = 'floor_01_01'"

Room 02 on that floor is room_01_01_02. The room watch emits that id.
The floor average falls below 40, so the floor watch emits floor_01_01.
The building average of the three floor levels also falls below 40, so the building watch emits building_01.

EOF
wait "$host_pid"
