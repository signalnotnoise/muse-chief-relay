#!/usr/bin/env bash
# Starts ChatBridge via the compatibility assembly Chief.Bridge.dll.
# chat-bridge and src/ChatBridge are the same program. mentions.enabled
# stays off unless the local config turns it on. A trip on
# chatbridge.inbox.wake is untrusted evidence.
set -euo pipefail
BOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd -- "$BOT_DIR/../.." && pwd)
python3 "$BOT_DIR/check_config.py" "$BOT_DIR/config.json"
if [[ "${1:-}" == --check ]]; then exit 0; fi
if [[ $# != 0 ]]; then echo 'Usage: bash bots/dot/run.sh [--check]' >&2; exit 2; fi
if ! command -v dotnet >/dev/null; then
  source "$REPO/../tooling/env.sh"
fi
exec dotnet "$REPO/src/Chief.Bridge/bin/Debug/net8.0/Chief.Bridge.dll" --config "$BOT_DIR/config.json"
