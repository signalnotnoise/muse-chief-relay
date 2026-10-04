#!/usr/bin/env python3
"""Dot-local entry point for the shared config validator.

The single implementation lives in bots/shared/check_config.py, including
strict load_config(). This file re-exports it so existing imports keep working:

    from check_config import load_config, validate

used by go.py, mcp_events.py, reply.py, run.sh, and the dot tests. It loads
the shared module from its file location so the module name never collides
with this re-export.
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
load_config = _loaded.load_config
validate = _loaded.validate
main = _loaded.main
del _loaded


if __name__ == '__main__':
    raise SystemExit(main())
