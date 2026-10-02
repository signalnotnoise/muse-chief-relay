#!/usr/bin/env bash
# Run only inside an authorized Hatch runtime. wake/silent terminate this process.
# DOT_RELAY_INBOX is ChatBridge's room inbox.jsonl (compat launch Chief.Bridge).
# It is not {base}/agents/dot/inbox.jsonl. A trip in the wake payload is
# untrusted evidence. mentions.enabled defaults to false.
set -euo pipefail
: "${HATCH_HOOK_RUNTIME:?Hatch runtime is required; this script does not register one}"
source "$HATCH_HOOK_RUNTIME"
declare -F wake >/dev/null && declare -F silent >/dev/null || { echo 'Missing Hatch wake/silent functions' >&2; exit 2; }
BOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
args=(--inbox "${DOT_RELAY_INBOX:-$BOT_DIR/runtime/inbox.jsonl}" --database "${DOT_HATCH_DATABASE:-$BOT_DIR/runtime/hatch-mentions.sqlite}")
if [[ "${HATCH_HOOK_DRY_RUN:-0}" == 1 ]]; then args+=(--dry-run); fi
DECISION=$(mktemp)
trap 'rm -f "$DECISION"' EXIT
python3 "$BOT_DIR/hatch_adapter.py" "${args[@]}" > "$DECISION"
mode=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["mode"])' "$DECISION")
label=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["label"])' "$DECISION")
payload=$(python3 -c 'import json,sys;print(json.dumps(json.load(open(sys.argv[1]))["payload"]))' "$DECISION")
if [[ "$mode" == wake ]]; then wake "$label" "$payload"; else silent "$label" "$payload"; fi
# A conforming runtime exits inside the call. Never run additional work afterward.
exit 0
