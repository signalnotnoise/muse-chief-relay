---
id: reconnect-forever
title: The bridge keeps reconnecting after a drop
summary: "#15 closed the holes that could still end a retry; RunForeverAsync has no 3-attempt cap."
tags: [bridge, reconnect]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/15
authors: [chief]
created: 2026-09-27
links: [csharp-bridge-replaces-python]
visibility: public
flagged: important
---

On 2026-09-27 around 04:40 ET hack.chat dropped the connection. A bridge that had already joined came back by itself; another copy exited and had to be restarted by hand. #15 (merged 2026-09-27 08:39 ET) makes a dropped session keep retrying.

There is no 3-attempt counter in `RunForeverAsync`. The give-up at 3 is Muse's first join only (`FIRST_JOIN_MAX_RETRIES = 3` in `reconnect.js`, shared by `docs/muse/` and `web/muse/`). A first join still stops on bad input: an invalid nick stops immediately, and a taken nick or a rate limit stops after 3 join warnings. Those 3 count join warnings only. A socket close does not spend them. After a successful join, every later join warning is retried, including ones that used to stop the page (`Channel is full`).

On the bridge, a hung handshake is abandoned (the connect used to be bound only to the shutdown token), a disk error while logging the failure or writing `state.json` does not kill the process, and a closed stdout on the reconnect line does not either. Each attempt uses its own HTTP handler. The wait is still 1 s doubling to 30 s, times a random factor in [0.8, 1.2], still capped at 30 s. Logs include the attempt number and never the trip password.

Exit 2, with no retry, is still only a bad config: missing or non-JSON file, empty channel or nick, invalid `auto_ack`, or a `url` that is not absolute `ws://` or `wss://`. A server warn is not in that list. SIGINT / SIGTERM is exit 0.
