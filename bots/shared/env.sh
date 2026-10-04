#!/usr/bin/env bash
# bots/shared/env.sh — shared private .NET tooling/cache environment selection.
#
# Source this file AFTER exporting:
#   BRIDGE_TOOLING_VAR - name of the env var holding the private tooling dir
#                        (e.g. DOT_TOOLING_DIR or FUSE_TOOLING_DIR). The bot's
#                        thin env.sh wrapper must set that variable's default
#                        (a private absolute directory) before sourcing this.
#
# Only exports paths/settings; never installs, creates state, or starts a service.
if [[ -z "${BRIDGE_TOOLING_VAR:-}" ]]; then
  printf '%s\n' 'BRIDGE_TOOLING_VAR must name the tooling-dir env var (e.g. DOT_TOOLING_DIR).' >&2
  return 2 2>/dev/null || exit 2
fi
_shared_tooling_dir="${!BRIDGE_TOOLING_VAR:-}"
if [[ "$_shared_tooling_dir" != /* ]]; then
  printf '%s\n' "$BRIDGE_TOOLING_VAR must be an absolute path." >&2
  unset _shared_tooling_dir
  return 2 2>/dev/null || exit 2
fi

# Prefer an explicitly selected runtime, then a private installation if present.
# Otherwise leave DOTNET_ROOT unset and let the system dotnet host find its runtime.
if [[ -z "${DOTNET_ROOT:-}" && -x "$_shared_tooling_dir/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$_shared_tooling_dir/dotnet"
fi
if [[ -n "${DOTNET_ROOT:-}" ]]; then
  if [[ "$DOTNET_ROOT" != /* ]]; then
    printf '%s\n' 'DOTNET_ROOT must be an absolute path.' >&2
    unset _shared_tooling_dir
    return 2 2>/dev/null || exit 2
  fi
  export DOTNET_ROOT
  case ":${PATH:-}:" in
    *":$DOTNET_ROOT:"*) ;;
    *) export PATH="$DOTNET_ROOT:${PATH:-/usr/bin:/bin}" ;;
  esac
fi

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$_shared_tooling_dir}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$_shared_tooling_dir/nuget}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$_shared_tooling_dir/nuget-http-cache}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-$_shared_tooling_dir/data}"
unset _shared_tooling_dir
