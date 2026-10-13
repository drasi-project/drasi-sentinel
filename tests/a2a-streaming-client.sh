#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly DEMO="$ROOT/examples/accounting-local-demo/demo.sh"
readonly SECRETS="$ROOT/examples/accounting-local-demo/secrets"
readonly EVENTS="$SECRETS/a2a-streaming-events.sse"
readonly REPLAY="$SECRETS/a2a-streaming-replay.sse"
readonly CURRENT_BEFORE="$SECRETS/a2a-current-before.sse"
readonly CURRENT_AFTER="$SECRETS/a2a-current-after.sse"
readonly CONNECTION="$SECRETS/sentinel-connection.txt"
readonly TASK_ID="streaming-live-$RANDOM-$$"

demo_pid=""
stream_pid=""

cleanup() {
	local status=$?
	[[ -z "$stream_pid" ]] || kill "$stream_pid" >/dev/null 2>&1 || true
	[[ -z "$demo_pid" ]] || kill "$demo_pid" >/dev/null 2>&1 || true
	wait "$stream_pid" 2>/dev/null || true
	wait "$demo_pid" 2>/dev/null || true
	exit "$status"
}

wait_for_host() {
	for _ in {1..90}; do
		[[ -f "$CONNECTION" ]] && return
		kill -0 "$demo_pid" >/dev/null 2>&1 || return 1
		sleep 1
	done
	return 1
}

wait_for_diffs() {
	local expected_count="$1"
	for _ in {1..30}; do
		local count
		curl --no-buffer --silent --max-time 2 \
			-H 'Content-Type: application/json' \
			-H 'Last-Event-ID: 0' \
			-X POST "$ENDPOINT" \
			--data "{\"jsonrpc\":\"2.0\",\"id\":\"live-poll\",\"method\":\"SubscribeToTask\",\"params\":{\"taskId\":\"$TASK_ID\"}}" \
			>"$REPLAY" || [[ $? -eq 28 ]]
		count="$(grep -Fc '"data":{"type":"native_query_diff"' "$REPLAY" 2>/dev/null || true)"
		if [[ "$count" -ge "$expected_count" ]] &&
			grep -F '"subscriptionId":' "$REPLAY" >/dev/null 2>&1 &&
			grep -F '"queryId":"invoice-paid"' "$REPLAY" >/dev/null 2>&1 &&
			grep -F '"reactionId":"notify-account-owner"' "$REPLAY" >/dev/null 2>&1 &&
			grep -F '"diffType":' "$REPLAY" >/dev/null 2>&1; then
			return
		fi
		sleep 1
	done
	return 1
}

wait_for_active_subscription() {
	for _ in {1..90}; do
		grep -F ' is active.' "$EVENTS" >/dev/null 2>&1 && return
		kill -0 "$stream_pid" >/dev/null 2>&1 || return 1
		sleep 1
	done
	return 1
}

request_current_results() {
	local output="$1"
	local message_id="$2"
	local expected_amount="$3"
	curl --no-buffer --silent --max-time 15 \
		-H 'Content-Type: application/json' \
		-X POST "$ENDPOINT" \
		--data "{\"jsonrpc\":\"2.0\",\"id\":\"$message_id\",\"method\":\"SendStreamingMessage\",\"params\":{\"message\":{\"role\":\"ROLE_USER\",\"taskId\":\"$TASK_ID\",\"messageId\":\"$message_id\",\"parts\":[{\"text\":\"What are the current results for this task?\",\"mediaType\":\"text/plain\"}]}}}" \
		>"$output" || [[ $? -eq 28 ]]
	grep -F "\"data\":{\"type\":\"current_query_results\",\"taskId\":\"$TASK_ID\"" "$output" >/dev/null
	grep -F '"subscriptionId":' "$output" >/dev/null
	grep -F '"queryId":"invoice-paid"' "$output" >/dev/null
	grep -F '"rows":[' "$output" >/dev/null
	if [[ -n "$expected_amount" ]]; then
		awk -v amount="$expected_amount" \
			'index($0, "\"type\":\"current_query_results\"") && index($0, "\"amount\":" amount) { found = 1 } END { exit !found }' \
			"$output"
	fi
}

assert_diff_count() {
	local expected_count="$1"
	local count
	count="$(grep -Fc '"data":{"type":"native_query_diff"' "$REPLAY" 2>/dev/null || true)"
	[[ "$count" -eq "$expected_count" ]] || {
		cat "$REPLAY" >&2
		exit 1
	}
}

assert_diff_type_count() {
	local type="$1"
	local expected_count="$2"
	local count
	count="$(grep -Fc "\"diffType\":\"$type\"" "$REPLAY" 2>/dev/null || true)"
	[[ "$count" -eq "$expected_count" ]] || {
		cat "$REPLAY" >&2
		exit 1
	}
}

trap cleanup EXIT INT TERM
rm -f "$EVENTS" "$REPLAY" "$CURRENT_BEFORE" "$CURRENT_AFTER" "$CONNECTION"
"$DEMO" >"$SECRETS/a2a-streaming-demo.log" 2>&1 &
demo_pid=$!
wait_for_host || { cat "$SECRETS/a2a-streaming-demo.log" >&2; exit 1; }
ENDPOINT="$(awk -F= '$1=="agent_endpoint" {print $2}' "$CONNECTION")"
[[ -n "$ENDPOINT" ]] || { cat "$SECRETS/a2a-streaming-demo.log" >&2; exit 1; }

curl --no-buffer --silent --show-error --max-time 120 \
	-H 'Content-Type: application/json' \
	-X POST "$ENDPOINT" \
	--data "{\"jsonrpc\":\"2.0\",\"id\":\"live-send\",\"method\":\"SendStreamingMessage\",\"params\":{\"message\":{\"role\":\"ROLE_USER\",\"taskId\":\"$TASK_ID\",\"messageId\":\"request-$TASK_ID\",\"parts\":[{\"text\":\"Tell me when an invoice is paid. Keep the watch for 7 days.\",\"mediaType\":\"text/plain\"}]}}}" \
	>"$EVENTS" &
stream_pid=$!
wait_for_active_subscription || { cat "$EVENTS" >&2; exit 1; }
request_current_results "$CURRENT_BEFORE" "current-before-$TASK_ID" ""
awk 'index($0, "\"type\":\"current_query_results\"") && index($0, "invoice-demo-001") { found = 1 } END { exit found }' \
	"$CURRENT_BEFORE"
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
	-c "UPDATE invoices SET paid = true, amount = 12501 WHERE invoice_id = 'invoice-demo-001'" >/dev/null
sleep 3
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
	-c "UPDATE invoices SET amount = 12502 WHERE invoice_id = 'invoice-demo-001'" >/dev/null
sleep 3
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
	-c "UPDATE invoices SET paid = false WHERE invoice_id = 'invoice-demo-001'" >/dev/null
sleep 3
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
	-c "DELETE FROM invoices WHERE invoice_id = 'invoice-demo-001'" >/dev/null
sleep 3
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
	-c "INSERT INTO invoices (invoice_id, account_id, amount, paid) VALUES ('invoice-demo-001', 'account-300', 15000, false)" >/dev/null
sleep 3
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
	-c "UPDATE invoices SET paid = true, amount = 15001 WHERE invoice_id = 'invoice-demo-001'" >/dev/null
wait_for_diffs 4 || { cat "$EVENTS" >&2; exit 1; }
request_current_results "$CURRENT_AFTER" "current-after-$TASK_ID" 15001
awk 'index($0, "\"type\":\"current_query_results\"") && index($0, "12502") { found = 1 } END { exit found }' \
	"$CURRENT_AFTER"
assert_diff_count 4
assert_diff_type_count Update 1
assert_diff_type_count Delete 1
assert_diff_type_count Add 2
grep -F '"data":{"account_id":"account-200","amount":12501,"invoice_id":"invoice-demo-001","paid":true}' "$REPLAY" >/dev/null
grep -F '"before":{"account_id":"account-200","amount":12501,"invoice_id":"invoice-demo-001","paid":true}' "$REPLAY" >/dev/null
grep -F '"after":{"account_id":"account-200","amount":12502,"invoice_id":"invoice-demo-001","paid":true}' "$REPLAY" >/dev/null
grep -F '"data":{"account_id":"account-200","amount":12502,"invoice_id":"invoice-demo-001","paid":true}' "$REPLAY" >/dev/null
grep -F '"data":{"account_id":"account-300","amount":15001,"invoice_id":"invoice-demo-001","paid":true}' "$REPLAY" >/dev/null

kill "$stream_pid" >/dev/null 2>&1 || true
wait "$stream_pid" 2>/dev/null || true
stream_pid=""

curl --no-buffer --silent --max-time 15 \
	-H 'Content-Type: application/json' \
	-H 'Last-Event-ID: 1' \
	-X POST "$ENDPOINT" \
	--data "{\"jsonrpc\":\"2.0\",\"id\":\"live-subscribe\",\"method\":\"SubscribeToTask\",\"params\":{\"taskId\":\"$TASK_ID\"}}" \
	>"$REPLAY" || [[ $? -eq 28 ]]

grep -F 'id: 2' "$REPLAY" >/dev/null
grep -F '"artifactUpdate"' "$REPLAY" >/dev/null
grep -F '"data":{"type":"native_query_diff"' "$REPLAY" >/dev/null
grep -F '"diffType":"Update"' "$REPLAY" >/dev/null
printf 'Live A2A SSE delivers current results before and after four typed PostgreSQL query diffs, then replays them.\n'
