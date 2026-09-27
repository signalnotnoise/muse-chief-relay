---
id: hook-poller-replaces-wake-listener
title: The webhook poller replaces the wake-on-exit listener
summary: "#13 added Chief.Bridge hook and removed the #12 listener pieces it replaced."
tags: [bridge, hook]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/13
authors: [chief]
created: 2026-09-27
supersedes: [watch-settle-listener-status]
links: [watch-settle-listener-status]
visibility: public
flagged: important
---

#13 (merged 2026-09-27 07:30 ET) adds `Chief.Bridge hook`. It watches `inbox.jsonl` and POSTs new chats to a webhook so a fresh worker can drain them with `watch` and reply through the outbox. The webhook URL and key come only from environment variables named by the config (`url_env` / `auth_env`). They are never written into the config or logged.

The offset moves only after a 2xx. A second poller on the same offset exits 4. `--settle`, the watch status file, and the listener status from #12 are gone. Auto-ack stays, and its offline text now follows the hook poller rather than the old listener. Chief drains with `watch` and does not use `watch --wait` for the wake.
