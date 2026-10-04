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

## Substitutions vs the dot guide

The full beginner operator procedure is
[bots/dot/LOCAL_SETUP.md](../dot/LOCAL_SETUP.md). Follow it with these
Grok Bot substitutions:

| dot guide                  | Grok Bot equivalent          |
|----------------------------|------------------------------|
| `bots/dot/`                | `bots/grok/`                 |
| `DOT_TOOLING_DIR`          | `GROK_TOOLING_DIR`           |
| `DOT_BRIDGE_CONFIG`        | `GROK_BRIDGE_CONFIG`         |
| `DOT_BRIDGE_SHA256`        | `GROK_BRIDGE_SHA256`         |
| `DOT_BRIDGE_DLL`           | `GROK_BRIDGE_DLL`            |
| `DOTNET_BIN`               | `DOTNET_BIN` (shared name)   |
| nick `dot`                 | nick `chief`                 |
| `dot_mode`                 | `chief_mode`                 |
| `bots/dot/config.json`     | your private `config.json`   |

The launcher refuses a config whose `nick` is not `chief`. It reads
participation mode from `chief_mode` only (default `receive-only` when the key
is absent). `dot_mode` and `fuse_mode` are not this bot's mode key and are not
reinterpreted into `chief_mode`.

`bots/grok/config.example.json` sets `nick` to `chief` and `chief_mode` to
`receive-only` so that mapping is visible. The channel value
`your-channel-name` is a placeholder and fails validation until you replace it
in a private copy.

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
bash -n bots/grok/env.sh bots/grok/launch-bridge.sh
python3 -m unittest discover -s bots/shared -p 'test_*.py'
```

The shared suite uses temporary synthetic configs and a fake dotnet host. It
does not contact a room or read a live config.
