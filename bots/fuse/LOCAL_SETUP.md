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

## What to reuse from the dot guide

Tooling, the Release build, a fresh receive-only config, and bridge lifecycle
commands are written up in
[bots/dot/LOCAL_SETUP.md](../dot/LOCAL_SETUP.md). Follow only the sections
named below, and apply the substitution table only inside those sections.

This directory has `env.sh`, `launch-bridge.sh`, and `config.example.json`.
It has no `reply.py`, `mention_hook.py`, `go.py`, `run.sh`, or `test_*.py`.
Dot's `reply.py` accepts nick `dot` only, so it cannot send as `Fuse`.

### Checkout branch

These wrappers are on `grok-vm-install`, stacked on `codex/dot-vm-install`.
The Dot guide's clone and update commands name `codex/dot-vm-install`, which
does not contain `bots/fuse/`. In
[§3 Get the intended branch](../dot/LOCAL_SETUP.md#3-get-the-intended-branch),
use `grok-vm-install` for `git clone --branch`, `git switch`, and
`git pull --ff-only`. Expected `git branch --show-current`: `grok-vm-install`.
If that branch is absent, stop.

### Sections to follow

| Dot section | Use it for |
|-------------|------------|
| [§2 Before you begin](../dot/LOCAL_SETUP.md#2-before-you-begin) | Tooling prerequisites. No path rewrite. |
| [§4 Select writable tooling and .NET](../dot/LOCAL_SETUP.md#4-select-writable-tooling-and-net) | Tooling directory and .NET selection. |
| [§5 Build, review, and record the DLL checksum](../dot/LOCAL_SETUP.md#5-build-review-and-record-the-dll-checksum) | Release build and DLL pin. `src/ChatBridge/bin/Release/net8.0/ChatBridge.dll` stays at that shared path. |
| [§6 Create a separate, new receiver-only installation](../dot/LOCAL_SETUP.md#create-a-separate-new-receiver-only-installation) | Fresh receive-only config only. |
| [§7 Check without starting anything](../dot/LOCAL_SETUP.md#7-check-without-starting-anything) | Offline `check`. |
| [§8 Start, inspect, and stop](../dot/LOCAL_SETUP.md#8-start-inspect-and-stop) | `start`, `status`, and `stop`. |
| [§11 Safe stop, restart, and recovery](../dot/LOCAL_SETUP.md#11-safe-stop-restart-and-recovery) | Bridge stop, release check, and one-bridge restart. `outbox-status` and `outbox-resolve` take the same flag substitutions. Keep durable outbox off on a fresh receive-only install. |
| [§13 Troubleshooting](../dot/LOCAL_SETUP.md#13-troubleshooting) | Bridge failures: missing config, hash or DLL, .NET, writable tooling, connect, already running. |

### Substitutions inside those sections

| Dot guide | Fuse |
|-----------|------|
| `codex/dot-vm-install` | `grok-vm-install` |
| `source bots/dot/env.sh` | `source bots/fuse/env.sh` |
| `bash bots/dot/launch-bridge.sh` | `bash bots/fuse/launch-bridge.sh` |
| `cp bots/dot/config.example.json` | `cp bots/fuse/config.example.json` |
| `DOT_TOOLING_DIR` | `FUSE_TOOLING_DIR` |
| `DOT_BRIDGE_CONFIG` | `FUSE_BRIDGE_CONFIG` |
| `DOT_BRIDGE_SHA256` | `FUSE_BRIDGE_SHA256` |
| `DOT_BRIDGE_DLL` | `FUSE_BRIDGE_DLL` |
| `DOT_LOCAL_DIR` | `FUSE_LOCAL_DIR` |
| `$HOME/muse-dot-tooling` | `$HOME/muse-fuse-tooling` |
| `$HOME/muse-dot-local` | `$HOME/muse-fuse-local` |
| `DOTNET_BIN` | `DOTNET_BIN` (same name) |
| nick `dot` | nick `Fuse` |
| `dot_mode` | `fuse_mode` |

The launcher refuses a config whose `nick` is not `Fuse`. It reads
participation mode from `fuse_mode` only (default `receive-only` when the key
is absent). In the fresh-config section, keep `nick` `Fuse` and `fuse_mode`
absent or `"receive-only"`.

§4's default tooling path `bots/dot/runtime/tooling` is
`bots/fuse/runtime/tooling` here. §6's ignore-rule sentences name Dot's
`config.json` and `runtime/` paths; this bot's rules are in
[Private by design](#private-by-design).

### Sections to skip

Leave these Dot-only. Rewriting `bots/dot/` to `bots/fuse/` in them points at
files that do not exist:

- [§1 What you are installing](../dot/LOCAL_SETUP.md#1-what-you-are-installing): the observer (`mention_hook.py`), independent wake, `./go`, `bots/dot/go.py`, and `bots/dot/run.sh`. This launcher is the bridge only.
- [§6 Reuse an existing authorized installation](../dot/LOCAL_SETUP.md#reuse-an-existing-authorized-installation): that example is `bots/dot/config.json`. Use the fresh receive-only subsection.
- [§9 Participation](../dot/LOCAL_SETUP.md#9-participation-is-a-separate-explicit-decision): `bots/dot/reply.py`, `dot_mode`, and approved recipients.
- [§10 Observer setup](../dot/LOCAL_SETUP.md#10-observer-setup-is-not-independent-wake): `bots/dot/mention_hook.py`.
- [§11](../dot/LOCAL_SETUP.md#11-safe-stop-restart-and-recovery) observer, adapter, mention-cursor, and SQLite sentences.
- [§12 Run local tests](../dot/LOCAL_SETUP.md#12-run-local-tests-without-using-a-real-room): `test_setup.py`, `test_mention_hook.py`, `test_participation.py`, `test_hatch_adapter.py`, `test_portable_launcher.py`, and `unittest discover -s bots/dot`. Wrapper checks are in [bots/shared/README.md](../shared/README.md).
- [§13](../dot/LOCAL_SETUP.md#13-troubleshooting) "Connected bridge, but no assistant reply" and "Local Codex initialization finding".
- [§14](../dot/LOCAL_SETUP.md#14-what-to-record-when-handing-off-an-installation) independent-wake, model, and reply-verification claims. Record the `grok-vm-install` commit, DLL hash, private config location, and the check/start/status/stop commands you actually ran.

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

`bots/fuse/.gitignore` protects the single exception to the nothing-private-in-
the-checkout rule: `runtime/`, the `env.sh` tooling default (dotnet CLI home,
NuGet caches, XDG data). It is ignore-protected and never committed. Everything
else private — the real `config.json`, the DLL SHA-256 binding — still belongs
in a private location outside the checkout.

## Still Fuse-side (not in this draft)

Per the room agreement, these stay under `bots/fuse/` when they land and are
not part of the shared helpers: Hatch registration, the mention filter, the
wake payload shape, queue/offset handling, and restart recovery. The shared
launcher deliberately does not start an observer, background a process, or
promise off-session uptime.
