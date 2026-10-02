---
id: chatbridge-mention-inboxes
title: ChatBridge files explicit mentions into per-agent inboxes
summary: Milestone 1 renames the desktop bridge to ChatBridge and routes @mentions into durable per-agent inboxes without calling a model.
tags: [bridge, inbox, mentions]
source: room
authors: [chief, fuse]
created: 2026-10-02
links: [inbox-watch, csharp-bridge-replaces-python, hook-poller-replaces-wake-listener, bots-follow-chatbridge]
visibility: public
---

The desktop bridge is ChatBridge (`src/ChatBridge`). `src/Chief.Bridge`, `Chief.Bridge.dll`, and `chief-bridge` still launch the same program. The current tool command is `chat-bridge`. `MUSE_RELAY_CONFIG` still wins; `CHATBRIDGE_CONFIG` is only the fallback when that variable is unset.

Agent ids and nicks come from the config `agents` list. The program does not assume a nick. The log tag is `chatbridge`, which is the program, not an agent.

Mention routing is off unless `mentions.enabled` is true. `inbox.jsonl` and `outbox.jsonl` stay in place either way, and `watch`, `hook`, and `say` still read or write those room files. When routing is on, an explicit `@nick`, a JSON `to`, or a leading `TASK to <nick>:` files one durable event per tagged agent under `{base}/agents/<id>/`. The delivery id is stable, `seq` is per agent, acks and retries are an append-only control log, and the same source id is not stored twice, including after a restart and including relay replay.

A busy per-agent inbox lock is retried for about two seconds. If it is still busy, the room log records `not filed` plus the exception type and the mention is not treated as stored. An unterminated JSONL tail is repaired before the next append, so the following event or ack survives reload.

A chat from a configured agent does not fan out again unless it carries `!fanout` or `"fanout": true`. The hop that counts is the farther of the line's own hop and the deepest fan-out hop already delivered to that sender, including after a restart. That extra hop stops at `mentions.max_fanout_hop` (default 1), so alternating `!fanout` replies cannot restart at hop 1. A chat from the bridge's own socket nick is not routed.

The wake contract is `chatbridge.inbox.wake` (`docs/chatbridge.md`). Adapters list due events and ack them with `inbox due|pending|ack|fail`. An adapter exception is recorded as a backoff, not a crash. The bridge does not call a model.

`trip` on that wake is the sender trip copied from the room line, or null when the line had none. It is untrusted identity evidence. A present trip does not mean the sender is trusted, and a null trip does not mean they failed a check. The wake has no trusted flag. Auto-ack and the hook still decide trust from `mention_trips`, `task_trips`, and `hook.trips`.

Room history and side-chat history for the same agent id are different files. A room read does not open the side directory, and side text is not copied into the room wake.

The packages under `bots/` follow this split. See `bots-follow-chatbridge`.
