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

## What stays

`inbox.jsonl`, `outbox.jsonl`, `watch`, `hook`, `say`, and auto-ack behave as before. Mention routing runs only when `mentions.enabled` is true. Leave it false and the process does not create `{base}/agents/`.

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
| `{base}/agents/<id>/room.jsonl` | Room history for that agent. Scope `room` only. |
| `{base}/agents/<id>/side/<peer>.jsonl` | Side-chat history. Scope `side`. The router never writes this. |

Restart reads those files and continues. Pending events stay pending, in `seq` order. An ack stays acked.

A final `inbox.jsonl` or `control.jsonl` line that does not end in a newline is repaired before the next append. A partial tail is cut back to the last complete line. A complete JSON value that is only missing its newline is terminated. The next event or ack is then its own line and is still there after reload. A newline-terminated corrupt line is skipped and does not glue to the following one.

Retry backoff after a recorded failure is 1s, 2s, 4s, 8s, 16s, then 30s. The head of the queue blocks later events until it is due, so a retry does not skip ahead.

The per-agent `inbox.lock` is exclusive. The bridge and `inbox ack` can both want it. A busy lock is retried for about 2 seconds. That wait is what keeps a mention from disappearing under ordinary contention. If the lock is still busy after that, or another disk error escapes, the mention is not treated as filed: the room `inbox.jsonl` gets `mention route: not filed: <exception type>` and the socket stays up.

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
  "scope": "room",
  "attempts": 0,
  "next_unix": null
}
```

`scope` is always `room`. There is no side-chat field. `trip` is the sender trip from the room line, or null when that line had none. It is untrusted identity evidence. A trip on the wake does not mean the sender is trusted, and a null trip does not mean they failed a check. Auto-ack and the hook still decide trust from their own allowlists (`mention_trips`, `task_trips`, `hook.trips`). The wake has no trusted flag.

An in-process host can implement `IAgentWakeAdapter.WakeAsync` instead of the CLI. A thrown exception there is a soft failure: the pump records the exception type (not the message), applies backoff, and returns. It does not tear down the process, and it does not ack the event. The adapter does its own inference outside this repository.

## Fan-out

Several tags on one line deliver once each.

A chat whose sender nick is a configured agent is a reply. It is not filed to anyone, even if it repeats the original @mentions. Two ways to ask for one extra hop:

- a leading `!fanout` token
- JSON `"fanout": true`

The hop that counts is the farther of the line's own `"hop"` and the deepest fan-out hop already delivered to that sender. A bare `!fanout` carries hop 0, and `"hop": 0` does not clear a hop the sender has already received. The reply is dropped when that carried hop is already at `mentions.max_fanout_hop` (default 1), so two agents cannot alternate `!fanout` and land each reply at hop 1. The remembered hop is the maximum `hop` on fan-out rows in that agent's inbox, including after a restart. The sender is never a recipient of their own tag. A chat from the bridge's own socket nick is treated as this process's echo and is not routed.

## Room and side chats

One agent id can be used in the room and in a side chat. Histories are different files, and room reads do not open `side/`. `ComposeRoomReply` returns the room text it was given and does not read side files. Copying a side scope into the room is refused. Nothing in the router writes side text into `inbox.jsonl`, `room.jsonl`, or a wake object.
