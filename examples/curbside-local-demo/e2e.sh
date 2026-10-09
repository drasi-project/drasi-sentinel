#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ROOT_DIR="$(cd "$EXAMPLE_DIR/../.." && pwd)"
readonly ACCOUNTING_ENV="$ROOT_DIR/examples/accounting-local-demo/.env"
readonly CONTAINER_NAME="drasi-sentinel-curbside-local-demo"
readonly MYSQL_CONTAINER="drasi-sentinel-curbside-mysql"
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
	printf '\n--- mysql processlist ---\n' >&2
	docker exec -e MYSQL_PWD=sentinel-demo "$MYSQL_CONTAINER" mysql -uroot -e 'SHOW PROCESSLIST' >&2 || true
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
	if [[ -n "$demo_pid" ]] && kill -0 "$demo_pid" >/dev/null 2>&1; then
		kill -9 "$demo_pid" >/dev/null 2>&1 || true
	fi
	exit "$status"
}

psql_at() {
	docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d "$1" -tAc "$2"
}

mysql_at() {
	docker exec -e MYSQL_PWD=sentinel-demo "$MYSQL_CONTAINER" \
		mysql -usentinel -Nse "$2" "$1"
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

request_current_results() {
	local task_id="$1"
	local output="$2"
	local payload
	payload="$(python3 - "$task_id" <<'PY'
import json, sys
print(json.dumps({
    "jsonrpc": "2.0",
    "id": "current",
    "method": "SendStreamingMessage",
    "params": {
        "message": {
            "role": "ROLE_USER",
            "taskId": sys.argv[1],
            "messageId": "current-" + sys.argv[1],
            "parts": [{"text": "What are the current results for this task?", "mediaType": "text/plain"}],
        }
    },
}))
PY
)"
	curl --no-buffer --silent --max-time 40 \
		-H 'Content-Type: application/json' \
		-X POST "$ENDPOINT" \
		--data "$payload" \
		>"$output" || [[ $? -eq 28 ]]
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
	for attempt in {1..25}; do
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

assert_contains() {
	local haystack="$1"
	local needle="$2"
	local label="$3"
	grep -F "$needle" <<<"$haystack" >/dev/null || fail "$label does not contain $needle: $haystack"
}

if [[ -f "$ACCOUNTING_ENV" ]]; then
	set -a
	# shellcheck source=/dev/null
	source "$ACCOUNTING_ENV"
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

if [[ "${1:-}" != "--delay-only" ]]; then
ready_task="curbside-ready-$$"
send_prompt "$ready_task" "$PROMPT_READY" "$SECRETS/ready-events.sse"
wait_for_active "$ready_task" || { cat "$SECRETS/ready-events.sse" >&2; fail "The ready-order watch did not become active."; }
ready_query="$(query_for_task "$ready_task")"
ready_cypher="$(authored_field "$ready_query" cypher)"
ready_sources="$(authored_field "$ready_query" source_ids)"
assert_contains "$ready_cypher" "ready" "ready cypher"
assert_contains "$ready_sources" "retail-ops" "ready sources"
printf 'Ready watch committed %s\n%s\n' "$ready_query" "$ready_cypher"
stop_process "$stream_pid"
stream_pid=""

matched_task="curbside-matched-$$"
send_prompt "$matched_task" "$PROMPT_MATCHED" "$SECRETS/matched-events.sse"
wait_for_active "$matched_task" || { cat "$SECRETS/matched-events.sse" >&2; fail "The matched watch did not become active."; }
matched_query="$(query_for_task "$matched_task")"
matched_cypher="$(authored_field "$matched_query" cypher)"
matched_sources="$(authored_field "$matched_query" source_ids)"
matched_joins="$(authored_field "$matched_query" joins)"
assert_contains "$matched_cypher" "orders" "matched cypher"
assert_contains "$matched_cypher" "vehicles" "matched cypher"
assert_contains "$matched_cypher" "ready" "matched cypher"
assert_contains "$matched_cypher" "Curbside" "matched cypher"
assert_contains "$matched_sources" "retail-ops" "matched sources"
assert_contains "$matched_sources" "physical-ops" "matched sources"
assert_contains "$matched_joins" "plate" "matched joins"
printf 'Matched watch committed %s\n%s\n%s\n' "$matched_query" "$matched_cypher" "$matched_joins"
wait_for_slots || fail "The PostgreSQL replication slots did not become active."
sleep 2
stop_process "$stream_pid"
stream_pid=""

docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
	-c "UPDATE orders SET status = 'ready' WHERE plate = 'A1234'" >/dev/null
wait_for_diff "$ready_task" "$SECRETS/ready-replay.sse" "A1234" || {
	cat "$SECRETS/ready-replay.sse" >&2
	fail "The ready-order watch did not emit a diff for A1234."
}
printf 'Ready watch emitted a diff for A1234.\n'

mysql_at physical "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'A1234'" >/dev/null
if ! wait_for_diff "$matched_task" "$SECRETS/matched-replay.sse" "A1234"; then
	mysql_at physical "UPDATE vehicles SET location = 'Parking' WHERE plate = 'A1234'" >/dev/null
	sleep 2
	mysql_at physical "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'A1234'" >/dev/null
	wait_for_diff "$matched_task" "$SECRETS/matched-replay.sse" "A1234" || {
		cat "$SECRETS/matched-replay.sse" >&2
		fail "The matched watch did not emit a diff for A1234."
	}
fi
printf 'Matched watch emitted a diff for A1234.\n'
stop_process "$stream_pid"
stream_pid=""

fi
if [[ "${1:-}" != "--delay" && "${1:-}" != "--delay-only" ]]; then
	printf 'Curbside authoring end-to-end passed against %s.\n' "$AZURE_OPENAI_MODEL"
	exit 0
fi
printf 'Starting the delay watch.\n'
delayed_task="curbside-delayed-$$"
delayed_query=""
delayed_cypher=""
for attempt in 1 2; do
	send_prompt "$delayed_task" "$PROMPT_DELAYED" "$SECRETS/delayed-events.sse"
	wait_for_active "$delayed_task" || { cat "$SECRETS/delayed-events.sse" >&2; fail "The delay watch did not become active."; }
	delayed_query="$(query_for_task "$delayed_task")"
	delayed_cypher="$(authored_field "$delayed_query" cypher)"
	if grep -F "trueFor" <<<"$delayed_cypher" >/dev/null && grep -F "preparing" <<<"$delayed_cypher" >/dev/null; then
		break
	fi
	printf 'Delay attempt %s committed the wrong query. Retrying on a new task.\n%s\n' "$attempt" "$delayed_cypher" >&2
	stop_process "$stream_pid"
	stream_pid=""
	delayed_task="curbside-delayed-$$-$attempt"
	delayed_cypher=""
done
assert_contains "$delayed_cypher" "trueFor" "delay cypher"
assert_contains "$delayed_cypher" "preparing" "delay cypher"
printf 'Delay watch committed %s\n%s\n' "$delayed_query" "$delayed_cypher"
wait_for_slots || fail "The PostgreSQL replication slots did not become active."
sleep 2

mysql_at physical "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'B5678'" >/dev/null
mysql_at physical "SELECT CONCAT(plate, ' ', location) FROM vehicles WHERE plate = 'B5678'" >&2
psql_at sentinel "SELECT slot_name || ' active=' || active::text FROM pg_replication_slots" >&2
sleep 4
replay_task "$delayed_task" "$SECRETS/delayed-early.sse"
if grep -F 'B5678' "$SECRETS/delayed-early.sse" >/dev/null 2>&1 &&
	grep -F '"type":"native_query_diff"' "$SECRETS/delayed-early.sse" >/dev/null 2>&1; then
	fail "The delay watch emitted B5678 before 10 seconds. trueFor did not hold."
fi
docker exec "$CONTAINER_NAME" psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
	-c "UPDATE orders SET status = 'preparing' WHERE plate = 'B5678'" >/dev/null
sleep 4
replay_task "$delayed_task" "$SECRETS/delayed-early.sse"
if grep -F 'B5678' "$SECRETS/delayed-early.sse" >/dev/null 2>&1 &&
	grep -F '"type":"native_query_diff"' "$SECRETS/delayed-early.sse" >/dev/null 2>&1; then
	fail "The delay watch emitted B5678 before 10 seconds. trueFor did not hold."
fi
sleep 12
request_current_results "$delayed_task" "$SECRETS/delayed-current.sse"
printf 'Delay current results:\n' >&2
grep -o 'current_query_results[^]]*' "$SECRETS/delayed-current.sse" >&2 || true
wait_for_diff "$delayed_task" "$SECRETS/delayed-replay.sse" "B5678" || {
	cat "$SECRETS/delayed-replay.sse" >&2
	fail "The delay watch did not emit a diff for B5678 after 10 seconds."
}
printf 'Delay watch emitted a diff for B5678 after the wait.\n'
printf 'Curbside authoring end-to-end passed against %s.\n' "$AZURE_OPENAI_MODEL"
