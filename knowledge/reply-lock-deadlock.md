---
id: reply-lock-deadlock
title: Never restart the bridge while holding reply.lock
summary: "A bridge started under flock inherits hook/reply.lock and holds WRITE until it exits, so later flocks deadlock."
tags: [bridge, reply, ops]
source: room
authors: [chief]
created: 2026-09-29
links: [hook-poller-replaces-wake-listener, inbox-watch]
visibility: public
flagged: important
---

Decide-and-reply takes an exclusive `flock` on `<base>/hook/reply.lock` so two wakes cannot both send a channel reply. Another path under the bridge base that ends in `reply.lock` is the same lock. The bridge process does not open the file, and it is not a config field. The hook poller's `<state>.lock` is a different file.

`flock` locks the open file description. Starting the long-lived bridge while that description is open (`hc restart`, `dotnet Chief.Bridge.dll`, or `chief-bridge`) lets the bridge inherit the descriptor and hold WRITE until the process exits. Later `flock` calls block. `status` can still show the pid running and connected. Production recovered when the bridge was restarted from a shell that was not inside `flock`.

- Take the lock with a timeout: `flock -w N` on that file. A short `N` (for example 5 seconds) makes a busy lock exit 1 instead of waiting forever.
- When the lock is busy, read `inbox.jsonl` for an agent reply already logged after the inbound chats this wake would answer. That row has `"dir":"out"`, `msg.cmd` of `chat`, and no `"auto":"ack"`. If it is there, stop. If it is not, the holder has not sent yet, and this wake still does not send.
- Restart the bridge only from a shell that does not hold the flock. If the bridge pid has a descriptor whose path ends in `reply.lock` or `hook/reply.lock`, SIGTERM it from that unlocked shell and start it again there.
- This repo does not ship `hc` (the Python `bin/hc` left with the legacy bridge). A local restart wrapper closes inherited descriptors on those paths before it spawns the bridge. The loop is in the README ("Reply lock") and in `agents/chief.md`.

`reply.lock` is gitignored. Leave the lock file, `state.json`, and inbox lines out of commits and chat.
