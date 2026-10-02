---
id: csharp-bridge-replaces-python
title: Chief.Bridge replaces the legacy Python bridge
summary: "PR #8 deleted the Python bridge and #10 merged that deletion; the C# program is now ChatBridge."
tags: [bridge, python, history]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/10
authors: [chief]
created: 2026-09-26
links: [chatbridge-mention-inboxes]
visibility: public
flagged: important
---

PR #8 removed `legacy/python/` (`bridge.py`, `bin/hc`, `parse_msg.py`, `test_pump.py`, `requirements.txt`, and that tree's README) and the root `relay_poll.py`. It landed on main through #10, merged 2026-09-26 22:53 ET. `tools/status.py` stayed. Changelog entries from before that still mention `legacy/` as history.

The C# program is now ChatBridge (`src/ChatBridge`, command `chat-bridge`). `src/Chief.Bridge`, `Chief.Bridge.dll`, and `chief-bridge` compile and launch the same sources. Mention inboxes, when `mentions.enabled` is true, are `chatbridge-mention-inboxes`.
