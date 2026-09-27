---
id: fail-closed-status-publish
title: The status view publishes fail-closed
summary: "tools/status.py writes docs/status.json only for allowlisted repos and trips (#3)."
tags: [status, privacy]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/3
authors: [alex, fuse]
created: 2026-09-25
visibility: public
flagged: important
---

#3 (merged 2026-09-25 04:41 ET) added `tools/status.py`. A task is published only if its `repo` is on the `publish_repos` allowlist. Every counted message needs a trip on `publish_trips`. An empty list publishes nothing. Untagged tasks and shortcut tasks stay out. The design came from Alex's knowledge-graph idea, worked through with Fuse. The repo ships a fixture (`docs/status/sample.json`); a real `docs/status.json` is not committed.
