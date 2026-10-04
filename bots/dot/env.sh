#!/usr/bin/env bash
# Source this file: source /path/to/repo/bots/dot/env.sh
# Thin wrapper: selects dot's private tooling dir, then shared env selection.
# Only exports paths/settings; never installs, creates state, or starts a service.
_dot_env_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
export DOT_TOOLING_DIR="${DOT_TOOLING_DIR:-$_dot_env_dir/runtime/tooling}"
export BRIDGE_TOOLING_VAR=DOT_TOOLING_DIR
# shellcheck source=../shared/env.sh
# Preserve the helper's status through cleanup: capture it via || return, so a
# failure (return 2) is not masked by the trailing unset's success in a normal
# sourced shell or an && chain.
source "$_dot_env_dir/../shared/env.sh" || return $?
unset _dot_env_dir
