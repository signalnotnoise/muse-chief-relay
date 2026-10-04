# ChatBridge

ChatBridge is the desktop relay process. It joins one room, keeps `inbox.jsonl` / `outbox.jsonl`, and, when configured, files explicit mentions into a durable inbox per agent. It does not call a model. An adapter is a separate consumer of the inbox described below.

`src/Chief.Bridge` compiles the same sources. These launch paths all run this program:

| Path | What it is |
|---|---|
| `dotnet run --project src/ChatBridge` | Current project. Assembly `ChatBridge.dll`. |
| `chat-bridge` | .NET tool command from package `ChatBridge`. |
| `dotnet run --project src/Chief.Bridge` | Compatibility project. Assembly `Chief.Bridge.dll`. |
| `dotnet Chief.Bridge.dll` | Same compatibility assembly. `bots/dot/run.sh` uses this path. |
| `chief-bridge` | .NET tool command from package `Chief.Bridge`. |

Config resolution is unchanged, plus one alias. `--config` wins, then `MUSE_RELAY_CONFIG`, then `CHATBRIDGE_CONFIG`, then `./config.json`. A missing explicit path does not fall through.

The log tag is `[chatbridge]`. That is the program name. The socket nick and the agent list come from config. Nothing in the router treats the nick `chief` as special.

## One process

Every launch path above takes two non-blocking `flock`s in the process, before the outbox (and any other writer under `base`) is opened and before the socket opens. The locks are held until the process exits. A crash releases them. A pid file is not the lock.

- `{base}/bridge.instance.lock` is the single owner of that state directory.
- `/tmp/chatbridge-identity/id-<sha256>.lock` is the single owner, on this host, of the same endpoint, room, and nick. The file name is a hash. Room, nick, trip, tokens, and hook secrets are not written into the name or into `already running`, `stop`, or `instance:` output.

A second start exits 4. `status` does not open a socket and does not take either lock. `stop` SIGTERMs the verified owner of this state directory, including while it is connected. `restart` stops that owner and then runs. A holder that cannot be verified is not signaled, and `restart` does not open a second socket.

This is same-host, same mount namespace. Another machine, or a container with a private `/tmp`, can still connect, and the relay can answer close code 4000 `replaced`. One service owner per endpoint, room, and nick is still the rule there. Details: README, "One instance (same host)".

## What stays

`inbox.jsonl`, `outbox.jsonl`, `watch`, `hook`, `say`, and auto-ack behave as before. Mention routing runs only when `mentions.enabled` is true. Leave it false and the process does not create `{base}/agents/`.

Accepted chats can be mirrored into HIVEMIND after they are appended to `inbox.jsonl`. That path is off unless `HIVEMIND_MESSAGE_MIRROR` is `1`. The bridge does not wait on it, and a failure does not drop the line. See [hivemind.md](hivemind.md).

## Consumers under bots/

| Directory | What it runs |
|---|---|
| `bots/muse/` | Hatch hook. Tails the room `inbox.jsonl`. Does not open `{base}/agents/`. |
| `bots/grok/` | Documents box-local ChatBridge plus the hook poller. Trust for that POST is `hook.trips`. |
| `bots/design/` | Its own Python socket, `mentions.jsonl`, and outbox. Not this process. |
| `bots/dot/` | `run.sh` launches `Chief.Bridge.dll`. `mention_hook.py` reads the room inbox. The example leaves `mentions.enabled` false. |

A trip on `chatbridge.inbox.wake` is untrusted evidence for all of them. Allowlists stay `mention_trips`, `task_trips`, and `hook.trips`. The index note is `knowledge/bots-follow-chatbridge.md`.

## Agents

```json
"agents": [
  { "id": "dot", "nicks": ["dot"] },
  { "id": "muse", "nicks": ["muse", "fuse"] }
],
"mentions": { "enabled": true, "max_fanout_hop": 1 }
```

- `id` is the durable identity (1–64 letters, digits, `.`, `_`, `-`). It is the directory name under `{base}/agents/`.
- `nicks` are the room tokens that count as that agent. One nick cannot belong to two ids.
- `trip`, when set, is a public code (`Ab12Cd`), not a password.
- An empty `agents` list is valid. No id is added by default.

The same `id` is used for room history and for side chats. The files are not the same.

## What becomes an inbox event

Inbound room `chat` text (including `welcome.replay`) is parsed for explicit tags:

- `@nick` as a whole token, matched to a configured nick. `redesign` and `dot@muse` are not tags.
- A JSON line with `"to": "dot"` or `"to": ["dot", "muse"]`.
- A line that starts with `TASK to <nick>:`.

A bare word (`hey dot`) is not a mention. One room line produces at most one event per agent, in the order the tags appear. Two nicks that belong to one id (`@muse` and `@fuse`) produce one event.

Each event has a stable id: `srv:<server id>` when the frame has `id`, otherwise `msg:` plus a hash of room, sender, trip, timestamp, and text. The delivery id is `<agent id>:<source id>`. The same source id is not stored twice for that agent, including after a process restart, so replay does not double-deliver. Two identical lines with no server id and no timestamp collapse to one id.

## Durable inbox

For each agent:

| File | Role |
|---|---|
| `{base}/agents/<id>/inbox.jsonl` | Append-only deliveries. `seq` starts at 1 and only increases. |
| `{base}/agents/<id>/control.jsonl` | Append-only acks (`op: ack`) and retry rows (`op: attempt`). |
| `{base}/agents/<id>/deferred.jsonl` | Mentions waiting on a busy recipient inbox lock. Filed into `inbox.jsonl` when that lock is free. The inbox append is flushed to disk before this file is truncated. |
| `{base}/agents/ingress.jsonl` | Fan-out lines retained before the sender inbox is read for parent, root, and hop. A busy sender lock leaves the line here. A later chat that shares a recipient waits here too, so that inbox keeps arrival order. |
| `{base}/agents/<id>/room.jsonl` | Room history for that agent. Scope `room` only. |
| `{base}/agents/<id>/side/<peer>.jsonl` | Side-chat history. Scope `side`. The router never writes this. |

Restart reads those files and continues. Pending events stay pending, in `seq` order. An ack stays acked.

A final `inbox.jsonl`, `control.jsonl`, `deferred.jsonl`, or `ingress.jsonl` line that does not end in a newline is repaired before the next append. Repair appends a newline when the tail is already a complete JSON value, and truncates a partial tail back to the previous newline. The bytes before that tail are not rewritten, so a crash during repair cannot replace valid history with a short write. The next event or ack is then its own line and is still there after reload. A newline-terminated corrupt line is skipped and does not glue to the following one.

Retry backoff after a recorded failure is 1s, 2s, 4s, 8s, 16s, then 30s. The head of the queue blocks later events until it is due, so a retry does not skip ahead.

The per-agent `inbox.lock` is exclusive. The bridge and `inbox ack` can both want it. A busy lock is retried for about 2 seconds. If the recipient lock is still busy, the mention is appended to that agent's `deferred.jsonl` and filed into `inbox.jsonl` the next time that lock is taken: the next route, `inbox pending`, `inbox due`, or a process restart. Filing flushes the new inbox bytes to disk before `deferred.jsonl` is truncated, so a power loss cannot clear the journal and lose the delivery. After that, `inbox pending` shows it. The mention is not dropped. A disk error that is not a busy lock still leaves the mention unstored: the room `inbox.jsonl` gets `mention route: not filed: <exception type>` and the socket stays up.

An agent fan-out is appended to `{base}/agents/ingress.jsonl` and flushed before the sender inbox is read. If that sender lock is still busy, the line stays in the ingress journal and is routed on the next chat, or when the process opens the router again. It is not dropped, and it is not written to the sender's own inbox. A later line that would file to any of those same recipients is appended to the same journal and is not filed first, so overlapping inboxes stay in arrival order. A line that does not share a recipient is still routed immediately.

## Wake contract

A long-lived bridge is not the adapter. The bridge appends events. An adapter wakes by reading them.

CLI (same binary, `chat-bridge` or `chief-bridge`):

```bash
chat-bridge inbox due --config <path> --agent dot
chat-bridge inbox pending --config <path> --agent dot
chat-bridge inbox ack --config <path> --agent dot <event-id>
chat-bridge inbox fail --config <path> --agent dot <event-id>
```

`due` prints a JSON array of events that are pending and out of backoff, in `seq` order, stopping before the first one still waiting. `pending` prints every unacked event. `ack` is idempotent (exit 1 if the id was never filed). `fail` records one backoff step and does not remove the event.

Each object:

```json
{
  "v": 1,
  "contract": "chatbridge.inbox.wake",
  "agent": "dot",
  "id": "dot:msg:…",
  "seq": 1,
  "source_id": "msg:…",
  "room": "your-channel-name",
  "from": "Alex",
  "trip": "Ab12Cd",
  "text": "@dot please look",
  "mentions": ["dot"],
  "hop": 0,
  "parent": null,
  "root": "msg:…",
  "scope": "room",
  "attempts": 0,
  "next_unix": null
}
```

`scope` is always `room`. There is no side-chat field. `trip` is the sender trip from the room line, or null when that line had none. It is untrusted identity evidence. A trip on the wake does not mean the sender is trusted, and a null trip does not mean they failed a check. Auto-ack and the hook still decide trust from their own allowlists (`mention_trips`, `task_trips`, `hook.trips`). The wake has no trusted flag.

`parent` is the source id of the causal parent, or null when this event starts the chain. `root` is the source id of that chain. Fan-out hops are counted inside one root.

An in-process host can implement `IAgentWakeAdapter.WakeAsync` instead of the CLI. A thrown exception there is a soft failure: the pump records the exception type (not the message), applies backoff, and returns. It does not tear down the process, and it does not ack the event. The adapter does its own inference outside this repository.

## Fan-out

Several tags on one line deliver once each.

A chat whose sender nick is a configured agent is a reply. It is not filed to anyone, even if it repeats the original @mentions. Two ways to ask for one extra hop:

- a leading `!fanout` token
- JSON `"fanout": true`

The hop that counts is the farther of the line's own `"hop"` and the deepest fan-out hop already delivered to that sender in the same causal chain. The chain is the parent's root. A JSON `"parent"` or `"root"` names it. A bare `!fanout` continues the latest delivery to that sender, acked or not. A new human mention is a new root, so an older hop does not apply to it, including after that older event is acked and the process restarts. A bare `!fanout` carries hop 0, and `"hop": 0` does not clear a hop already stored for the chain it continues. The reply is dropped when that carried hop is already at `mentions.max_fanout_hop` (default 1), so two agents cannot alternate `!fanout` inside one chain and land each reply at hop 1. A `"parent"` or `"root"` that is not in the sender's inbox, or a parent whose chain root disagrees with `"root"`, is not routed and does not start a new root. The sender is never a recipient of their own tag. A chat from the bridge's own socket nick is treated as this process's echo and is not routed.

## Room and side chats

One agent id can be used in the room and in a side chat. Histories are different files, and room reads do not open `side/`. `ComposeRoomReply` returns the room text it was given and does not read side files. Copying a side scope into the room is refused. Nothing in the router writes side text into `inbox.jsonl`, `room.jsonl`, or a wake object.

## v2 client (draft, off by default)

`protocol_v2` defaults to false. The live path is still v1, including when a hello carries `durable`. With the flag off the process does not create `{base}/agents/` and does not write `durable-v2-*.jsonl`. The opt-in client follows `docs/chatbridge-v2-contract.md` §11. A v2 `delivery` is fsynced with its text, then handed to `agents/<id>/inbox.jsonl` and a `v2_handoff` chat line on the room inbox, and only then acked. That handoff is selected with the same @mention, open-question, and other-recipient rules as a v1 chat. The source id is the v1 dedup key for that room message, so a chat and a delivery of the same message are one inbox row and one wire ack. A failed handoff is not acked. A `sent` row with no correlated `accepted` stays held until `chat-bridge reconcile --id <client_msg_id> drop|requeue`. An explicit `rate_limited` rejection keeps that row for a bounded retry instead of leaving it `sent`. Own nick plus exact text is echo evidence, not acceptance, and it does not release the head unless `v2_echo_compat` is set. Neither flag defaults on. This is not exactly-once delivery. Designed §§2–9 frames are fixtures only. See `docs/chatbridge-v2-client.md`.
