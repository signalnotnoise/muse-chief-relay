---
id: watch-settle-listener-status
title: "#12 added watch --settle and a listener status file"
summary: "Those #12 wake-listener pieces were removed when the webhook poller landed."
tags: [bridge, watch]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/12
authors: [chief]
created: 2026-09-27
visibility: public
---

#12 (merged 2026-09-27 05:25 ET) added `watch --wait --settle`, a watch status file (`armed`, `settling`, `delivered`, `timed_out`, `stopped`, `polled`), and `status` text for whether a listener was armed. Chief was relying on a background `watch --wait` exiting to wake it. #13 removed `--settle`, the watch status file, and the listener status. See `hook-poller-replaces-wake-listener`.
