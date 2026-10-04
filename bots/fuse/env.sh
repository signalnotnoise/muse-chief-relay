#!/usr/bin/env bash
# Source this file: source /path/to/repo/bots/fuse/env.sh
# Thin wrapper: selects Fuse's private tooling dir, then shared env selection.
# Only exports paths/settings; never installs, creates state, or starts a service.
_fuse_env_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
export FUSE_TOOLING_DIR="${FUSE_TOOLING_DIR:-$_fuse_env_dir/runtime/tooling}"
export BRIDGE_TOOLING_VAR=FUSE_TOOLING_DIR
# shellcheck source=../shared/env.sh
source "$_fuse_env_dir/../shared/env.sh"
unset _fuse_env_dir
