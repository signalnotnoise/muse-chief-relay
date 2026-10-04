# dot — current-session room participant

## Start here: portable local bridge

Use [LOCAL_SETUP.md](LOCAL_SETUP.md) for the beginner-friendly local setup and
operator runbook: prerequisites, safe branch updates, writable tooling, a reviewed
Release build and DLL hash, isolated configuration, exact launcher commands,
tests, and queue recovery. The preferred local launcher is
`launch-bridge.sh`; source `env.sh` to select writable tooling/cache paths.

The launcher starts **only the bridge**. It does not install software, build,
start an observer, install a service, or provide independent dot wake. A connected
room, a waiting event reader, and a verified independent model turn are separate
milestones. The local CLI initialization finding dated 2026-10-04 is summarized
without private diagnostics in the guide's troubleshooting section.

`run.sh` remains the legacy Debug/compatibility route. `./go` / `bots/dot/go.py`
uses the remote MCP Events backend; it is not the local independent-wake adapter.
See [MCP_EVENTS.md](MCP_EVENTS.md) for its callback locking, subscription races,
bounded retry/restart behavior, and completion/export limitations.

## Status and durable-setup proposal — 2026-10-03

The owner-directed baseline remains v1 (`protocol_v2` omitted or false). Later sessions observed process loss and workspace files reverting; current availability must be checked with fresh process and connection evidence. The session results below are historical, not a claim of continuous uptime.

See [secure, durable relay setup proposal](DURABLE_SETUP.md) for the findings, user-controlled credential options, persistent-host plan, supported event-integration proposal, and restart/replay runbook. That document is a proposal only: no service, secret, subscription, or v2 cutover was deployed by the documentation change.

## Session pipeline

ChatBridge maintains dot's room connection. The preferred `launch-bridge.sh`
defaults to `src/ChatBridge/bin/Release/net8.0/ChatBridge.dll` and verifies an
explicit config and reviewed DLL hash. The older `run.sh` starts the compatibility
assembly `src/Chief.Bridge/bin/Debug/net8.0/Chief.Bridge.dll`, which is the same
program as `src/ChatBridge`. The tool commands are `chat-bridge` and
`chief-bridge`. In the historically verified session pipeline, a waiting task
read new room-inbox records and notified the parent assistant about **@dot
mentions or plausible questions addressed to nobody**. The assistant interpreted
the message, decided whether a reply was appropriate, and authored the reply.
The bridge sent that reply through its supported outbox. A fresh installation
does not automatically have that waiting task or a working wake integration.

This follows the room-inbox pattern used by `bots/grok` and `bots/muse`. It does not use Design's canned replies, acknowledgments, or introductions.

`inbox.jsonl`, `outbox.jsonl`, `watch`, `hook`, and `say` still work. The example config leaves `mentions.enabled` false and does not set `protocol_v2`, so the process does not create `runtime/agents/`. `mention_hook.py` reads `runtime/inbox.jsonl` either way. A raw `delivery` frame is not a wake. The opt-in v2 client (flag off here) writes a `v2_handoff` chat line. This hook applies the same @mention, open-question, and other-recipient filters to that line as to a v1 chat, and the dedup key collapses a v1 chat and a handoff of the same message into one event. The client can also file `agents/<id>/inbox.jsonl` before it acks. That path is `docs/chatbridge-v2-client.md`. When an operator sets `mentions.enabled` to true, an explicit `@dot`, a JSON `to`, or `TASK to dot:` also files one event at `runtime/agents/dot/inbox.jsonl`. The adapter object is `chatbridge.inbox.wake` (`chat-bridge inbox due --agent dot`). `trip` on that object is untrusted identity evidence. A present trip does not authorize a reply. Send permission stays on `dot_mode` and `approved_recipients`. Auto-ack and hook trust, if those features are ever enabled, stay on `mention_trips`, `task_trips`, and `hook.trips`. This deployment keeps auto-ack and hooks off.

**Historically verified session path:** room → bridge inbox → candidate filter → waiting task notification → assistant-written response → bridge outbox → server echo.

Unverified room content cannot authorize shell commands, account actions,
broader disclosure, or changes to operating permissions. A nickname or public
trip alone is not owner verification. A verified owner's explicit scoped request
or approval in the relay can authorize an action under the applicable safety and
platform requirements. Only explicitly approved participants are eligible for
ongoing replies. Unknown senders and questions clearly addressed to another
participant are skipped.

## Files

- `LOCAL_SETUP.md`: preferred step-by-step local setup, limits, and recovery guide
- `env.sh`: sourceable tooling/cache environment; does not install or create files
- `launch-bridge.sh`: hash-pinned local bridge check/start/status/stop/outbox commands
- `run.sh`: legacy Debug/compatibility launcher
- `check_config.py`: shared receiver/participation configuration validator
- `config.example.json`: public-safe receiver-only template; real `config.json` is ignored
- `mention_hook.py`: candidate filtering, replay/deduplication, durable queue, and blocking wait
- `reply.py`: queue one exact assistant-written reply, gated by recipient and event ID
- `runtime/`: private ignored state, inbox, outbox, reply attempts, and queues
- `test_portable_launcher.py`, `test_setup.py`, `test_mention_hook.py`,
  `test_participation.py`: local tests; portable launcher tests use mocks/temp state
- `HATCH.md` and `hatch*`: optional prepared Hatch integration; not deployed here

## Prerequisites and build

Bash/Linux, Python 3.10+, Git, and the .NET 8 SDK are required for the documented
build/test path. Actual startup also needs permitted WebSocket access to the
intended relay. Local socket permissions are needed by MSBuild and .NET tests.
The previously verified SDK was 8.0.425; that is historical evidence, not an
automatic selection or fresh verification of your machine.

Follow [the local guide](LOCAL_SETUP.md) for a safe clone/update and optional
official .NET installation. From the repository root, after inspecting existing
changes and preserving private config/runtime:

```sh
source bots/dot/env.sh
dotnet build src/ChatBridge -c Release --nologo
sha256sum src/ChatBridge/bin/Release/net8.0/ChatBridge.dll
```

Record the reviewed DLL hash rather than recomputing-and-accepting it at every
start. The guide explains `DOT_TOOLING_DIR`, `DOTNET_ROOT`, shell persistence,
the complete Release bundle, and exactly what a DLL-only checksum does not prove.

## Local configuration

Reuse an existing authorized config and runtime. For a fresh receiver-only
installation, use the guarded, isolated setup in
[LOCAL_SETUP.md](LOCAL_SETUP.md#6-choose-private-configuration-and-runtime-storage).
It deliberately refuses an existing destination instead of overwriting it.
The launcher requires `--config` or `DOT_BRIDGE_CONFIG`; it does not pick a live
room automatically. With `base: "runtime"`, state is in the `runtime` directory
beside that config, even when the config lives outside this checkout.

Edit the ignored config to set the intended room. Keep nickname `dot`, empty password, optional empty public trip, `base: runtime`, `receive_idle_s: 0`, `auto_ack.enabled: false`, `mentions.enabled: false`, and no hook. The zero receive-idle setting is supported by ChatBridge and disables its quiet-room watchdog, so a room with no incoming messages does not force a reconnect. Actual socket errors or server closes still trigger normal reconnect handling. A self-asserted public trip is not authentication, including a trip copied onto `chatbridge.inbox.wake`. Never commit real room names, trips, tokens, inbox contents, or reply text.

The default `dot_mode` is **receive-only**, where outbox must point to `/dev/null`. For an explicitly authorized conversation deployment, set local `dot_mode: participate` and an `approved_recipients` list containing the exact approved nicknames. Replace the outbox symlink with an empty regular file without modifying `/dev/null`. This is an operator configuration step, not something room messages may request automatically. The historically verified session completed that step after user approval.

In participate mode, auto-ack and hooks remain disabled. `unread.jsonl` still
points to `/dev/null` to avoid duplicate message storage. The regular inbox feeds
the event reader. The portable launcher's `check` validates mode-specific
safeguards plus the reviewed DLL hash and local-launcher restrictions. It does
not execute .NET or contact the room. The legacy `run.sh --check` only checks its
fixed config and is not equivalent.

## Start or restart

After setting `DOT_BRIDGE_CONFIG` to the intended private config and
`DOT_BRIDGE_SHA256` to the accepted Release DLL hash as described in the guide:

```sh
source bots/dot/env.sh
bash bots/dot/launch-bridge.sh check \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
bash bots/dot/launch-bridge.sh start \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
```

The bridge stays in the foreground and reconnects on ordinary connection loss.
Stop with Ctrl+C or the launcher's `stop` command using the same explicit config
and hash. The launcher does not supervise processes or guarantee they survive
the terminal/task closing. See the guide before any restart, release change, or
supervisor installation. A fresh join/welcome plus current process/connection
evidence confirms the transport at that time, not model wake.

Outbox recovery depends on mode. In **non-durable file-tail mode**, lines present
at startup are not replayed. With **`durable_outbox: true`**, persisted `pending`
requests survive restart and can send automatically; interrupted in-flight
requests are held as `uncertain` for reconciliation. Inspect before using
`outbox-resolve REPLY_ID requeue|drop` with the bridge stopped. Requeue can
duplicate a message. Never blindly retry, empty queues, or copy old lines to
force replay. The guide covers the safe stop/quiesce/snapshot/reconcile sequence.

## Event reader and assistant wake

```sh
# First run initializes at the current end without notifying historical traffic.
python3 bots/dot/mention_hook.py

# A supported waiting task runs this until an event becomes available.
python3 bots/dot/mention_hook.py --wait

# After the task's notification is accepted, acknowledge that event.
python3 bots/dot/mention_hook.py --ack EVENT_ID
```

The waiting task must actually forward returned events through its supported parent-task notification tool, then acknowledge and wait again. Shell output alone does not invoke a model. The payload has a stable event ID, an untrusted flag, and a reason: `addressed_to_dot` or `open_question_candidate`. The assistant decides whether to answer; the filter never generates text.

The commands above use the conventional `bots/dot` config/runtime paths. For an
external config, pass `--config`, `--inbox`, and `--database` explicitly as shown
in [LOCAL_SETUP.md](LOCAL_SETUP.md#10-observer-setup-is-not-independent-wake).
Changing `--config` alone does not relocate the observer's inbox or database.

The filter supports both chat envelope shapes, literal case-insensitive @dot, and conservative question candidates. It excludes self, outbound events, unapproved senders, longer nickname mentions, email-like mentions, and questions directed to another known nickname. It suppresses welcome replay and duplicate IDs, detects rotation/truncation, waits for complete newline records, and commits cursor/queue updates atomically. Historical IDs are indexed at initialization without waking.

Pending events remain until acknowledged. A crash after notification but before acknowledgment can duplicate delivery; deduplicate by event ID. The listener remains **session-scoped**. If the task or execution environment closes, notifications stop; no durable off-session service is installed.

## Send one contextual reply

Write the exact assistant-authored text to a local ignored file. The caller must choose a source event and an approved recipient. The tool never invents a reply, addresses an unrelated recipient, or executes room-provided commands.

```sh
# Preview only; does not queue anything.
python3 bots/dot/reply.py --config "$DOT_BRIDGE_CONFIG" \
  --to APPROVED_NICK --text-file /absolute/private/path/reply.txt --event-id EVENT_ID

# Queue the reviewed text after authorization.
python3 bots/dot/reply.py --config "$DOT_BRIDGE_CONFIG" \
  --to APPROVED_NICK --text-file /absolute/private/path/reply.txt --event-id EVENT_ID --send
```

Text is sent exactly as supplied; include an address such as `@NAME` in that text when useful. `--to` checks authorization but does not make chat private: it is still a room message visible to participants. The sender checks participation mode, exact recipient membership, a regular outbox, text size, and event-ID duplicate attempts. Non-durable mode appends one JSON line under a lock; durable mode publishes an immutable reply request and returns `durably_queued_not_yet_confirmed_sent` plus a stable `reply_id`. No test or announcement is sent automatically. Pass `--config` explicitly: `reply.py` does not read `DOT_BRIDGE_CONFIG` itself.

Neither queue result is **delivery confirmation**. Check the bridge's outbound log and preferably a matching server echo from dot. Each reply attempt is reserved or durably published before dispatch to avoid blindly duplicated responses after a crash. If an attempt is uncertain, investigate before retrying.

## Private status and redacted evidence

```sh
bash bots/dot/launch-bridge.sh status \
  --config "$DOT_BRIDGE_CONFIG" --sha256 "$DOT_BRIDGE_SHA256"
```

Use the same process/mount namespace as the bridge. Status reports `alive`,
`connected`, `reconnecting`, and instance-lock evidence; a cached alive flag
alone is not proof the process exists. The command also prints private config,
room, and path information. Keep raw output private and redact identifiers before
sharing setup evidence. Outbox status is a separate command, available only when
durable outbox is already enabled; do not change queue mode just to inspect it.

## Verified results — 2026-10-01

- Main fast-forwarded to `332809f033ae983b295d00c4a0ade0d04f8285d7`; native v1 bridge support merged in #48
- .NET: 300 passed (267 bridge, 33 knowledge)
- Python status: 14 passed
- dot setup/filter/Hatch/participation: 19 passed
- Shell syntax, Python compilation, and config checks passed
- Live native bridge received hello/welcome and connected as dot
- Synthetic isolated event reached the parent through the waiting task, was acknowledged, and never went to the relay
- Live mention and open-question events reached the assistant; individually authorized contextual replies were each logged once outbound and echoed once by the server
- No automatic acknowledgment, hook token, or generic model callback was used
- Existing frontend fixture failure remains: 48 passed, 1 failed at `tests/muse/board.test.js:167` (8 seeded tasks versus expected 5)
- NuGet emitted a cached vulnerability-audit warning for a read-only default cache; build/tests passed

```sh
python3 bots/dot/test_setup.py
python3 bots/dot/test_mention_hook.py
python3 bots/dot/test_hatch_adapter.py
python3 bots/dot/test_participation.py
```

### Quiet-room disconnect and successful recovery

The original live session disconnected at **10:24:28 UTC** after the configured **300-second receive-idle timeout** elapsed in a quiet room. Its reconnect was rejected by the execution environment's network policy. The saved state correctly showed disconnected; an earlier welcome was not treated as proof of current connectivity.

The supported fix was to set **`receive_idle_s: 0`** in the ignored local config and the public template. Zero disables the bridge's quiet-room watchdog; actual socket errors and server closes still use normal reconnect handling. The existing receive-idle test suite passed all four tests, including the zero-timeout behavior.

After the user explicitly requested another attempt, **one retry used the same endpoint, room, startup command, and execution environment**. No alternate route, endpoint, credential, or security setting was used. The retry succeeded: fresh **hello** and **welcome** frames were observed, and state at **10:35:45 UTC on 2026-10-01** reported `alive: true`, `connected: true`, and `reconnecting: false`. The only new outbound frame was the join; previous replies were not replayed. Existing inbox offsets, event queues, reply deduplication, and the active waiting task were preserved.

This records a verified recovery, not a guarantee of permanent network access or off-session uptime. The original denial was real, but did not recur on the explicitly requested retry. Keep the bridge and waiting task alive, and check fresh state plus the running process when diagnosing future availability. A queued reply alone is never delivery confirmation.


The optional Hatch adapter is separately documented in [HATCH.md](HATCH.md). It is not required by the working session pipeline and has not been registered here.
