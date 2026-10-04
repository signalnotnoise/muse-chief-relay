# bots/shared — one implementation for the local bridge launchers

`env.sh`, `launch_bridge.py`, and `check_config.py` are the single
implementation behind every bot's local ChatBridge launcher. Each bot keeps a
thin wrapper (`bots/<name>/env.sh`, `bots/<name>/launch-bridge.sh`) that only
exports that bot's parameters, then delegates here. Bug fixes and safety
changes land once, in this directory.

## What is shared

- **Tooling/cache selection** (`env.sh`): private .NET tooling directory,
  `DOTNET_ROOT` preference, `DOTNET_CLI_HOME`, telemetry opt-out, NuGet/XDG
  cache paths. Parameterized by `BRIDGE_TOOLING_VAR`, the name of the env var
  holding the bot's private tooling dir (e.g. `DOT_TOOLING_DIR`).
- **Config validation** (`check_config.py`): endpoint, room, nick, no
  passwords/hooks, explicit `auto_ack`/`mentions` off, `base: runtime`,
  trip format, mode/outbox shape. The expected `nick` and the participation
  mode key are parameters (`nick='dot'`, `mode_key='dot_mode'` defaults).
- **Launch lifecycle** (`launch_bridge.py`): explicit `--config`, reviewed
  DLL SHA-256 pin, lowercase/unique JSON keys, ordinary `runtime/` directory,
  `os.umask(0o077)`, restricted child environment, mirror forced off, and
  `check`/`start`/`status`/`stop`/`outbox-status`/`outbox-resolve` forwarding
  to the selected dotnet host. `check` never executes .NET or contacts a room.

## Wrapper contract

A bot's `launch-bridge.sh` must export, then `exec python3` this file:

| Variable             | Meaning                                              |
|----------------------|------------------------------------------------------|
| `BRIDGE_CONFIG_ENV`  | env var holding the default `--config` path          |
| `BRIDGE_SHA256_ENV`  | env var holding the default expected DLL hash        |
| `BRIDGE_DLL_ENV`     | env var holding the default `--dll` path             |
| `BRIDGE_DLL_DEFAULT` | fallback DLL path (wrapper resolves it repo-relative)|
| `BRIDGE_DOTNET_ENV`  | env var holding the default `--dotnet` (else `DOTNET_BIN`) |
| `BRIDGE_NICK`        | nick the config must declare                         |
| `BRIDGE_MODE_KEY`    | participation mode key (`dot_mode`, `fuse_mode`, …)  |
| `BRIDGE_DOCS`        | docs path named in help/refusal text                 |

A bot's `env.sh` must set its `<PREFIX>_TOOLING_DIR` default, export
`BRIDGE_TOOLING_VAR=<PREFIX>_TOOLING_DIR`, then source `../shared/env.sh`.

## What stays per-bot (never in shared)

Nick/trip/room, real configs, `runtime/` state, Hatch registration, mention
filters, wake payloads, queue/offset handling, and recovery procedures. Shared
code never reads a bot's private config or state on its own; the wrapper hands
it an explicit `--config` every time.

## Tests

```sh
bash -n bots/shared/env.sh bots/dot/env.sh bots/fuse/env.sh \
  bots/dot/launch-bridge.sh bots/fuse/launch-bridge.sh
python3 -m unittest discover -s bots/dot -p 'test_*.py'      # dot's suites
python3 -m unittest discover -s bots/shared -p 'test_*.py'   # shared wrapper tests
```

Launcher tests use only temporary synthetic configs/DLLs and a fake dotnet
host: no real room, service, download, observer, or .NET execution.
