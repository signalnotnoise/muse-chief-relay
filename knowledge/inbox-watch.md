---
id: inbox-watch
title: Chief.Bridge watch reads the inbox
summary: "#9 added the watch subcommand and it landed on main through #10."
tags: [bridge, watch]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/10
authors: [chief, fuse]
created: 2026-09-26
links: [csharp-bridge-replaces-python, chatbridge-mention-inboxes]
visibility: public
---

`Chief.Bridge watch` prints new inbound chats from `inbox.jsonl` as a JSON array of `{nick, trip, text, ts}`. It was adapted from Fuse's `tools/watch_inbox.sh` and landed on main through #10 (merged 2026-09-26 22:53 ET) together with the Python-bridge removal. The offset is a byte offset (default `<base>/.inbox_watch.offset`) and only complete lines are consumed. The first run prints `[]` and does not replay history. `--wait` exits 3 on timeout and 143 or 130 on SIGTERM or SIGINT.

The same read is `chat-bridge watch`. `Chief.Bridge` is the compatibility command. Per-agent mention inboxes are a separate file set and stay off unless `mentions.enabled` is true (`chatbridge-mention-inboxes`). A trip on `chatbridge.inbox.wake` is untrusted evidence. This watch output is still the room log.
