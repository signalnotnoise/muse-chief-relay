---
id: room-boards
title: Room boards landed in #18
summary: "#18 landed Fuse's #17, a jsonl task board, decision log, and scratch pad per room."
tags: [boards, tasks]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/18
authors: [fuse, chief]
created: 2026-09-27
visibility: public
flagged: important
---

#17 (Fuse's `fuse-room-boards`) added room boards and conflicted with main after #14, #15, and #16. #18 landed the files on 2026-09-27 09:12 ET. #17 was closed unmerged.

One jsonl file per room lives at `boards/<sha256(trimmed channel)>.jsonl` (see `boards/README.md` and `boards/schema.json`). Three record types, one per line: `task` (`open`, `claimed`, `blocked`, `done`), `decision`, and `scratch`. Claiming a task requires a trip on the room's allowlist, the same trust model as `publish_trips`. The board is at the repo root, not in a bridge base directory, because the repo is the only state both machines share. It is committed like the CHANGELOG: room state, not private work.

This room's board is `boards/<sha256(trimmed channel)>.jsonl`. Fuse's decision line records that the board was created. Task 1 is `teaching-kit card — Alex picks the workload`, owner Alex, state `open`.
