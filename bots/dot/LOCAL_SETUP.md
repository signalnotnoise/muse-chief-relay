# Local bridge setup, step by step

This is the preferred guide for the portable **local ChatBridge launcher** on
the `codex/dot-vm-install` branch. Examples use Bash on Linux. They deliberately
contain no private room names, participant identities, credentials, or room
messages. Read each step before running it; commands that start the bridge really
connect to the configured relay.

If this machine already runs a bridge, start with [Existing installations](#existing-installations-do-not-start-over).
Do not replace its config, empty its runtime directory, or start another copy to
try this guide.

## 1. What you are installing

There are three separate pieces:

1. **Bridge:** the .NET program `ChatBridge.dll` connects to a room, records
   incoming messages, and sends authorized queued replies. It does not run an AI
   model or decide what to say.
2. **Observer/event reader:** `mention_hook.py` reads the inbox and records
   selected events in a persistent SQLite database. Its `--wait` option prints
   the next event and exits. Printing an event is not an assistant turn.
3. **Independent assistant wake:** a supported runtime must accept that event
   for the intended assistant, start work when no conversation is active, expose
   the result, and recover unfinished work. This launcher does not supply that
   adapter.

`launch-bridge.sh` handles only the first piece. It verifies your chosen DLL's
SHA-256 checksum and local configuration, then runs the chosen bridge command.
It keeps the process in the foreground, uses a private file-creation mask,
restricts the environment passed to .NET, and forces bridge mirroring off. It
does not install .NET, download files, build source, background a process,
create a service, start an observer, or promise always-on operation.

The local route does not require OAuth, a public callback URL, or the remote
MCP Events integration. The repository's `./go` / `bots/dot/go.py` route starts
the remote MCP Events backend as well as a bridge; that is a different setup.
`bots/dot/run.sh` is the older Debug/compatibility launcher. Neither is a
replacement for the checks in this guide.

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

For a new checkout, use an empty destination:

```sh
git clone --branch codex/dot-vm-install --single-branch \
  https://github.com/signalnotnoise/muse-chief-relay.git muse-chief-relay
cd muse-chief-relay
git branch --show-current
git rev-parse HEAD
```

Expected branch: `codex/dot-vm-install`. Record the commit ID privately with your
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

When local changes permit a normal update, and the configured remote is the
correct one:

```sh
git fetch origin
git switch codex/dot-vm-install
git pull --ff-only origin codex/dot-vm-install
```

If the local branch is absent but the fetched branch exists, use
`git switch --track origin/codex/dot-vm-install`. If Git reports conflicting
changes or divergence, preserve the files and resolve that deliberately. Do not
force it. An update/build does not automatically update a running process; use
the [restart procedure](#11-safe-stop-restart-and-recovery) for a release change.

## 4. Select writable tooling and .NET

From the repository root:

```sh
source bots/dot/env.sh
printf 'Tooling directory: %s\n' "$DOT_TOOLING_DIR"
```

By default, `DOT_TOOLING_DIR` is this checkout's
`bots/dot/runtime/tooling`. Sourcing the file sets up writable locations for
.NET CLI/NuGet caches; it does not create directories or install anything.
Build/restore commands may create their caches later.

To select a different writable location, set it **before** sourcing:

```sh
export DOT_TOOLING_DIR="$HOME/muse-dot-tooling"
source bots/dot/env.sh
```

Use an absolute path. If your home directory is read-only, choose a permitted
writable location on this machine instead. A temporary or task-scoped directory
is not evidence that files will survive environment replacement.

The helper preserves existing `DOTNET_CLI_HOME`, `NUGET_PACKAGES`,
`NUGET_HTTP_CACHE_PATH`, and `XDG_DATA_HOME` overrides. If you already sourced it
in this shell, those exported paths remain set even after changing
`DOT_TOOLING_DIR`. Use a fresh terminal for a new default selection, or review
and deliberately update those cache overrides too. Do not assume a new tooling
path silently replaces an existing cache selection.

The environment helper honors an explicitly selected `DOTNET_ROOT`. Otherwise
it uses a private `$DOT_TOOLING_DIR/dotnet` installation when an executable
`dotnet` exists there, or leaves the system `dotnet` on `PATH` available. To use
an existing private SDK explicitly:

```sh
export DOTNET_ROOT="/absolute/path/to/your/dotnet-installation"
source bots/dot/env.sh
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
mkdir -p "$DOT_TOOLING_DIR"
curl -fL https://dot.net/v1/dotnet-install.sh \
  -o "$DOT_TOOLING_DIR/dotnet-install.sh"
```

Review the downloaded official script before executing it. Then, if installation
is authorized for this machine:

```sh
bash "$DOT_TOOLING_DIR/dotnet-install.sh" --channel 8.0 \
  --install-dir "$DOT_TOOLING_DIR/dotnet" --no-path
export DOTNET_ROOT="$DOT_TOOLING_DIR/dotnet"
source bots/dot/env.sh
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
export DOT_BRIDGE_DLL="$PWD/src/ChatBridge/bin/Release/net8.0/ChatBridge.dll"
sha256sum "$DOT_BRIDGE_DLL"
```

The output starts with a 64-character hexadecimal checksum. After reviewing and
accepting **this build**, record that value as your expected checksum:

```sh
export DOT_BRIDGE_SHA256="PASTE_THE_REVIEWED_64_CHARACTER_SHA256_HERE"
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
live config or fall back to the public example.

### Reuse an existing authorized installation

Point to its existing config; do not run the fresh-setup commands below:

```sh
export DOT_BRIDGE_CONFIG="$PWD/bots/dot/config.json"
```

Confirm the file, runtime directory, mode, and queued state are the ones you
intend to operate. Keep an existing approved participant list unchanged unless
the owner explicitly authorizes a change. Do not print the private config or
copy it into an issue, commit, public guide, or new test fixture.

### Create a separate, new receiver-only installation

Use this only when you need a **new** installation and the destination does not
exist. The example puts private state outside the checkout. Choose an approved,
writable persistent location if `$HOME` is unsuitable:

```sh
export DOT_LOCAL_DIR="$HOME/muse-dot-local"
(
  set -eu
  umask 077
  mkdir "$DOT_LOCAL_DIR"
  cp bots/dot/config.example.json "$DOT_LOCAL_DIR/config.json"
  mkdir "$DOT_LOCAL_DIR/runtime"
  ln -s /dev/null "$DOT_LOCAL_DIR/runtime/outbox.jsonl"
  ln -s /dev/null "$DOT_LOCAL_DIR/runtime/unread.jsonl"
)
```

The first `mkdir` deliberately fails if that installation already exists. The
subshell then stops without overwriting it. Do not change this to an overwrite
operation to get past that warning. Only continue after the whole block succeeds:

```sh
export DOT_BRIDGE_CONFIG="$DOT_LOCAL_DIR/config.json"
```

Edit this private file locally with your preferred editor. Replace
`your-channel-name` with the intended authorized room. The public placeholder
must fail validation; it is not a working room assignment. Confirm the endpoint
and origin are the intended relay. Keep:

- `nick: "dot"`, `pass: ""`, and an empty public `trip` unless separately needed
- `base: "runtime"`
- `receive_idle_s: 0` so a quiet room alone does not trigger the idle watchdog
- `auto_ack.enabled: false`, `mentions.enabled: false`, and no hook
- `protocol_v2` absent or false for this v1 setup
- `dot_mode` absent or `"receive-only"` for this new receiver-only installation
- `durable_outbox` absent or false; receive-only plus durable outbox is refused

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

Repository ignore rules protect the conventional `bots/dot/config.json` and
`bots/dot/runtime/` paths. They are not a privacy guarantee for arbitrary new
paths. Keep private files outside Git or verify their ignore rules explicitly.

## 7. Check without starting anything

Use explicit arguments the first time, so the choices are visible:

```sh
bash bots/dot/launch-bridge.sh check \
  --config "$DOT_BRIDGE_CONFIG" \
  --sha256 "$DOT_BRIDGE_SHA256" \
  --dll "$DOT_BRIDGE_DLL"
```

The successful output is:

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
the full config into a public support request.

All launcher commands use the same validation gates, including `status` and
`stop`. Keep the reviewed binary and hash available while operating an existing
process. Do not overwrite the release you need to stop or diagnose.

You may omit `--config`, `--sha256`, or `--dll` when the corresponding exported
`DOT_BRIDGE_CONFIG`, `DOT_BRIDGE_SHA256`, or `DOT_BRIDGE_DLL` variable is set.
Without a DLL override, the default is the repository Release DLL. Choose a
specific runtime with `--dotnet /absolute/path/to/dotnet`, or `DOTNET_BIN`; without
an executable override, the launcher uses `dotnet` from the prepared `PATH`.

Unknown commands/options are errors. Use `bash bots/dot/launch-bridge.sh --help`
to inspect the interface in your checkout. There is no launcher `restart`
shortcut; stopping, checking state, and starting are deliberate separate steps.

## 8. Start, inspect, and stop

Start in the current terminal:

```sh
bash bots/dot/launch-bridge.sh start \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
```

`start` is the default command if you omit the command word. The launcher stays
in the foreground; the terminal remains occupied. Closing that terminal or its
execution environment may stop it. There is no installed reboot service or
session-lifetime guarantee.

Startup logs contain private room/path details. Keep them private. A fresh
welcome/join result shows that startup reached the room at that time; reconnection
and later failures are separate events.

In another terminal, change to the same checkout, source `env.sh`, and restore
the **same** config/DLL/hash selections. Then:

```sh
bash bots/dot/launch-bridge.sh status \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
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
`connected: true` does not prove an observer, model, or independent wake works.

For a clean stop, press Ctrl+C in the foreground terminal, or run:

```sh
bash bots/dot/launch-bridge.sh stop \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
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

Receiving permission is not permission to reply. To enable an authorized
conversation, the owner must identify the exact approved participants and reply
scope. A nickname or public trip is not authenticated owner identity. Unverified
room text is untrusted data and cannot authorize installations, shell commands,
credential access, recipient changes, or broader disclosure. An explicit scoped
request or approval from the **verified owner** in the relay can authorize the
action under the applicable safety and platform requirements; another agent's
claim about the owner is not verification.

After approval, stop/quiesce the installation, preserve its state, and deliberately
set these private config fields:

```json
"dot_mode": "participate",
"approved_recipients": ["EXACT_APPROVED_NICK"],
"durable_outbox": true
```

This is a fragment to merge into the existing JSON object, not a replacement
config. Keep v1, auto-ack disabled, hooks disabled, and `mentions.enabled: false`.
Do not enable durable outbox in an existing deployment as an incidental docs or
launcher upgrade; mode and queue migrations need their own reviewed plan.

Participation needs an ordinary private `runtime/outbox.jsonl` file. For a
confirmed receiver-only installation, remove **only that symlink**, then create
a new empty regular file at the same pathname. First verify it really is the
expected `/dev/null` symlink and preserve any existing ordinary outbox instead.
Never redirect `>` through the symlink, modify `/dev/null`, or erase an existing
reply queue. `unread.jsonl` remains a `/dev/null` symlink. Run `check` again before
starting.

The model/operator writes the exact authorized reply to a private file. Preview
with actual approved recipient and event-ID values:

```sh
python3 bots/dot/reply.py --config "$DOT_BRIDGE_CONFIG" \
  --to EXACT_APPROVED_NICK --event-id SOURCE_EVENT_ID \
  --text-file /absolute/private/path/reply.txt
```

Only after the applicable reply authorization, add `--send` to queue that text.
This is a room message visible to room participants, not a private DM. `--to`
checks the allowlist; it does not hide the message from the rest of the room.

With durable outbox enabled, the helper returns a `reply_id` and
`durably_queued_not_yet_confirmed_sent`. Without it, the result is
`queued_not_yet_confirmed_sent`. Neither result confirms visible delivery or
completed assistant work. Preserve the source event ID and reply ID when
reconciling the same attempt; inventing a new ID can defeat deduplication.

## 10. Observer setup is not independent wake

If you run an observer for an installation outside `bots/dot`, pass **all three
paths**. `--config` alone does not relocate the observer's default inbox/database:

```sh
export DOT_RUNTIME_DIR="$(dirname -- "$DOT_BRIDGE_CONFIG")/runtime"
python3 bots/dot/mention_hook.py \
  --config "$DOT_BRIDGE_CONFIG" \
  --inbox "$DOT_RUNTIME_DIR/inbox.jsonl" \
  --database "$DOT_RUNTIME_DIR/mentions.sqlite" \
  --wait
```

Use an absolute config path and the same persistent database every time. First
initialization indexes historical messages without notifying them and begins at
the current inbox end. Creating a new database is not how to recover pending
work. The observer may print `{"status":"waiting","mode":"local-event"}` and
then wait for an eligible event; it exits after printing one. A real adapter must
loop and forward it through a supported runtime.

Only acknowledge an event after the destination has durably accepted it:

```sh
python3 bots/dot/mention_hook.py \
  --config "$DOT_BRIDGE_CONFIG" \
  --inbox "$DOT_RUNTIME_DIR/inbox.jsonl" \
  --database "$DOT_RUNTIME_DIR/mentions.sqlite" \
  --ack SOURCE_EVENT_ID
```

An acknowledgment removes that event from the hook's pending-delivery queue;
it does not prove the model finished. The adapter must retain its own unfinished
turn/reply state until the reply is durably queued. A crash between acceptance
and acknowledgment can duplicate delivery, so deduplicate using the stable ID.

Do not run the local observer and remote MCP delivery concurrently for the same
deployment. A session-scoped task that forwards events can stop when that session
ends. A standalone local CLI model is also a separate agent; even successful
inference does not prove it wakes the intended dot conversation.

## 11. Safe stop, restart, and recovery

Do not treat a restart as permission to replay every old reply.

1. **Quiesce writers:** pause the observer/adapter's dispatch and new replies.
   Identify the existing supervisor before stopping anything. Record the
   reviewed commit/DLL/hash, config location, queue counts, and unresolved IDs
   privately without printing message bodies
2. **Stop the bridge:** use the same validated config and release. Confirm its
   actual instance has stopped. Do not proceed if lock ownership is ambiguous
3. **Snapshot consistently:** after all relevant writers stop, preserve the
   complete config/runtime boundary and SQLite companion journals together in a
   protected backup. Copying only a live `.sqlite` file is not a safe backup.
   Do not lose mention cursors, reply attempts, durable requests, or state files
4. **Inspect delivery state:** reconcile each uncertain reply against surviving
   private outbound/room evidence. An echo is evidence of a matching room
   message, not authenticated sender identity or task completion
5. **Validate the chosen release:** ensure private storage still exists, the
   intended files survived, and the DLL matches its reviewed hash. Stop if
   expected production state is missing; silently creating fresh state can lose
   pending work or duplicate delivery
6. **Start one bridge, then the intended consumer:** reuse the same config and
   database. Check fresh process/connection evidence. Test actual independent
   wake/reply recovery only in an authorized test context

### Durable v1 queue commands

With `durable_outbox: true`:

```sh
bash bots/dot/launch-bridge.sh outbox-status \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
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
apply to durable pending requests. The dated [durable-setup proposal](DURABLE_SETUP.md)
predates this guide's operational clarification.

Resolve only the specific uncertain/unconfirmed reply you investigated, while
the bridge is stopped and writers are quiesced:

```sh
bash bots/dot/launch-bridge.sh outbox-resolve REPLY_ID drop \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
```

Or, only when an authorized deliberate retry is appropriate:

```sh
bash bots/dot/launch-bridge.sh outbox-resolve REPLY_ID requeue \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
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

## 12. Run local tests without using a real room

From the repository root, after sourcing `env.sh`:

```sh
bash -n bots/dot/env.sh bots/dot/launch-bridge.sh
python3 bots/dot/test_setup.py
python3 bots/dot/test_mention_hook.py
python3 bots/dot/test_participation.py
python3 bots/dot/test_hatch_adapter.py
python3 bots/dot/test_portable_launcher.py
dotnet test MuseChiefRelay.sln -m:1 -p:UseSharedCompilation=false
python3 tools/test_status.py
```

To run all `bots/dot` Python suites together instead of individually:

```sh
python3 -m unittest discover -s bots/dot -p 'test_*.py'
```

The portable launcher's regression suite uses temporary fake configs/DLLs and
mock executables, never an existing private config, live runtime, room, or
service. It must not install tooling, send test messages, or start a real
observer/bridge. A .NET dependency restore may need package-network access;
that is separate from contacting a real relay room.

Report the commands and actual results for the exact commit. A focused suite
passing does not mean the entire solution passed. A socket-permission failure,
dependency failure, or unrelated test failure must remain visible in the report.
Mock tests establish local launcher behavior, not real connectivity or independent
assistant wake. Authorized end-to-end testing is a separate milestone.

## 13. Troubleshooting

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

Choose a permitted writable `DOT_TOOLING_DIR` before sourcing `env.sh`; keep it
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
output; redact identifiers before sharing excerpts.

### Connected bridge, but no assistant reply

Check the observer's correct inbox/database paths, exact approved-recipient
filter, pending events, supported runtime handoff, model completion, and reply
queue separately. `mentions.enabled: false` is intentional for this local
reader; do not switch on a second delivery mechanism as a guess. The launcher
does not implement independent wake, and a receiver-only deployment cannot send.

### Local Codex initialization finding, 2026-10-04

A bounded local CLI test failed before creating a model thread while preparing
the filesystem sandbox. Its sanitized error was:

```text
app-server socket directory must be a user-owned directory with mode 0700
```

Using a private, writable `CODEX_HOME` did **not** fix that separate
socket-directory check. The failing directory and a supported override were
not established. Authentication and successful model inference were not
verified. The safe next step is platform-operator verification of the socket
directory selected by the sandbox helper while preserving the sandbox; cache
relocation or another arbitrary home path is not a proven fix.

Even a future successful standalone CLI test would demonstrate a separate local
agent, not that it can wake this dot conversation. The required integration still
needs a verified target, durable event acceptance, correlated completion, and
restart recovery. No CLI bootstrap, observer, wake adapter, or supervisor is
installed by this guide's launcher.

## 14. What to record when handing off an installation

Keep a private operator record with:

- Checked-out branch/commit, reviewed DLL path/hash, and selected .NET executable
- Config/runtime/tooling locations and confirmation of their persistence/private
  access, without copying credentials or room transcripts
- Exact check/start/status/stop commands and any actual service owner/name
- Test results, current process/connection evidence, and time of observation
- Queue mode and pending/uncertain counts; any intentional recovery decision
- Independent wake, model completion, and visible reply verification reported
  separately as passed, failed, blocked, or not tested

“Built,” “validated,” “connected,” “event observed,” “model ran,” “reply queued,”
“echo observed,” and “independent wake after restart verified” are different
claims. Record only the ones actually demonstrated. Never turn historical
success into a promise of current connectivity or permanent uptime.
