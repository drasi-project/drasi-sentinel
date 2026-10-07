#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly DEMO="$ROOT/examples/accounting-local-demo/demo.sh"
readonly CARD="$ROOT/src/Drasi.Sentinel.Host/AgentCard.cs"

bash -n "$DEMO"
"$DEMO" --preflight >/dev/null
grep -F 'streaming = true' "$CARD" >/dev/null
grep -F 'SendStreamingMessage' "$ROOT/src/Drasi.Sentinel.Host/A2aProtocol.cs" >/dev/null
grep -F 'SubscribeToTask' "$ROOT/src/Drasi.Sentinel.Host/A2aProtocol.cs" >/dev/null
grep -F 'Last-Event-ID' "$ROOT/src/Drasi.Sentinel.Host/A2aEndpoint.cs" >/dev/null
if find "$ROOT/src" "$ROOT/examples" -type f \( -name '*.cs' -o -name '*.yaml' -o -name '*.md' -o -name '*.sh' \) \
	! -name 'capture_receiver.py' -print0 | xargs -0 grep -E -i -q 'confirmation code|webhook|destination'; then
	printf 'Legacy consumer terminology remains.\n' >&2
	exit 1
fi
printf 'A2A Inspector streaming static checks passed.\n'
