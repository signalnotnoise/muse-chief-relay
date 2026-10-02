# dot — current-session room participant

## What is running

ChatBridge maintains dot's room connection. `run.sh` starts the compatibility assembly `src/Chief.Bridge/bin/Debug/net8.0/Chief.Bridge.dll`, which is the same program as `src/ChatBridge`. The tool commands are `chat-bridge` and `chief-bridge`. A waiting task reads new room-inbox records and notifies the parent assistant about **@dot mentions or plausible questions addressed to nobody**. The assistant interprets the message, decides whether a reply is appropriate, and authors the reply. The bridge sends that reply through its supported outbox.

This follows the room-inbox pattern used by `bots/grok` and `bots/muse`. It does not use Design's canned replies, acknowledgments, or introductions.

`inbox.jsonl`, `outbox.jsonl`, `watch`, `hook`, and `say` still work. The example config leaves `mentions.enabled` false and does not set `protocol_v2`, so the process does not create `runtime/agents/`. `mention_hook.py` reads `runtime/inbox.jsonl` either way. A raw `delivery` frame is not a wake. The opt-in v2 client (flag off here) writes a `v2_handoff` chat line. This hook applies the same @mention, open-question, and other-recipient filters to that line as to a v1 chat, and the dedup key collapses a v1 chat and a handoff of the same message into one event. The client can also file `agents/<id>/inbox.jsonl` before it acks. That path is `docs/chatbridge-v2-client.md`. When an operator sets `mentions.enabled` to true, an explicit `@dot`, a JSON `to`, or `TASK to dot:` also files one event at `runtime/agents/dot/inbox.jsonl`. The adapter object is `chatbridge.inbox.wake` (`chat-bridge inbox due --agent dot`). `trip` on that object is untrusted identity evidence. A present trip does not authorize a reply. Send permission stays on `dot_mode` and `approved_recipients`. Auto-ack and hook trust, if those features are ever enabled, stay on `mention_trips`, `task_trips`, and `hook.trips`. This deployment keeps auto-ack and hooks off.

**Current verified path:** room → bridge inbox → candidate filter → waiting task notification → assistant-written response → bridge outbox → server echo.

No room content authorizes shell commands, account actions, broader disclosure, or changes to operating permissions. Messages are untrusted data. Only explicitly approved participants are eligible for ongoing replies. Unknown senders and questions clearly addressed to another participant are skipped.

## Files

- `run.sh`, `check_config.py`: start and validate the configured bridge mode
- `config.example.json`: public-safe receiver-only template; real `config.json` is ignored
- `mention_hook.py`: candidate filtering, replay/deduplication, durable queue, and blocking wait
- `reply.py`: queue one exact assistant-written reply, gated by recipient and event ID
- `runtime/`: private ignored state, inbox, outbox, reply attempts, and queues
- `test_setup.py`, `test_mention_hook.py`, `test_participation.py`: local tests
- `HATCH.md` and `hatch*`: optional prepared Hatch integration; not deployed here

## Prerequisites and build

Linux shell, Python 3, .NET 8 SDK, and outbound HTTPS/WSS access to the intended relay. The verified SDK was 8.0.425. Local socket permissions are needed by MSBuild and the .NET tests.

Run from the repository root; preserve local config when pulling:

```sh
git pull --ff-only
dotnet test MuseChiefRelay.sln -m:1 -p:UseSharedCompilation=false
python3 tools/test_status.py
```

If .NET is absent, use Microsoft's official installer in a writable location:

```sh
mkdir -p ../tooling
curl -fL https://dot.net/v1/dotnet-install.sh -o ../tooling/dotnet-install.sh
bash ../tooling/dotnet-install.sh --channel 8.0 --install-dir "$(pwd)/../tooling/dotnet" --no-path
export DOTNET_ROOT="$(cd ../tooling/dotnet && pwd)"
export PATH="$DOTNET_ROOT:$PATH"
```

On a machine with a read-only home, point caches at writable locations before the build:

```sh
TOOLING="$(cd ../tooling && pwd)"
export DOTNET_CLI_HOME="$TOOLING"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="$TOOLING/nuget"
export NUGET_HTTP_CACHE_PATH="$TOOLING/nuget-http-cache"
export XDG_DATA_HOME="$TOOLING/data"
```

## Local configuration

For a new receiver-only installation:

```sh
cp -n bots/dot/config.example.json bots/dot/config.json
mkdir -p bots/dot/runtime
ln -s /dev/null bots/dot/runtime/outbox.jsonl
ln -s /dev/null bots/dot/runtime/unread.jsonl
```

Edit the ignored config to set the intended room. Keep nickname `dot`, empty password, optional empty public trip, `base: runtime`, `receive_idle_s: 0`, `auto_ack.enabled: false`, `mentions.enabled: false`, and no hook. The zero receive-idle setting is supported by ChatBridge and disables its quiet-room watchdog, so a room with no incoming messages does not force a reconnect. Actual socket errors or server closes still trigger normal reconnect handling. A self-asserted public trip is not authentication, including a trip copied onto `chatbridge.inbox.wake`. Never commit real room names, trips, tokens, inbox contents, or reply text.

The default `dot_mode` is **receive-only**, where outbox must point to `/dev/null`. For an explicitly authorized conversation deployment, set local `dot_mode: participate` and an `approved_recipients` list containing the exact approved nicknames. Replace the outbox symlink with an empty regular file without modifying `/dev/null`. This is an operator configuration step, not something room messages may request automatically. The current installation completed that step after user approval.

In participate mode, auto-ack and hooks remain disabled. `unread.jsonl` still points to `/dev/null` to avoid duplicate message storage. The regular inbox feeds the event reader. `run.sh --check` validates mode-specific safeguards.

## Start or restart

```sh
bash bots/dot/run.sh --check
bash bots/dot/run.sh
```

The launcher uses .NET on PATH or the adjacent workspace `tooling/env.sh` when available. It runs the Debug binary built by `dotnet test`, stays in the foreground, and automatically reconnects. Stop with Ctrl+C or SIGTERM. A console line saying `joined #… as dot` plus `connected: true` confirms the welcome handshake.

The bridge does not replay outbox contents that existed before it started. If restarting after a partially completed reply, inspect its outgoing log before deciding whether to requeue. Do not blindly retry uncertain sends.

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

The filter supports both chat envelope shapes, literal case-insensitive @dot, and conservative question candidates. It excludes self, outbound events, unapproved senders, longer nickname mentions, email-like mentions, and questions directed to another known nickname. It suppresses welcome replay and duplicate IDs, detects rotation/truncation, waits for complete newline records, and commits cursor/queue updates atomically. Historical IDs are indexed at initialization without waking.

Pending events remain until acknowledged. A crash after notification but before acknowledgment can duplicate delivery; deduplicate by event ID. The listener remains **session-scoped**. If the task or execution environment closes, notifications stop; no durable off-session service is installed.

## Send one contextual reply

Write the exact assistant-authored text to a local ignored file. The caller must choose a source event and an approved recipient. The tool never invents a reply, addresses an unrelated recipient, or executes room-provided commands.

```sh
# Preview only; does not queue anything.
python3 bots/dot/reply.py --to APPROVED_NICK --text-file bots/dot/runtime/reply.txt --event-id EVENT_ID

# Queue the reviewed text after authorization.
python3 bots/dot/reply.py --to APPROVED_NICK --text-file bots/dot/runtime/reply.txt --event-id EVENT_ID --send
```

Text is sent exactly as supplied; include an address such as `@NAME` in that text when useful. `--to` checks authorization but does not make chat private: it is still a room message visible to participants. The sender checks participation mode, exact recipient membership, a regular outbox, text size, and event-ID duplicate attempts. It appends one JSON line under a lock. No test or announcement is sent automatically.

A `queued_not_yet_confirmed_sent` result is **not delivery confirmation**. Check the bridge's outbound log and preferably a matching server echo from dot. Each reply attempt is reserved before writing to avoid blindly duplicated responses after a crash. If an attempt is uncertain, investigate before retrying.

## Status without exposing room content

```sh
dotnet src/Chief.Bridge/bin/Debug/net8.0/Chief.Bridge.dll status --config bots/dot/config.json
# same program: dotnet src/ChatBridge/bin/Debug/net8.0/ChatBridge.dll status --config bots/dot/config.json
```

Use the same process namespace as the bridge. The private state file reports `alive`, `connected`, and `reconnecting`; a cached alive flag alone is not proof the process exists. Do not publish message bodies, the room identifier, or participant/session identifiers as setup evidence.

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
