#!/usr/bin/env bash
# Local bridge only. Thin wrapper over bots/shared/launch_bridge.py.
# See bots/grok/LOCAL_SETUP.md before starting or recovering a queue.
# No installer, observer, model invocation, supervisor, or implicit restart.
set -euo pipefail
BOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$BOT_DIR/env.sh"
export BRIDGE_CONFIG_ENV=GROK_BRIDGE_CONFIG
export BRIDGE_SHA256_ENV=GROK_BRIDGE_SHA256
export BRIDGE_DLL_ENV=GROK_BRIDGE_DLL
export BRIDGE_DOTNET_ENV=DOTNET_BIN
export BRIDGE_DLL_DEFAULT="$BOT_DIR/../../src/ChatBridge/bin/Release/net8.0/ChatBridge.dll"
export BRIDGE_NICK=chief
export BRIDGE_MODE_KEY=chief_mode
export BRIDGE_DOCS=bots/grok/LOCAL_SETUP.md
exec python3 -B "$BOT_DIR/../shared/launch_bridge.py" "$@"
