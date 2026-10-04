#!/usr/bin/env python3
"""Dot-local entry point for the shared config validator.

The single implementation lives in bots/shared/check_config.py. This file
keeps the existing `from check_config import validate` imports (go.py,
mcp_events.py, reply.py, run.sh, tests) working unchanged. It loads the
shared module from its file location so the module name never collides with
this re-export.
"""
import importlib.util
import os

_SHARED = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', 'shared', 'check_config.py')


def _load():
    spec = importlib.util.spec_from_file_location('shared_check_config', _SHARED)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_loaded = _load()
validate = _loaded.validate
main = _loaded.main
del _loaded


if __name__ == '__main__':
    raise SystemExit(main())
