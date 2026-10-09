#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ROOT_DIR="$(cd "$EXAMPLE_DIR/../.." && pwd)"
readonly ACCOUNTING_ENV="$ROOT_DIR/examples/accounting-local-demo/.env"
readonly CONTAINER_NAME="drasi-sentinel-building-comfort"
readonly SECRETS="$EXAMPLE_DIR/secrets"
readonly CONNECTION="$SECRETS/sentinel-connection.txt"
readonly DEMO_LOG="$SECRETS/e2e-demo.log"

# shellcheck source=prompts.sh
source "$EXAMPLE_DIR/prompts.sh"

demo_pid=""
stream_pid=""

fail() {
	printf 'error: %s\n' "$*" >&2
	exit 1
}

stop_tree() {
	local pid="$1"
	local child
	[[ -n "$pid" ]] || return 0
	while read -r child; do
		[[ -n "$child" ]] || continue
		stop_tree "$child"
	done < <(pgrep -P "$pid" || true)
	kill "$pid" >/dev/null 2>&1 || true
}

stop_process() {
	local pid="$1"
	[[ -n "$pid" ]] || return 0
	stop_tree "$pid"
	wait "$pid" 2>/dev/null || true
	return 0
}

dump_failure() {
	printf '\n--- authored queries ---\n' >&2
	docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -c \
		"SELECT query_id, cypher, source_ids, joins FROM sentinel_authored_queries ORDER BY query_id" >&2 || true
	printf '\n--- subscriptions ---\n' >&2
	docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -c \
		"SELECT task_id, query_id, state FROM sentinel_subscriptions ORDER BY created_at" >&2 || true
	if [[ -f "$SECRETS/host.log" ]]; then
		printf '\n--- host log ---\n' >&2
		tail -n 80 "$SECRETS/host.log" >&2 || true
	fi
}

cleanup() {
	local status=$?
	if [[ "$status" -ne 0 ]]; then
		dump_failure
	fi
	stop_process "$stream_pid"
	stop_process "$demo_pid"
	exit "$status"
}

psql_at() {
	docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d "$1" -tAc "$2"
}

wait_for_host() {
	local attempt
	for attempt in {1..180}; do
		[[ -f "$CONNECTION" ]] && return 0
		kill -0 "$demo_pid" >/dev/null 2>&1 || return 1
		sleep 1
	done
	return 1
}

post_json() {
	local output="$1"
	local payload="$2"
	curl --no-buffer --silent --show-error --max-time 240 \
		-H 'Content-Type: application/json' \
		-X POST "$ENDPOINT" \
		--data "$payload" \
		>"$output" || [[ $? -eq 28 ]]
}

send_prompt() {
	local task_id="$1"
	local prompt="$2"
	local output="$3"
	local payload
	payload="$(python3 - "$task_id" "$prompt" <<'PY'
import json, sys
print(json.dumps({
    "jsonrpc": "2.0",
    "id": "live-send",
    "method": "SendStreamingMessage",
    "params": {
        "message": {
            "role": "ROLE_USER",
            "taskId": sys.argv[1],
            "messageId": "request-" + sys.argv[1],
            "parts": [{"text": sys.argv[2], "mediaType": "text/plain"}],
        }
    },
}))
PY
)"
	: >"$output"
	post_json "$output" "$payload" &
	stream_pid=$!
}

wait_for_active() {
	local task_id="$1"
	local attempt state
	for attempt in {1..120}; do
		state="$(psql_at sentinel "SELECT state FROM sentinel_subscriptions WHERE task_id = '$task_id'" | tr -d '[:space:]' || true)"
		[[ "$state" == "Active" ]] && return 0
		if ! kill -0 "$stream_pid" >/dev/null 2>&1 && [[ "$attempt" -gt 5 ]]; then
			return 1
		fi
		sleep 2
	done
	return 1
}

wait_for_slots() {
	local attempt active
	for attempt in {1..30}; do
		active="$(psql_at sentinel "SELECT count(*) FROM pg_replication_slots WHERE active" | tr -d '[:space:]' || true)"
		[[ "$active" -ge 1 ]] && return 0
		sleep 1
	done
	return 1
}

query_for_task() {
	psql_at sentinel "SELECT query_id FROM sentinel_subscriptions WHERE task_id = '$1' AND state = 'Active'" | tr -d '[:space:]'
}

authored_field() {
	local query_id="$1"
	local column="$2"
	psql_at sentinel "SELECT $column FROM sentinel_authored_queries WHERE query_id = '$query_id'"
}

replay_task() {
	local task_id="$1"
	local output="$2"
	local payload
	payload="$(python3 - "$task_id" <<'PY'
import json, sys
print(json.dumps({
    "jsonrpc": "2.0",
    "id": "live-replay",
    "method": "SubscribeToTask",
    "params": {"taskId": sys.argv[1]},
}))
PY
)"
	curl --no-buffer --silent --max-time 3 \
		-H 'Content-Type: application/json' \
		-H 'Last-Event-ID: 0' \
		-X POST "$ENDPOINT" \
		--data "$payload" \
		>"$output" || [[ $? -eq 28 ]]
}

wait_for_diff() {
	local task_id="$1"
	local output="$2"
	local needle="$3"
	local attempt journal
	for attempt in {1..40}; do
		journal="$(psql_at sentinel "SELECT content FROM sentinel_task_events WHERE task_id = '$task_id' AND content LIKE '%native_query_diff%' AND content LIKE '%$needle%'" || true)"
		if grep -F "$needle" <<<"$journal" >/dev/null 2>&1; then
			printf '%s\n' "$journal" >"$output"
			return 0
		fi
		replay_task "$task_id" "$output"
		if grep -F '"type":"native_query_diff"' "$output" >/dev/null 2>&1 &&
			grep -F "$needle" "$output" >/dev/null 2>&1; then
			return 0
		fi
		sleep 1
	done
	return 1
}

assert_ci() {
	local haystack="$1"
	local needle="$2"
	local label="$3"
	grep -F -i "$needle" <<<"$haystack" >/dev/null || fail "$label does not contain $needle: $haystack"
}

author_watch() {
	local name="$1"
	local prompt="$2"
	local task="comfort-${name}-$$"
	local attempt output query cypher
	for attempt in 1 2; do
		output="$SECRETS/${name}-events.sse"
		send_prompt "$task" "$prompt" "$output"
		if wait_for_active "$task"; then
			query="$(query_for_task "$task")"
			cypher="$(authored_field "$query" cypher)"
			printf '%s' "$query" >"$SECRETS/${name}-query.txt"
			printf '%s' "$cypher" >"$SECRETS/${name}-cypher.txt"
			printf '%s\n' "$task"
			stop_process "$stream_pid"
			stream_pid=""
			return 0
		fi
		printf '%s attempt %s did not become active. Retrying.\n' "$name" "$attempt" >&2
		stop_process "$stream_pid"
		stream_pid=""
		task="comfort-${name}-$$-$attempt"
	done
	cat "$SECRETS/${name}-events.sse" >&2 || true
	fail "The $name watch did not become active."
}

if [[ -f "$ACCOUNTING_ENV" ]]; then
	set -a
	# shellcheck source=/dev/null
	source "$ACCOUNTING_ENV"
	set +a
fi
if [[ -f "$EXAMPLE_DIR/.env" ]]; then
	set -a
	# shellcheck source=/dev/null
	source "$EXAMPLE_DIR/.env"
	set +a
fi
[[ -n "${AZURE_OPENAI_ENDPOINT:-}" && -n "${AZURE_OPENAI_MODEL:-}" ]] ||
	fail "Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_MODEL. The accounting demo .env is the expected source."

trap cleanup EXIT INT TERM
rm -f "$CONNECTION" "$DEMO_LOG"
"$EXAMPLE_DIR/demo.sh" >"$DEMO_LOG" 2>&1 &
demo_pid=$!
wait_for_host || { cat "$DEMO_LOG" >&2; fail "Sentinel did not become ready."; }
ENDPOINT="$(awk -F= '$1=="agent_endpoint" {print $2}' "$CONNECTION")"
[[ -n "$ENDPOINT" ]] || fail "The connection file has no agent endpoint."
printf 'Host is ready. Model is %s.\n' "$AZURE_OPENAI_MODEL"

room_task="$(author_watch room "$PROMPT_ROOM")"
room_query="$(cat "$SECRETS/room-query.txt")"
room_cypher="$(cat "$SECRETS/room-cypher.txt")"
assert_ci "$room_cypher" "room" "room cypher"
printf 'Room watch committed %s\n%s\n' "$room_query" "$room_cypher"

floor_task="$(author_watch floor "$PROMPT_FLOOR")"
floor_query="$(cat "$SECRETS/floor-query.txt")"
floor_cypher="$(cat "$SECRETS/floor-cypher.txt")"
floor_joins="$(authored_field "$floor_query" joins)"
assert_ci "$floor_cypher" "floor" "floor cypher"
assert_ci "$floor_joins" "floor_id" "floor joins"
printf 'Floor watch committed %s\n%s\n%s\n' "$floor_query" "$floor_cypher" "$floor_joins"

building_task="$(author_watch building "$PROMPT_BUILDING")"
building_query="$(cat "$SECRETS/building-query.txt")"
building_cypher="$(cat "$SECRETS/building-cypher.txt")"
building_joins="$(authored_field "$building_query" joins)"
assert_ci "$building_cypher" "building" "building cypher"
assert_ci "$building_joins" "building_id" "building joins"
printf 'Building watch committed %s\n%s\n%s\n' "$building_query" "$building_cypher" "$building_joins"

wait_for_slots || fail "The PostgreSQL replication slot did not become active."
sleep 2

docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d building_comfort \
	-c "UPDATE \"Room\" SET temperature = 40, humidity = 20, co2 = 700 WHERE floor_id = 'floor_01_01'" >/dev/null

wait_for_diff "$room_task" "$SECRETS/room-replay.sse" "room_01_01_02" || {
	cat "$SECRETS/room-replay.sse" >&2 || true
	fail "The room watch did not emit a diff for room_01_01_02."
}
printf 'Room watch emitted a diff for room_01_01_02.\n'

wait_for_diff "$floor_task" "$SECRETS/floor-replay.sse" "floor_01_01" || {
	cat "$SECRETS/floor-replay.sse" >&2 || true
	fail "The floor watch did not emit a diff for floor_01_01."
}
printf 'Floor watch emitted a diff for floor_01_01.\n'

wait_for_diff "$building_task" "$SECRETS/building-replay.sse" "building_01" || {
	cat "$SECRETS/building-replay.sse" >&2 || true
	fail "The building watch did not emit a diff for building_01."
}
printf 'Building watch emitted a diff for building_01.\n'
printf 'Building comfort authoring end-to-end passed against %s.\n' "$AZURE_OPENAI_MODEL"
