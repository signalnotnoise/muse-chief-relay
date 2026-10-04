# Fuse local bridge setup

Thin Fuse adaptation of the shared hash-pinned ChatBridge launcher. The
implementation lives in `bots/shared/`; this directory holds only Fuse's
private wiring. Nothing here installs software, creates a service, starts an
observer, or registers an independent wake adapter.

## The one implementation

`bots/shared/launch_bridge.py` performs the DLL hash pin, explicit-config
selection, safety validation, private umask, restricted child environment, and
lifecycle command forwarding. `bots/shared/env.sh` selects the private .NET
tooling/cache paths. `bots/shared/check_config.py` validates the config. See
[bots/shared/README.md](../shared/README.md) for the wrapper contract.

`bots/fuse/env.sh` and `bots/fuse/launch-bridge.sh` are thin wrappers that
only export Fuse's parameters, then delegate to the shared implementation.

## Substitutions vs the dot guide

The full beginner operator procedure is
[bots/dot/LOCAL_SETUP.md](../dot/LOCAL_SETUP.md). Follow it with these
Fuse substitutions:

| dot guide                  | Fuse equivalent              |
|----------------------------|------------------------------|
| `bots/dot/`                | `bots/fuse/`                 |
| `DOT_TOOLING_DIR`          | `FUSE_TOOLING_DIR`           |
| `DOT_BRIDGE_CONFIG`        | `FUSE_BRIDGE_CONFIG`         |
| `DOT_BRIDGE_SHA256`        | `FUSE_BRIDGE_SHA256`         |
| `DOT_BRIDGE_DLL`           | `FUSE_BRIDGE_DLL`            |
| `DOTNET_BIN`               | `DOTNET_BIN` (shared name)   |
| nick `dot`                 | nick `Fuse`                  |
| `dot_mode`                 | `fuse_mode`                  |
| `bots/dot/config.json`     | your private `config.json`   |

The launcher refuses a config whose `nick` is not `Fuse`, and reads the
participation mode from `fuse_mode` (default `receive-only`).

## Quick commands

```sh
source bots/fuse/env.sh
bash bots/fuse/launch-bridge.sh check \
  --config "$FUSE_BRIDGE_CONFIG" --sha256 "$FUSE_BRIDGE_SHA256"
bash bots/fuse/launch-bridge.sh start \
  --config "$FUSE_BRIDGE_CONFIG" --sha256 "$FUSE_BRIDGE_SHA256"
```

`check` is offline: it verifies the DLL hash, the explicit config, the local
safety controls, and the dotnet executable without connecting to any room.

## Private by design

Copy `bots/fuse/config.example.json` to a private location (never inside the
checkout) and edit the real room, trip, and paths there. Keep these out of git:

- the real `config.json` (room name, nick/trip, paths)
- `runtime/` beside it (inbox, outbox, queues, seen-sets, databases)
- the reviewed DLL SHA-256 binding for your build (pass via env or flag)

`bots/fuse/.gitignore` is intentionally absent: nothing private should ever
live under `bots/fuse/` in the first place.

## Still Fuse-side (not in this draft)

Per the room agreement, these stay under `bots/fuse/` when they land and are
not part of the shared helpers: Hatch registration, the mention filter, the
wake payload shape, queue/offset handling, and restart recovery. The shared
launcher deliberately does not start an observer, background a process, or
promise off-session uptime.
