#!/usr/bin/env bash
# Source this file: source /path/to/repo/bots/grok/env.sh
# Only exports paths/settings; never installs, creates state, or starts a service.
_grok_env_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
export GROK_TOOLING_DIR="${GROK_TOOLING_DIR:-$_grok_env_dir/runtime/tooling}"
if [[ "$GROK_TOOLING_DIR" != /* ]]; then
  printf '%s\n' 'GROK_TOOLING_DIR must be an absolute path.' >&2
  unset _grok_env_dir
  return 2 2>/dev/null || exit 2
fi

# Prefer an explicitly selected runtime, then a private installation if present.
# Otherwise leave DOTNET_ROOT unset and let the system dotnet host find its runtime.
if [[ -z "${DOTNET_ROOT:-}" && -x "$GROK_TOOLING_DIR/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$GROK_TOOLING_DIR/dotnet"
fi
if [[ -n "${DOTNET_ROOT:-}" ]]; then
  if [[ "$DOTNET_ROOT" != /* ]]; then
    printf '%s\n' 'DOTNET_ROOT must be an absolute path.' >&2
    unset _grok_env_dir
    return 2 2>/dev/null || exit 2
  fi
  export DOTNET_ROOT
  case ":${PATH:-}:" in
    *":$DOTNET_ROOT:"*) ;;
    *) export PATH="$DOTNET_ROOT:${PATH:-/usr/bin:/bin}" ;;
  esac
fi

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$GROK_TOOLING_DIR}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$GROK_TOOLING_DIR/nuget}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$GROK_TOOLING_DIR/nuget-http-cache}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-$GROK_TOOLING_DIR/data}"
unset _grok_env_dir
