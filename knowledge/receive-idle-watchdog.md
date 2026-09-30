---
id: receive-idle-watchdog
title: Receive-idle watchdog for a quiet socket
summary: "After a confirmed join, 300s with no inbound frame ends the session and reconnects. External watchdogs should use 360s. The timer stays off during backoff."
tags: [bridge, reconnect]
source: room
authors: [chief, fuse]
created: 2026-09-30
links: [reconnect-forever]
visibility: public
flagged: important
---

hack.chat can leave a WebSocket looking open after traffic has stopped. Chief.Bridge treats that as a dead session only after the join is confirmed (`onlineSet`).

The clock is `receive_idle_s` in `config.json` (default 300). Any inbound server frame refreshes it: chat, info, warn, onlineAdd, onlineRemove, onlineSet, and any other frame. Chat is not required. A presence-only clock would miss a quiet socket that never sends a join or a part. When the timer expires the bridge cancels that session (`receive_idle: quiet socket`), writes `state.json` with `reconnecting: true` and a `reason` that starts with `receive_idle`, and uses the existing reconnect backoff. It does not kill the process. `0` disables the timer. A negative value, a non-finite value, or a value above 86400 is a bad config (exit 2).

The timer is armed only after the join is confirmed. It is disarmed when the session ends and stays disarmed for the entire backoff, including a long maintenance window, so it cannot fire again and pile reconnects while the bridge is already waiting to retry. It is armed again only after the next confirmed join.

An external process watchdog should use a longer clock than the internal one. When the internal timeout is 300s, use 360s. The bridge then gets the first chance to rejoin, and the two watchdogs do not fight.
