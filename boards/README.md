# Room boards

One jsonl file per room: `boards/<room>.jsonl`. A shared task board, decision
log, and scratch pad for the humans and agents in a room. Any participant can
read and append; claiming a task requires a trip on the room's allowlist
(same trust model as the relay's `publish_trips`).

Three primitives, one record per line, each with a `type`:

- **task** — work on the board.
  `{type:"task", id, title, owner, state, blocked_on?, handoff_to?}`
  `state` is one of `open | claimed | blocked | done`.
  A later line with the same `id` replaces the earlier card (the file is
  append-only).
- **decision** — the log that stops us re-asking.
  `{type:"decision", ts, decider, decision, context}`
- **scratch** — append-only, no racing chat.
  `{type:"scratch", ts, author, text}`

See `boards/schema.json` for the exact shape. Blank lines are ignored. A line
that is not one of those three records is skipped by readers.

Room files are committed to the repo like the CHANGELOG: room state, not
private work, so they don't trip the fixture-only rule. Two agents on
different machines only share the repo — that's why the board lives here and
not in a bridge base dir. Readers should treat the file as read-only and never
assume they own the last line. Updates are commits. The browser does not append.

This room's board: `boards/fuse-grok-6f4e970cd8.jsonl`. Task #1 is seeded
empty, waiting on Alex to pick the teaching-kit card.

These board records are not hack.chat messages. A chat line
`{"type":"task","id":"t1","to":"chief",...}` is the relay protocol
(`docs/protocol.md`). A board line `{type:"task", id, title, owner, state}` is
this file. The Muse stub renders the file. It does not turn chat into board
rows, and it does not send board rows into the channel.

## Muse stub (read-only)

`docs/muse/` and `web/muse/` (kept identical) show the board above the join
form. The page issues one `GET` for `../../boards/<room>.jsonl` (default room
`fuse-grok-6f4e970cd8`). `?board=<room>` selects another file, and the room
name must be a single safe path segment (`letters, digits, . _ -`). There is
no form, no `POST`/`PUT`, and no write to `localStorage`. Card text is rendered
with `textContent`.

A missing file, an empty file, or a host that cannot see `boards/` (GitHub
Pages publishes `docs/` only, so `/muse/` cannot fetch `../../boards/`) shows
an empty board and leaves chat working. Serve the repository root to see the
file:

```bash
python3 -m http.server 8080
# http://localhost:8080/web/muse/   or   http://localhost:8080/docs/muse/
```

## Room board, public status page, and the private Voizle knowledge graph

These are three different stores. They do not feed each other.

**Room board** (`boards/<room>.jsonl`). Committed room state for the humans
and agents in one hack.chat room: tasks, decisions, and scratch. This room's
file is `boards/fuse-grok-6f4e970cd8.jsonl`. It is public the way the CHANGELOG
is public (anyone with the repo can read it). It is not a place for secrets.
The Muse client only reads it. Updates are commits.

**Public status page** (`docs/status/`, from merged PR #3). `tools/status.py`
builds `docs/status.json` from an inbox log, and the page renders that file.
Publishing fails closed: a task appears only when its `repo` is on
`publish_repos` (default `signalnotnoise/muse-chief-relay`) and every counted
message carries a trip on `publish_trips`. Untagged tasks and tasks for any
other repo are left out. Shortcut tasks never appear. A real `docs/status.json`
is a public artifact even when it lists zero tasks, because `coverage` still
shows when the relay was online. This repo ships the fixture only
(`docs/status/sample.json`, open `docs/status/?demo`). Do not commit a
generated status file without the repo owner's OK.

**Private Voizle knowledge graph.** Alex's knowledge graph is the Voizle KG.
It lives locally under his Voizle work and is produced there (`generate-graph.js`,
`graph-data.js`). It is not part of muse-chief-relay. It must never be
published into `docs/status.json`, the public status page, a room board, or
any other file in this repository. Do not add a Voizle repo to `publish_repos`.
Do not paste graph nodes, edges, or generated `graph-data.js` output here.
Leaving Voizle off the status allowlist is the rule, not a courtesy: the graph
is a different system and stays on Alex's machine.
