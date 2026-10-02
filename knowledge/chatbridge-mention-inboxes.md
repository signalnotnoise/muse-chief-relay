---
id: chatbridge-mention-inboxes
title: ChatBridge files explicit mentions into per-agent inboxes
summary: Milestone 1 renames the desktop bridge to ChatBridge and routes @mentions into durable per-agent inboxes without calling a model.
tags: [bridge, inbox, mentions]
source: room
authors: [chief]
created: 2026-10-02
links: [inbox-watch, csharp-bridge-replaces-python]
visibility: public
---

The desktop bridge is ChatBridge (`src/ChatBridge`). `src/Chief.Bridge`, `Chief.Bridge.dll`, and `chief-bridge` still launch the same program. `MUSE_RELAY_CONFIG` still wins; `CHATBRIDGE_CONFIG` is only the fallback when that variable is unset.

Agent ids and nicks come from the config `agents` list. The program does not assume a nick. The log tag is `chatbridge`, which is the program, not an agent.

Mention routing is off unless `mentions.enabled` is true. `inbox.jsonl` and `outbox.jsonl` stay in place either way. When routing is on, an explicit `@nick`, a JSON `to`, or a leading `TASK to <nick>:` files one durable event per tagged agent under `{base}/agents/<id>/`. The delivery id is stable, `seq` is per agent, acks and retries are an append-only control log, and the same source id is not stored twice, including after a restart and including relay replay.

A chat from a configured agent does not fan out again unless it carries `!fanout` or `"fanout": true`, and that extra hop stops at `mentions.max_fanout_hop` (default 1). A chat from the bridge's own socket nick is not routed.

The wake contract is `chatbridge.inbox.wake` (`docs/chatbridge.md`). Adapters list due events and ack them. An adapter exception is recorded as a backoff, not a crash. The bridge does not call a model.

Room history and side-chat history for the same agent id are different files. A room read does not open the side directory, and side text is not copied into the room wake.
