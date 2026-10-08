#!/usr/bin/env bash
set -euo pipefail

readonly EXAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ROOT_DIR="$(cd "$EXAMPLE_DIR/../.." && pwd)"
readonly BOOTSTRAP="$EXAMPLE_DIR/bootstrap-local-demo.sh"
readonly MANIFEST="$EXAMPLE_DIR/sentinel.yaml"
readonly HOST_PROJECT="$ROOT_DIR/src/Drasi.Sentinel.Host/Drasi.Sentinel.Host.csproj"
readonly INSPECTOR_DIR="$ROOT_DIR/a2a-inspector"
readonly INSPECTOR_RUNNER="$INSPECTOR_DIR/scripts/run.sh"
readonly CONTAINER_NAME="drasi-sentinel-accounting-local-demo"
readonly HOST_LOG="$EXAMPLE_DIR/secrets/host.log"
readonly SENTINEL_CONNECTION="$EXAMPLE_DIR/secrets/sentinel-connection.txt"

host_pid=""
inspector_pid=""
webhook_pid=""

choose_port() {
	python3 - <<'PY'
import socket

with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
}

stop_process() {
	local pid="$1"
	[[ -n "$pid" ]] || return
	kill "$pid" >/dev/null 2>&1 || return
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
	stop_process "$webhook_pid"
	"$BOOTSTRAP" stop >/dev/null 2>&1 || true
	exit "$status"
}

if [[ "${1:-}" == "--preflight" ]]; then
	bash -n "$BOOTSTRAP"
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
	printf 'A2A Inspector runner not found at %s.\\n' "$INSPECTOR_RUNNER" >&2
	exit 2
}

trap cleanup EXIT INT TERM
printf 'Preparing local PostgreSQL.\n'
"$BOOTSTRAP" start >/dev/null
printf 'Starting the local webhook receiver, Sentinel, and A2A Inspector.\n'
webhook_log="$EXAMPLE_DIR/secrets/webhook-events.jsonl"
rm -f "$webhook_log"
python3 "$EXAMPLE_DIR/capture_receiver.py" --bind 127.0.0.1 --port 8082 --output "$webhook_log" \
	>"$EXAMPLE_DIR/secrets/webhook.log" 2>&1 &
webhook_pid=$!
rm -f "$SENTINEL_CONNECTION"
host_url="http://127.0.0.1:$(choose_port)"
(
	cd "$EXAMPLE_DIR"
	exec env ASPNETCORE_URLS="$host_url" dotnet run --no-launch-profile --project "$HOST_PROJECT" -- "$MANIFEST"
) >"$HOST_LOG" 2>&1 &
host_pid=$!

(
	cd "$INSPECTOR_DIR"
	exec env A2A_INSPECTOR_PORT=8081 bash "$INSPECTOR_RUNNER"
) >"$EXAMPLE_DIR/secrets/inspector.log" 2>&1 &
inspector_pid=$!

until python3 -c 'import socket; socket.create_connection(("127.0.0.1", 8082), 1).close()' >/dev/null 2>&1; do
	kill -0 "$webhook_pid" >/dev/null 2>&1 || { cat "$EXAMPLE_DIR/secrets/webhook.log" >&2; exit 1; }
	sleep 1
done

until curl --silent --fail "$host_url/healthz" >/dev/null; do
	kill -0 "$host_pid" >/dev/null 2>&1 || { cat "$HOST_LOG" >&2; exit 1; }
	sleep 1
done

(
	umask 077
	printf 'agent_card_url=%s\nagent_endpoint=%s\n' \
		"$host_url/.well-known/agent-card.json" "$host_url/a2a/" > "$SENTINEL_CONNECTION"
)
chmod 600 "$SENTINEL_CONNECTION"

until curl --silent --fail http://127.0.0.1:8081/ >/dev/null; do
	kill -0 "$inspector_pid" >/dev/null 2>&1 || {
		cat "$EXAMPLE_DIR/secrets/inspector.log" >&2
		exit 1
	}
	sleep 1
done

cat <<EOF
Connect A2A Inspector to:
  Inspector UI: http://127.0.0.1:8081/
  Agent Card URL: $host_url/.well-known/agent-card.json
  Agent endpoint: $host_url/a2a/
  Webhook capture: $webhook_log

In A2A Inspector, click Send streaming and ask:
  Tell me when an invoice is paid.

Inspector sends the standard A2A SendStreamingMessage request and renders
the task updates. If the task stream disconnects, click Resume task stream
to send SubscribeToTask. This demo waits for the active subscription. Then
run the printed PostgreSQL update in a second terminal.
EOF

until docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc \
	"SELECT to_regclass('public.sentinel_subscriptions') IS NOT NULL;" |
	tr -d '[:space:]' | grep -qx t; do
	kill -0 "$host_pid" >/dev/null 2>&1 || { cat "$HOST_LOG" >&2; exit 1; }
	sleep 1
done

until docker exec "$CONTAINER_NAME" psql -U sentinel -d sentinel -tAc \
	"SELECT EXISTS (SELECT 1 FROM sentinel_subscriptions WHERE state = 'Active' AND query_id = 'invoice-paid');" |
	tr -d '[:space:]' | grep -qx t; do
	kill -0 "$host_pid" >/dev/null 2>&1 || { cat "$HOST_LOG" >&2; exit 1; }
	sleep 1
done

cat <<EOF
The seeded invoice starts unpaid, so it is not in the result set. In another terminal, run:

docker exec $CONTAINER_NAME psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \\
  -c "UPDATE invoices SET paid = true WHERE invoice_id = 'invoice-demo-001'"

That SQL update marks the invoice paid and adds it to the result set.
The same diff JSON is also posted to http://127.0.0.1:8082/ and appended to:
  $webhook_log

EOF
wait "$host_pid"
