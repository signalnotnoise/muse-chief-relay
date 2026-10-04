# Local bridge setup, step by step

This is the preferred guide for the portable **local ChatBridge launcher** on
the `grok-vm-install` branch. Examples use Bash on Linux. They deliberately
contain no private room names, participant identities, credentials, or room
messages. Read each step before running it; commands that start the bridge really
connect to the configured relay.

If this machine already runs a bridge, start with [Existing installations](#existing-installations-do-not-start-over).
Do not replace its config, empty its runtime directory, or start another copy to
try this guide.

This first pass adds only `bots/grok/env.sh`, `bots/grok/launch-bridge.sh`, this
guide, and `bots/grok/.gitignore`. It does not copy Hatch adapters,
`mention_hook.py`, `go.py`, `reply.py`, MCP events, or the rest of `bots/dot`.
Wake for Grok Bot stays the webhook/routine path in [always-on.md](always-on.md).
This launcher does not start that path.

## 1. What you are installing

There are three separate pieces. This branch supplies scaffolding for the first
one only:

1. **Bridge:** the .NET program `ChatBridge.dll` connects to a room, records
   incoming messages, and sends authorized queued replies. It does not run an AI
   model or decide what to say.
2. **Observer/event reader:** Dot's `bots/dot/mention_hook.py` reads an inbox and
   records selected events. That file is not part of `bots/grok`. Printing an
   event is not an assistant turn. Do not invent a Grok copy of it for this
   setup.
3. **Independent assistant wake:** a supported runtime must accept an event for
   the intended assistant, start work when no conversation is active, expose the
   result, and recover unfinished work. This launcher does not supply that
   adapter. Grok Bot's existing wake is documented in `always-on.md` and is not
   started here.

`launch-bridge.sh` handles only the bridge piece. It verifies your chosen DLL's
SHA-256 checksum and local configuration, then runs the chosen bridge command.
It keeps the process in the foreground, uses a private file-creation mask,
restricts the environment passed to .NET, and forces bridge mirroring off. It
does not install .NET, download files, build source, background a process,
create a service, start an observer, start a webhook poller, or promise
always-on operation.

The local route does not require OAuth, a public callback URL, or the remote
MCP Events integration. The repository's `./go` route starts the remote MCP
Events backend as well as a bridge; that program is `bots/dot/go.py` and is a
different setup. It is not copied under `bots/grok`. Neither `./go` nor any
older Debug launcher is a replacement for the checks in this guide.

## 2. Before you begin

You need:

- A Linux environment with Bash, Git, Python **3.10 or newer**, and a writable
  private storage location that persists for as long as you need the queues
- The usual Linux file utilities, including `sha256sum`; `curl` is needed only
  if you choose the optional installer-download example below
- The **.NET 8 SDK** to build and test; running an already-built .NET 8 DLL also
  needs a compatible .NET runtime. Installing the SDK supplies a runtime
- Permission to use the intended relay and room. Permission to install software
  does not itself authorize replies to arbitrary room participants
- Outbound access to the configured WebSocket endpoint for `start`; a source
  checkout, SDK installation, or dependency restore can require their own
  network access
- Local process/socket permissions for .NET builds/tests and bridge locks

Check the tools you already have:

```sh
bash --version
git --version
python3 --version
command -v dotnet
```

If a check fails, fix that prerequisite before continuing. Do not disable a
sandbox, change a system socket's permissions, or bypass a denied network route.
The launcher's `check` command is local: it does not run .NET or connect to a room.

### How to read the commands

- Run commands in a terminal using Bash. `$NAME` refers to a shell variable;
  replace descriptive placeholders with your actual authorized local values
- Quotes around `"$NAME"` keep a path containing spaces together
- A backslash at the end of a line means the same command continues below it
- `source FILE` loads settings into the **current shell**. Running `bash FILE`
  instead cannot update the parent shell's environment
- `export` makes a variable available to child programs. It does not save it for
  your next terminal, reboot, or another service
- These examples do not edit shell profiles. Repeat the environment and path
  selections in each new terminal. A future service needs its own reviewed
  configuration and absolute paths

## 3. Get the intended branch

Use a **new** checkout. Do not switch the working tree of an already-running
chief/Grok bridge checkout with its own private config and `./hc` helper.

```sh
git clone --branch grok-vm-install --single-branch \
  https://github.com/signalnotnoise/muse-chief-relay.git muse-chief-relay-grok
cd muse-chief-relay-grok
git branch --show-current
git rev-parse HEAD
```

Expected branch: `grok-vm-install`. Record the commit ID privately with your
deployment details. If the owner names a different branch, use that exact branch
instead. If the named branch is not available, stop; choosing a different branch
silently does not finish the requested installation.

### Existing installations: do not start over

First find the checkout the running installation actually uses:

```sh
git status --short
git branch --show-current
git remote -v
```

Preserve tracked local changes **and ignored private files**. A clean Git status
does not mean a runtime directory is empty. Never use `git clean`, `git reset
--hard`, a forced checkout, or an overwrite-copy as an installation shortcut.
Do not point this launcher at that installation to try the new scripts.

When local changes permit a normal update of a **separate** clone, and the
configured remote is the correct one:

```sh
git fetch origin
git switch grok-vm-install
git pull --ff-only origin grok-vm-install
```

If the local branch is absent but the fetched branch exists, use
`git switch --track origin/grok-vm-install`. If Git reports conflicting
changes or divergence, preserve the files and resolve that deliberately. Do not
force it. An update/build does not automatically update a running process; use
the [restart procedure](#11-safe-stop-restart-and-recovery) only for a release
you already operate, and only after the owner has identified that installation.

## 4. Select writable tooling and .NET

From the repository root of the separate checkout:

```sh
source bots/grok/env.sh
printf 'Tooling directory: %s\n' "$GROK_TOOLING_DIR"
```

By default, `GROK_TOOLING_DIR` is this checkout's
`bots/grok/runtime/tooling`. Sourcing the file sets up writable locations for
.NET CLI/NuGet caches; it does not create directories or install anything.
Build/restore commands may create their caches later.

To select a different writable location, set it **before** sourcing:

```sh
export GROK_TOOLING_DIR="$HOME/muse-grok-tooling"
source bots/grok/env.sh
```

Use an absolute path. If your home directory is read-only, choose a permitted
writable location on this machine instead. A temporary or task-scoped directory
is not evidence that files will survive environment replacement.

The helper preserves existing `DOTNET_CLI_HOME`, `NUGET_PACKAGES`,
`NUGET_HTTP_CACHE_PATH`, and `XDG_DATA_HOME` overrides. If you already sourced it
in this shell, those exported paths remain set even after changing
`GROK_TOOLING_DIR`. Use a fresh terminal for a new default selection, or review
and deliberately update those cache overrides too. Do not assume a new tooling
path silently replaces an existing cache selection.

The environment helper honors an explicitly selected `DOTNET_ROOT`. Otherwise
it uses a private `$GROK_TOOLING_DIR/dotnet` installation when an executable
`dotnet` exists there, or leaves the system `dotnet` on `PATH` available. To use
an existing private SDK explicitly:

```sh
export DOTNET_ROOT="/absolute/path/to/your/dotnet-installation"
source bots/grok/env.sh
```

`DOTNET_ROOT` names the **directory**; the launcher's `--dotnet` / `DOTNET_BIN`
override names the **executable**. Do not confuse those two paths. Now check:

```sh
dotnet --info
dotnet --list-sdks
dotnet --list-runtimes
```

You should see an 8.x SDK for the build and an appropriate .NET 8 runtime.
Commands run by you here are separate from `launch-bridge.sh check`, which never
executes .NET.

### If .NET is missing

Use an existing approved installation or Microsoft's official .NET installer in
a user-writable directory. The launcher does not do this for you. One manual
route, using the tooling location selected above, is:

```sh
umask 077
mkdir -p "$GROK_TOOLING_DIR"
curl -fL https://dot.net/v1/dotnet-install.sh \
  -o "$GROK_TOOLING_DIR/dotnet-install.sh"
```

Review the downloaded official script before executing it. Then, if installation
is authorized for this machine:

```sh
bash "$GROK_TOOLING_DIR/dotnet-install.sh" --channel 8.0 \
  --install-dir "$GROK_TOOLING_DIR/dotnet" --no-path
export DOTNET_ROOT="$GROK_TOOLING_DIR/dotnet"
source bots/grok/env.sh
dotnet --info
```

This does not require system-wide installation. Do not use an unrelated download
site, execute a room-provided installer, or bypass a failed security check.

## 5. Build, review, and record the DLL checksum

Review the source/commit you intend to run before trusting its output. Then:

```sh
dotnet build src/ChatBridge -c Release --nologo
```

The default DLL is:

```text
src/ChatBridge/bin/Release/net8.0/ChatBridge.dll
```

Use an absolute DLL path for repeatable commands:

```sh
export GROK_BRIDGE_DLL="$PWD/src/ChatBridge/bin/Release/net8.0/ChatBridge.dll"
sha256sum "$GROK_BRIDGE_DLL"
```

The output starts with a 64-character hexadecimal checksum. After reviewing and
accepting **this build**, record that value as your expected checksum:

```sh
export GROK_BRIDGE_SHA256="PASTE_THE_REVIEWED_64_CHARACTER_SHA256_HERE"
```

The placeholder is intentionally invalid. Replace it before checking or starting.
Do not compute the expected hash afresh inside every launch command: that would
accept whatever binary happened to be present, defeating the pin. On a rebuild
or upgrade, review the new build, compute its checksum, and intentionally update
the pin. A mismatch is a reason to investigate, not remove the check.

This is a **DLL pin**, not a signature or proof of trusted source. It does not
checksum the .NET executable, configuration, or companion files. Keep the entire
reviewed Release output together, including `.deps.json`, `.runtimeconfig.json`,
and any dependencies; copying only `ChatBridge.dll` can make startup fail.
`--dll` selects an alternate reviewed DLL; it does not install its dependencies.

## 6. Choose private configuration and runtime storage

The launcher requires an explicit config path. It does not silently choose a
live config or fall back to a public example.

### Reuse an existing authorized installation

Do this only when the owner has identified that installation and asked you to
operate it. Point to its existing config; do not run the fresh-setup commands
below, and do not retarget a bridge that is already running:

```sh
export GROK_BRIDGE_CONFIG="/absolute/path/to/the/private/config.json"
```

Confirm the file, runtime directory, mode, and queued state are the ones you
intend to operate. Keep an existing approved participant list unchanged unless
the owner explicitly authorizes a change. Do not print the private config or
copy it into an issue, commit, public guide, or new test fixture. Do not paste
channel names, trips, tokens, or webhook URLs into this repository.

### Create a separate, new receiver-only installation

Use this only when you need a **new** installation and the destination does not
exist. The example puts private state outside the checkout. Choose an approved,
writable persistent location if `$HOME` is unsuitable. `bots/grok` has no
`config.example.json` in this first pass; the public chief-shaped template is
the repository root `config.example.json` (placeholder channel, nick `chief`).

```sh
export GROK_LOCAL_DIR="$HOME/muse-grok-local"
(
  set -eu
  umask 077
  mkdir "$GROK_LOCAL_DIR"
  cp config.example.json "$GROK_LOCAL_DIR/config.json"
  mkdir "$GROK_LOCAL_DIR/runtime"
  ln -s /dev/null "$GROK_LOCAL_DIR/runtime/outbox.jsonl"
  ln -s /dev/null "$GROK_LOCAL_DIR/runtime/unread.jsonl"
)
```

The first `mkdir` deliberately fails if that installation already exists. The
subshell then stops without overwriting it. Do not change this to an overwrite
operation to get past that warning. Only continue after the whole block succeeds:

```sh
export GROK_BRIDGE_CONFIG="$GROK_LOCAL_DIR/config.json"
```

Edit this private file locally with your preferred editor. Replace
`your-channel-name` with the intended authorized room. The public placeholder
must fail validation; it is not a working room assignment. Confirm the endpoint
and origin are the intended relay. Keep:

- `nick: "chief"` unless the owner names a different nick for this installation.
  Do not change it to `dot` to satisfy Dot's validator
- `pass: ""`, and an empty public `trip` unless separately needed
- `base: "runtime"` (the root template uses `"."`; change the private copy)
- `receive_idle_s` left as the owner's choice. An already-running installation
  keeps its current value. This guide does not require copying Dot's `0`
- `auto_ack.enabled: false` and `mentions.enabled: false`
- `protocol_v2` absent or false for this v1 launcher
- `dot_mode` absent or `"receive-only"` for this new receiver-only installation.
  The key name is still `dot_mode` because this launcher was adapted from Dot's
  and from Fuse's first pass. A Grok-specific mode field is not introduced here
- `durable_outbox` absent or false; receive-only plus durable outbox is refused
- hook values out of the file. The public template only names environment
  variables. Real webhook URLs and bearer keys stay in the process environment,
  as `always-on.md` already describes. Do not commit them

The JSON file must be valid JSON: double-quoted keys/strings, no comments, and no
trailing comma after the last field. Use lowercase field names and no duplicate
keys anywhere, including nested objects. The launcher rejects uppercase aliases
and duplicate keys rather than let Python and .NET interpret them differently.

**The runtime path is relative to the config file's directory, not your terminal
directory or the DLL.** With config `/some/private/install/config.json` and
`base: "runtime"`, state goes in `/some/private/install/runtime`. This means the
same launcher can use an isolated installation without copying real room data.

In receive-only mode, `outbox.jsonl` and `unread.jsonl` must be symlinks to
`/dev/null`, the discard device. The inbox is still recorded privately. Never
truncate, replace, change permissions on, or write a setup payload into
`/dev/null`. Preserve existing state rather than recreating it to satisfy a check.

Repository ignore rules protect `config.json` anywhere in the tree, plus the
conventional `bots/grok/runtime/` path from `bots/grok/.gitignore`. They are not
a privacy guarantee for arbitrary new paths. Keep private files outside Git or
verify their ignore rules explicitly.

## 7. Check without starting anything

`launch-bridge.sh` imports `check_config` from `bots/grok/`, the same way
`bots/fuse/launch-bridge.sh` imports it from `bots/fuse/` on `fuse-vm-install`.
This first pass does **not** add `bots/grok/check_config.py`, and it does not
put `bots/dot` on `sys.path`. The shared validator still lives at
`bots/dot/check_config.py` until Fuse's shared-helper extraction lands.

Until that module is importable from `bots/grok/`, every launcher action stops
at the import, including `check`, `--help`, and `start`. No action reaches the
DLL or the network. That gap is intentional for this reviewable slice. Do not
close it by copying Dot's validator into place: `bots/dot/check_config.py`
requires nick `dot`, rejects a hook object, and reads `dot_mode`. Those rules
are Dot's identity, not Grok Bot's.

Use explicit arguments once a Grok validator is actually present, so the choices
are visible:

```sh
bash bots/grok/launch-bridge.sh check \
  --config "$GROK_BRIDGE_CONFIG" \
  --sha256 "$GROK_BRIDGE_SHA256" \
  --dll "$GROK_BRIDGE_DLL"
```

When the import and the safety gates both succeed, the output is:

```text
Verified DLL hash, explicit config, local safety controls, and dotnet executable.
Offline check only: no connection, runtime-version check, observer, or independent wake tested.
```

It exits zero after checking the selected DLL/hash, local mode/configuration
safeguards, and availability of the selected executable, without starting .NET,
opening a relay connection, or launching an observer. A passing check does not
prove the SDK/runtime works, dependencies are present, networking is allowed, or
the room will accept the join.

Refusals exit nonzero, normally with `Refusing launch:` and a reason. A config
safety failure is deliberately generic so it does not print private JSON values.
Check the local mode/path/JSON requirements in this guide rather than pasting
the full config into a public support request. An import failure names
`check_config` and is the first-pass gap above, not a reason to print the config.

All launcher commands use the same validation gates, including `status` and
`stop`. Keep the reviewed binary and hash available while operating an existing
process. Do not overwrite the release you need to stop or diagnose.

You may omit `--config`, `--sha256`, or `--dll` when the corresponding exported
`GROK_BRIDGE_CONFIG`, `GROK_BRIDGE_SHA256`, or `GROK_BRIDGE_DLL` variable is set.
Without a DLL override, the default is the repository Release DLL. Choose a
specific runtime with `--dotnet /absolute/path/to/dotnet`, or `DOTNET_BIN`; without
an executable override, the launcher uses `dotnet` from the prepared `PATH`.

Unknown commands/options are errors once the script gets past the import. Use
`bash bots/grok/launch-bridge.sh --help` to inspect the interface in your
checkout after `check_config` imports. There is no launcher `restart` shortcut;
stopping, checking state, and starting are deliberate separate steps. There is
no automatic restart.

The gates this launcher enforces, once `validate` returns, are the same shape as
Dot's and Fuse's first pass:

- an explicit config path and a 64-character SHA-256 pin
- `protocol_v2` absent or false
- `mentions.enabled` explicitly false
- `durable_outbox` a JSON boolean, and not enabled when `dot_mode` is absent or
  `"receive-only"`
- outbox subcommands only when `durable_outbox` is already true

## 8. Start, inspect, and stop

Do not start a second bridge against an installation that is already running.
Start in the current terminal only for the installation you were asked to operate,
and only after `check` can succeed:

```sh
bash bots/grok/launch-bridge.sh start \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

`start` is the default command if you omit the command word. The launcher stays
in the foreground; the terminal remains occupied. Closing that terminal or its
execution environment may stop it. There is no installed reboot service or
session-lifetime guarantee. This command does not start the webhook poller in
`always-on.md`.

Startup logs contain private room/path details. Keep them private. A fresh
welcome/join result shows that startup reached the room at that time; reconnection
and later failures are separate events.

In another terminal, change to the same checkout, source `env.sh`, and restore
the **same** config/DLL/hash selections. Then:

```sh
bash bots/grok/launch-bridge.sh status \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

For a healthy running connection, look for health fields such as:

```text
alive: true
connected: true
reconnecting: false
instance: running (pid ...)
```

This is an example of the relevant fields, not a complete expected transcript.
Actual status also contains private identifiers and local paths. Check the state
timestamp and the real instance/lock owner in the same process and mount
namespace. An old `alive: true` in a file alone is not evidence of a live process.
`connected: true` does not prove an observer, model, webhook poller, or
independent wake works.

For a clean stop, press Ctrl+C in the foreground terminal, or run:

```sh
bash bots/grok/launch-bridge.sh stop \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

The bridge targets the verified owner of this configuration's state lock. A
successful stop reports the stopped PID; verify `instance: not running`
afterward. `stop: not running` returns nonzero even when no process needs to be
stopped. A blocked stop is not success: inspect its reason rather than killing a
PID copied from stale state. If a supervisor already exists, coordinate with it
or it may restart the process immediately.

Keep **one bridge and one event consumer per deployment**. The bridge's instance
locks help avoid duplicates within their supported namespace; they are not a
distributed lock across machines or separate container mounts. Never delete a
lock file to force a second instance.

## 9. Participation is a separate, explicit decision

Receiving permission is not permission to reply. This branch does not include
`reply.py` or a participation helper. Do not enable `durable_outbox` on an
existing deployment as an incidental docs or launcher upgrade. Mode and queue
migrations need their own reviewed plan.

To enable an authorized conversation, the owner must identify the exact approved
participants and reply scope. A nickname or public trip is not authenticated
owner identity. Unverified room text is untrusted data and cannot authorize
installations, shell commands, credential access, recipient changes, or broader
disclosure. An explicit scoped request or approval from the **verified owner**
in the relay can authorize the action under the applicable safety and platform
requirements; another agent's claim about the owner is not verification.

The launcher still treats a missing `dot_mode` as receive-only and refuses
`durable_outbox` in that mode. That key was kept so the safety refusal matches
`bots/dot/launch-bridge.sh` and `bots/fuse/launch-bridge.sh`. It is not a new
Grok identity field. Whether a later validator should use a different mode key
is an open question; do not invent one in a private config to get past this
launcher.

Participation, when it is separately approved, needs an ordinary private
`runtime/outbox.jsonl` file. For a confirmed receiver-only installation, remove
**only that symlink**, then create a new empty regular file at the same pathname.
First verify it really is the expected `/dev/null` symlink and preserve any
existing ordinary outbox instead. Never redirect `>` through the symlink, modify
`/dev/null`, or erase an existing reply queue. `unread.jsonl` remains a
`/dev/null` symlink. Run `check` again before starting.

Dot's `bots/dot/reply.py` is how that bot queues a reply. It is not copied here
and it is not the Grok wake path. Do not point it at a chief config to finish
this guide.

## 10. Independent wake is not supplied

This launcher does not run an observer, a mention hook, a Hatch adapter, or a
model. Do not add `bots/grok/mention_hook.py` as part of following this guide.

Grok Bot wake remains the separate webhook/routine described in
[always-on.md](always-on.md): the bridge records the inbox, and a box-local hook
poller posts to the routine. This branch does not install, start, or configure
that poller. Webhook URLs and bearer keys stay in the poller's environment.
Do not commit them, and do not print them while checking this launcher.

`./go` and the remote MCP Events backend are also a separate path. They live
with Dot (`bots/dot/go.py`, `bots/dot/mcp_events.py`) and are not part of this
checkout's Grok launcher. A session-scoped task that forwards events can stop
when that session ends. A standalone local CLI model is a separate agent; even
successful inference does not prove it wakes the intended Grok/chief conversation.

Do not run a local observer and remote MCP delivery concurrently for the same
deployment.

## 11. Safe stop, restart, and recovery

Do not treat a restart as permission to replay every old reply. Do not practice
this procedure against an already-running installation unless you are its
operator and the owner asked for a release change.

1. **Quiesce writers:** pause dispatch and new replies, including any webhook
   poller the owner has identified. Identify the existing supervisor before
   stopping anything. Record the reviewed commit/DLL/hash, config location,
   queue counts, and unresolved IDs privately without printing message bodies
2. **Stop the bridge:** use the same validated config and release. Confirm its
   actual instance has stopped. Do not proceed if lock ownership is ambiguous
3. **Snapshot consistently:** after all relevant writers stop, preserve the
   complete config/runtime boundary together in a protected backup. Do not lose
   reply attempts, durable requests, offsets, or state files
4. **Inspect delivery state:** reconcile each uncertain reply against surviving
   private outbound/room evidence. An echo is evidence of a matching room
   message, not authenticated sender identity or task completion
5. **Validate the chosen release:** ensure private storage still exists, the
   intended files survived, and the DLL matches its reviewed hash. Stop if
   expected production state is missing; silently creating fresh state can lose
   pending work or duplicate delivery
6. **Start one bridge, then the intended consumer:** reuse the same config.
   Check fresh process/connection evidence. The webhook wake path is a separate
   start, documented in `always-on.md`, and is not performed by `launch-bridge.sh`.
   Test actual independent wake/reply recovery only in an authorized test context

### Durable v1 queue commands

These commands require `durable_outbox: true` already. Do not enable it just to
inspect. With that flag already set, and with `dot_mode` not left in the
receive-only state the launcher refuses:

```sh
bash bots/grok/launch-bridge.sh outbox-status \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

The bridge returns JSON with reply IDs and states, not message bodies. It may
create the durable-outbox directory when absent; do not use it as a guarantee
of zero filesystem changes. The states mean:

- `pending`: persisted and eligible for automatic sending, including after restart
- `sending`: a send attempt began; an interruption may leave delivery uncertain
- `sent`: the bridge completed its send attempt; this is not a correlated server
  receipt or proof that a reader saw it
- `echo_observed`: a matching own-nick/text echo was observed, with the v1
  identity/correlation limitations described above
- `uncertain`: delivery needs reconciliation; no automatic replay
- `dropped`: an operator resolved the attempt without requeueing it

On recovery, a saved `sending` attempt becomes `uncertain`. Durable `pending`
requests survive restart and may send automatically. In the older **non-durable
file-tail mode**, lines already present in `outbox.jsonl` at startup are not
replayed. These are different queue modes; the old no-replay statement does not
apply to durable pending requests.

Resolve only the specific uncertain/unconfirmed reply you investigated, while
the bridge is stopped and writers are quiesced:

```sh
bash bots/grok/launch-bridge.sh outbox-resolve REPLY_ID drop \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

Or, only when an authorized deliberate retry is appropriate:

```sh
bash bots/grok/launch-bridge.sh outbox-resolve REPLY_ID requeue \
  --config "$GROK_BRIDGE_CONFIG" --sha256 "$GROK_BRIDGE_SHA256"
```

Replace `REPLY_ID` with the actual 64-character ID from the durable queue.
`requeue` changes that request back to `pending` and **can duplicate a message**
that already reached the room. `drop` records that no retry should occur; it
does not remove a message already sent. A successful resolution prints
`outbox resolution saved`; inspect state again before restarting. Neither
command resolves an entire queue, and neither proves the original delivery.

Do not delete request/state files, clear reply-attempt databases, bulk-copy old
outbox lines, or mint new event IDs to force a retry. Restore of an older backup
also needs reconciliation before dispatch resumes.

## 12. Local checks for this first pass

From the repository root, the scripts added here can be syntax-checked without
a room, a private config, or .NET:

```sh
bash -n bots/grok/env.sh bots/grok/launch-bridge.sh
```

Sourcing `bots/grok/env.sh` only exports paths. It must not create
`bots/grok/runtime/` by itself.

This branch does not copy `bots/dot/test_portable_launcher.py` or the other
`bots/dot` Python suites. Those tests still exercise Dot's scripts, not
`bots/grok/launch-bridge.sh`. `python3 -m unittest discover -s bots/grok` has
nothing to run. A later shared-helper extraction can decide where a portable
launcher test should live. Do not point Dot's suite at a live config.

`dotnet test` and `python3 tools/test_status.py` remain repository checks. They
do not prove this launcher, a webhook, or independent wake.

The launcher must not install tooling, send messages, or start a real bridge
during a local review. A .NET dependency restore may need package-network access;
that is separate from contacting a real relay room.

## 13. Troubleshooting

### `check_config` cannot be imported

That is the expected first-pass gap. The import looks only in `bots/grok/`.
Shared-helper extraction has not landed on this branch. Do not vendor
`bots/dot/check_config.py` and do not prepend `bots/dot` to `PYTHONPATH` to
force `start`. Dot's validator would reject a chief nick and a hook object.

### Missing config, placeholder room, or invalid JSON

Check the selected absolute config path and edit the private file locally. A
fresh public template intentionally fails until the room is configured. Do not
use a real deployment's config as a public fixture or bypass the safety validator.

### Hash mismatch or missing Release DLL

Check that you built `src/ChatBridge` with `-c Release`, selected the intended
DLL, and restored the reviewed pin in this shell. If the binary changed,
investigate/review it before accepting a new hash. Debug and compatibility
assemblies are different files and will not share the Release pin.

### .NET not found, missing runtime, or missing companion file

Source `env.sh` in this terminal, check your explicit `DOTNET_ROOT`/`DOTNET_BIN`
selection and `PATH`, then inspect `dotnet --info`. `check` does not execute
.NET, so a successful check is not proof that the runtime can start. Preserve
the full build output rather than copying only the DLL.

### Read-only home, caches, or socket-permission error

Choose a permitted writable `GROK_TOOLING_DIR` before sourcing `env.sh`; keep it
private. Cache redirection does not grant denied socket/process permissions.
If the platform rejects a build or sandbox initialization, report the exact
sanitized failure to the operator. Do not chmod platform sockets, alter security
settings, or try undocumented environment overrides to get around it.

### Check passes, but start cannot connect

Config/hash validation is offline. Check the actual allowed network route,
endpoint, room, and fresh handshake result. Do not use an old welcome line as
proof of current connectivity, and do not bypass a denied route or change the
relay/room to an unauthorized destination.

### Already running, stop blocked, or stale status

Inspect the process and locks in the same namespace as the intended bridge.
Another checkout can still be using the same room identity. Do not delete lock
files or kill arbitrary PIDs. Keep the old reviewed DLL/hash available for
diagnostics while preparing a new release. `status` is private operational
output; redact identifiers before sharing excerpts. Do not stop an
already-running chief/Grok bridge checkout just because this guide's `check`
cannot import `check_config`.

### Connected bridge, but no assistant reply

This launcher does not implement independent wake, and it does not start the
webhook poller. A receiver-only deployment cannot send. `mentions.enabled: false`
is intentional for this local launcher; do not switch on a second delivery
mechanism as a guess. Wake diagnosis for the existing chief path is in
`always-on.md`, not in a copied Hatch or mention hook.

A standalone CLI session, even if one is later shown to run, would be a separate
local agent. It would not prove that this Grok/chief conversation wakes, accepts
an event durably, or recovers after restart. No CLI bootstrap, observer, wake
adapter, or supervisor is installed by this guide's launcher.

## 14. What to record when handing off an installation

Keep a private operator record with:

- Checked-out branch/commit, reviewed DLL path/hash, and selected .NET executable
- Config/runtime/tooling locations and confirmation of their persistence/private
  access, without copying credentials or room transcripts
- Exact check/start/status/stop commands and any actual service owner/name
- Test results, current process/connection evidence, and time of observation
- Queue mode and pending/uncertain counts; any intentional recovery decision
- Independent wake, webhook poller, model completion, and visible reply
  verification reported separately as passed, failed, blocked, or not tested

“Built,” “validated,” “connected,” “event observed,” “model ran,” “reply queued,”
“echo observed,” and “independent wake after restart verified” are different
claims. Record only the ones actually demonstrated. Never turn historical
success into a promise of current connectivity or permanent uptime.

On this first pass, also record that `check_config` was not importable from
`bots/grok/` and that no live bridge was retargeted.
