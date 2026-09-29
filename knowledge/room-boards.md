---
id: room-boards
title: Room boards landed in #18
summary: "#18 landed Fuse's #17, a jsonl task board, decision log, and scratch pad per room."
tags: [boards, tasks]
source: https://github.com/signalnotnoise/muse-chief-relay/pull/18
authors: [fuse, chief]
created: 2026-09-27
links: [classroom-kit-priority, lesson-outline-critique-shape, muse-product-backlog]
visibility: public
flagged: important
---

#17 (Fuse's `fuse-room-boards`) added room boards and conflicted with main after #14, #15, and #16. #18 landed the files on 2026-09-27 09:12 ET. #17 was closed unmerged.

One jsonl file per room lives at `boards/<sha256(trimmed channel)>.jsonl` (see `boards/README.md` and `boards/schema.json`). Three record types, one per line: `task` (`open`, `claimed`, `blocked`, `done`), `decision`, and `scratch`. Claiming a task requires a trip on the room's allowlist, the same trust model as `publish_trips`. The board is at the repo root, not in a bridge base directory, because the repo is the only state both machines share. It is committed like the CHANGELOG: room state, not private work.

This room's board is `boards/<sha256(trimmed channel)>.jsonl`. Fuse's decision line records that the board was created. Task 1 (`teaching-kit card — Alex picks the workload`, owner Alex) is done: a later line with the same id records the pick. See `classroom-kit-priority`.

Task 2 (`Lesson outline coach — SME-gate checklist + outline critique path`, owner chief) stays `claimed`. The schema has no in-progress state and no subtasks, so a later line with state `done` is how that card closes, and this note does not append one. The coach path and how an agent appends the next card are in `agents/lesson-outline-coach.md`. See `lesson-outline-critique-shape`.

Tasks 6, 7, and 8 are open Muse product backlog cards, owner Alex: shared workspaces, file and image send, and LaTeX files. The next free task id is 9. See `muse-product-backlog`.
