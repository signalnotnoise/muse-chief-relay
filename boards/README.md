# Room boards

One jsonl file per room: `boards/<sha256>.jsonl`. The filename is the lowercase
hex SHA-256 of the exact UTF-8 channel string after `trim()`, with no prefix.
A shared task board, decision log, and scratch pad for the humans and agents
in a room. Any participant can read and append; claiming a task requires a trip
on the room's allowlist (same trust model as the relay's `publish_trips`).

Three primitives, one record per line, each with a `type`:

- **task** — work on the board.
  `{type:"task", id, title, owner, state, blocked_on?, handoff_to?}`
  `state` is one of `open | claimed | blocked | done`.
- **decision** — the log that stops us re-asking.
  `{type:"decision", ts, decider, decision, context}`
- **scratch** — append-only, no racing chat.
  `{type:"scratch", ts, author, text}`

See `boards/schema.json` for the exact shape.

Room files are committed to the repo like the CHANGELOG: room state, not
private work, so they don't trip the fixture-only rule. Two agents on
different machines only share the repo — that's why the board lives here and
not in a bridge base dir. Readers (like the Muse page) should treat the
file as read-only and never assume they own the last line.

Writers name the file by hashing the channel. Do not put the channel name in
the filename or in a record. `sha256sum` prints the hash, then two spaces and
a dash; the filename is the hash:

```bash
printf '%s' "$CHANNEL" | sha256sum
```

```python
import hashlib
hashlib.sha256(channel.strip().encode("utf-8")).hexdigest()
```

```csharp
Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel.Trim()))).ToLowerInvariant()
```

A later task line with the same id replaces the card. Task #1 is done: Alex picked the classroom / lesson-coach kit. Tasks 6, 7, and 8 are open Muse product backlog cards, owner Alex: shared workspaces, sending files and images in the app, and LaTeX files. The next free task id is 9.

## Advancing a card

Append a new line. Do not edit or delete earlier lines. Readers keep the latest `task` line for each `id` and keep every `decision` and `scratch` line.

`state` is only `open`, `claimed`, `blocked`, or `done`. There is no in-progress value and no subtask field. Work that has been taken stays `claimed` until a later line marks it `done` or `blocked`.

Task 2 (`Lesson outline coach — SME-gate checklist + outline critique path`, owner `chief`, state `claimed`) is that product card. The schema cannot record in-progress or subtasks, so this repo does not append a replacement line for it here. Closing it is a room decision after the coach path is reviewed: append one later line with the same id, the same title, owner `chief`, and state `done`. A critique of one outline is a new task id, not a subtask of task 2. How an agent does that without writing the channel name into the line is `agents/lesson-outline-coach.md`. Tasks 6–8 are already the backlog cards above, so the next critique id is 9 unless a later line has used a higher id.
