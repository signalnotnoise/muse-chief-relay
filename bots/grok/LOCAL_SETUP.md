# Grok Bot local bridge setup

Thin Grok Bot adaptation of the shared hash-pinned ChatBridge launcher. The
implementation lives in `bots/shared/`; this directory holds only Grok Bot's
private wiring. Nothing here installs software, creates a service, starts an
observer, or registers an independent wake adapter.

The folder is `bots/grok`. The bridge nick is **`chief`**. Do not guess `Grok`,
`dot`, or `Fuse`. `launch-bridge.sh` exports `BRIDGE_NICK=chief` and
`BRIDGE_MODE_KEY=chief_mode`.

## The one implementation

`bots/shared/launch_bridge.py` performs the DLL hash pin, explicit-config
selection, safety validation, private umask, restricted child environment, and
lifecycle command forwarding. `bots/shared/env.sh` selects the private .NET
tooling/cache paths. `bots/shared/check_config.py` validates the config. See
[bots/shared/README.md](../shared/README.md) for the wrapper contract.

`bots/grok/env.sh` and `bots/grok/launch-bridge.sh` are thin wrappers. They
export Grok Bot's parameters, then delegate to the shared implementation.
There is no `bots/grok/check_config.py`.

## What to reuse from the dot guide

Tooling, the Release build, a fresh receive-only config, and bridge lifecycle
commands are written up in
[bots/dot/LOCAL_SETUP.md](../dot/LOCAL_SETUP.md). Follow only the sections
named below, and apply the substitution table only inside those sections.

This directory has `env.sh`, `launch-bridge.sh`, and `config.example.json`.
It has no `reply.py`, `mention_hook.py`, `go.py`, `run.sh`, or `test_*.py`.
Dot's `reply.py` accepts nick `dot` only, so it cannot send as `chief`.

### Checkout branch

`grok-vm-install` is stacked on `codex/dot-vm-install`. Use a separate checkout
of `grok-vm-install`. The Dot guide's clone and update commands name
`codex/dot-vm-install`, which does not contain `bots/grok/`. Leave an
already-running chief/Grok bridge checkout, with its private config and `./hc`
helper, on its own branch.

In [§3 Get the intended branch](../dot/LOCAL_SETUP.md#3-get-the-intended-branch),
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

| Dot guide | Grok Bot |
|-----------|----------|
| `codex/dot-vm-install` | `grok-vm-install` |
| `source bots/dot/env.sh` | `source bots/grok/env.sh` |
| `bash bots/dot/launch-bridge.sh` | `bash bots/grok/launch-bridge.sh` |
| `cp bots/dot/config.example.json` | `cp bots/grok/config.example.json` |
| `DOT_TOOLING_DIR` | `GROK_TOOLING_DIR` |
| `DOT_BRIDGE_CONFIG` | `GROK_BRIDGE_CONFIG` |
| `DOT_BRIDGE_SHA256` | `GROK_BRIDGE_SHA256` |
| `DOT_BRIDGE_DLL` | `GROK_BRIDGE_DLL` |
| `DOT_LOCAL_DIR` | `GROK_LOCAL_DIR` |
| `$HOME/muse-dot-tooling` | `$HOME/muse-chief-tooling` |
| `$HOME/muse-dot-local` | `$HOME/muse-chief-local` |
| `DOTNET_BIN` | `DOTNET_BIN` (same name) |
| nick `dot` | nick `chief` |
| `dot_mode` | `chief_mode` |

The launcher refuses a config whose `nick` is not `chief`. It reads
participation mode from `chief_mode` only (default `receive-only` when the key
is absent). `dot_mode` and `fuse_mode` are not this bot's mode key and are not
reinterpreted into `chief_mode`. In the fresh-config section, keep `nick`
`chief` and `chief_mode` absent or `"receive-only"`.

§4's default tooling path `bots/dot/runtime/tooling` is
`bots/grok/runtime/tooling` here. §6's ignore-rule sentences name Dot's
`config.json` and `runtime/` paths; this bot's rules are in
[Private by design](#private-by-design).

`bots/grok/config.example.json` sets `nick` to `chief` and `chief_mode` to
`receive-only` so that mapping is visible. The channel value
`your-channel-name` is a placeholder and fails validation until you replace it
in a private copy.

### Sections to skip

Leave these Dot-only. Rewriting `bots/dot/` to `bots/grok/` in them points at
files that do not exist:

- [§1 What you are installing](../dot/LOCAL_SETUP.md#1-what-you-are-installing): the observer (`mention_hook.py`), independent wake, `./go`, `bots/dot/go.py`, and `bots/dot/run.sh`. This launcher is the bridge only.
- [§6 Reuse an existing authorized installation](../dot/LOCAL_SETUP.md#reuse-an-existing-authorized-installation): that example is `bots/dot/config.json`. A live chief config with a hook block fails `check`. Use the fresh receive-only subsection.
- [§9 Participation](../dot/LOCAL_SETUP.md#9-participation-is-a-separate-explicit-decision): `bots/dot/reply.py`, `dot_mode`, and approved recipients.
- [§10 Observer setup](../dot/LOCAL_SETUP.md#10-observer-setup-is-not-independent-wake): `bots/dot/mention_hook.py`.
- [§11](../dot/LOCAL_SETUP.md#11-safe-stop-restart-and-recovery) observer, adapter, mention-cursor, and SQLite sentences.
- [§12 Run local tests](../dot/LOCAL_SETUP.md#12-run-local-tests-without-using-a-real-room): `test_setup.py`, `test_mention_hook.py`, `test_participation.py`, `test_hatch_adapter.py`, `test_portable_launcher.py`, and `unittest discover -s bots/dot`. Grok Bot's checks are [below](#local-checks).
- [§13](../dot/LOCAL_SETUP.md#13-troubleshooting) "Connected bridge, but no assistant reply" and "Local Codex initialization finding".
- [§14](../dot/LOCAL_SETUP.md#14-what-to-record-when-handing-off-an-installation) independent-wake, model, and reply-verification claims. Record the `grok-vm-install` commit, DLL hash, private config location, and the check/start/status/stop commands you actually ran.

## Quick commands

Use a separate checkout of `grok-vm-install`. Do not switch or retarget an
already-running chief/Grok bridge checkout with its own private config and
`./hc` helper.

```sh
source bots/grok/env.sh
bash bots/grok/launch-bridge.sh check \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
bash bots/grok/launch-bridge.sh start \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

`check` is offline: it verifies the DLL hash, the explicit config, the local
safety controls, and the dotnet executable without connecting to any room.
There is no automatic restart. `start` really connects; do not run it against
an installation that is already up.

## Private by design

Copy `bots/grok/config.example.json` to a private location (never inside the
checkout) and edit the real room there. Keep these out of git:

- the real `config.json` (room name, nick/trip, paths)
- `runtime/` beside it (inbox, outbox, queues, seen-sets, databases)
- the reviewed DLL SHA-256 binding for your build (pass via env or flag)

`bots/grok/.gitignore` ignores `config.json` and `runtime/` under this
directory. `runtime/` is the `env.sh` tooling default (dotnet CLI home, NuGet
caches, XDG data). Ignore rules are not a privacy guarantee for arbitrary new
paths. The root repository ignore also ignores `config.json`. Keep the real
file outside the checkout anyway.

The public example has an empty `pass`, an empty `trip`, and no credentials.
Do not commit room names, trips, tokens, or webhook URLs.

## Hook-bearing live config is a separate shape

The shared validator refuses a config when `hook` is present
(`hook is not None`), including a block that only names environment variables.
That refusal is unchanged. This launcher does not strip it and does not
reinterpret a live config so the refusal passes.

Grok Bot's existing wake path in [always-on.md](always-on.md) uses a hook
block on the box. A copy of that live config will fail `check` for that
reason, and for nick/mode/base mismatches if those differ from this example.
`bots/grok/config.example.json` is hook-free and receive-only, like Fuse's
example. Leave the live checkout alone. Webhook URLs and bearer keys stay in
the poller's environment.

`./go` and the remote MCP Events path stay with Dot (`bots/dot/go.py`). They
are not this launcher. Independent wake is not supplied here.

## Still Grok-side (not in this draft)

These stay outside the shared helpers: the webhook/routine wake in
`always-on.md`, Hatch registration, mention filters, queue/offset handling,
and restart recovery. The shared launcher deliberately does not start an
observer, a hook poller, a background process, or promise off-session uptime.

## Local checks

```sh
# bash -n checks one script. Extra filenames become positional parameters.
for script in bots/grok/env.sh bots/grok/launch-bridge.sh; do
  bash -n "$script"
done
python3 -m unittest discover -s bots/shared -p 'test_*.py'
```

The shared suite uses temporary synthetic configs and a fake dotnet host. It
does not contact a room or read a live config.
