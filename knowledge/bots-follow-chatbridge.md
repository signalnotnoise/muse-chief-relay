---
id: bots-follow-chatbridge
title: Bot directories follow the ChatBridge wake contract
summary: Muse, Grok, Design, and Dot keep the room inbox and treat a mention-wake trip as untrusted evidence.
tags: [bridge, bots, mentions]
source: room
authors: [fuse, alex]
created: 2026-10-02
links: [chatbridge-mention-inboxes, inbox-watch, hook-poller-replaces-wake-listener]
visibility: public
---

Alex asked that each directory under `bots/` account for ChatBridge milestone 1. The desktop process is ChatBridge. `Chief.Bridge`, `Chief.Bridge.dll`, and `chief-bridge` remain launch aliases. `chat-bridge` is the current tool command.

`inbox.jsonl`, `outbox.jsonl`, `watch`, `hook`, and `say` still work. `mentions.enabled` defaults to false. While it is false the process does not create `{base}/agents/`. When it is true, explicit mentions file one event per tagged agent, and the adapter object is `chatbridge.inbox.wake`.

`trip` on that object is the sender trip from the room line, or null when the line had none. It is untrusted identity evidence. A present trip does not authorize the sender. Trust stays on `mention_trips`, `task_trips`, and `hook.trips`.

- `bots/muse/` tails the room `inbox.jsonl` from Hatch. It does not open `{base}/agents/`. The script still reads `FUSE_RELAY_DIR` and falls back to nick `Fuse` when the config has no nick.
- `bots/grok/` documents the box path: ChatBridge on the socket, then the hook poller. The POST is still filtered by `hook.trips`. The mention-wake trip does not replace that list.
- `bots/design/` is its own Python socket, `mentions.jsonl`, and outbox. It is not the bridge process. A whole-word nick is still how it decides to speak.
- `bots/dot/` starts `Chief.Bridge.dll`. `mention_hook.py` reads `runtime/inbox.jsonl`. The example config leaves `mentions.enabled` false. Send permission stays on `dot_mode` and `approved_recipients`.

No room name, real trip, or webhook URL belongs in those files. The contract text is `docs/chatbridge.md`.
