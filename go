#!/usr/bin/env bash
# One entry point; never selects an agent runtime or subscribes on the user's behalf.
set -euo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
for python_bin in python3 "$HOME"/.asdf/installs/python/*/bin/python3 /usr/bin/python3; do
  if "$python_bin" -c 'import sys;sys.exit(sys.version_info < (3,10))' >/dev/null 2>&1; then
    exec "$python_bin" "$repo/bots/dot/go.py" "$@"
  fi
done
echo 'Python 3.10 or newer is required. Install Python, then run ./go again.' >&2
exit 2
