# Hive mind

Shared memory for chief, Fuse, and Alex. One small markdown note per decision, fact, bug, fix, or how-to. The notes in this folder are the source of truth. The SQLite file under `knowledge/.index/` is a rebuildable search index and is gitignored.

The repo is public. Anything written here is public. A note that is not safe to publish does not belong here.

## When a note gets written

Chief and Fuse both write notes.

- **On every merge.** The PR that landed, what changed, and what it replaced.
- **On every decision made in the room.** The decision, who made it, and the context that should stop the room from asking again.
- **When Alex says "remember this".** Write it down in his words, then a note.

`add` only writes the markdown. Run `rebuild` before `search`. Search reads the index, not the files directly.

## How to write one

```bash
dotnet run --project src/Chief.Knowledge -- add \
  --title "The bridge keeps reconnecting after a drop" \
  --summary "One line, the whole point." \
  --tag bridge --tag reconnect \
  --source https://github.com/signalnotnoise/muse-chief-relay/pull/15 \
  --author chief \
  --body "The facts, and nothing invented."
```

That writes `knowledge/<id>.md`. The id is a slug of the title unless you pass `--id`. `--author` is `chief`, `fuse`, or `alex` (repeat it for more than one). `--source` is exactly one of:

- `alex` — Alex is the source
- `room` — the decision was made in the room
- an `https://` URL, usually the PR

Do not put a hack.chat channel name in the source, the title, or the body. `room` is the word for "it happened in the channel" without naming the channel. A real channel name in this repo is a privacy bug; the Muse client already refuses to remember one for the same reason.

Optional fields: `--created YYYY-MM-DD` (default today, UTC), `--supersedes <id>`, `--link <id>`, `--flagged important`. `--flagged important` is a small ranking boost for a standing decision, not a way to pin a note forever.

Front matter the file must have:

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | lowercase words and hyphens, matches the file name |
| `title` | yes | one line |
| `summary` | yes | one line, at most 240 characters |
| `tags` | yes | at least one lowercase tag |
| `source` | yes | `alex`, `room`, or an https URL |
| `authors` | yes | one or more of `chief`, `fuse`, `alex` |
| `created` | yes | `YYYY-MM-DD` |
| `supersedes` | no | ids this note replaces |
| `links` | no | related ids |
| `visibility` | yes | must be `public` |
| `flagged` | no | `important`, or leave it off |

A note that lists another id in `supersedes` hides that older note from search. Pass `--include-superseded` to show it, ranked down. A cycle (A replaces B and B replaces A) is rejected so two notes cannot hide each other.

Keep the body short and atomic. One decision per file. Link related notes instead of merging them.

## Search and rebuild

```bash
dotnet run --project src/Chief.Knowledge -- check
dotnet run --project src/Chief.Knowledge -- rebuild
dotnet run --project src/Chief.Knowledge -- search "reconnect" --tag bridge --author chief --since 2026-09-01
```

`check` is the CI command. It does not write an index. Exit 0 means the folder is safe to index. Exit 1 is a privacy failure. Exit 2 is a bad note or a bad invocation.

`search` also takes `--until`, `--source`, `--limit`, `--json`, and `--include-superseded`. Filters run before ranking, so a note with the wrong tag cannot outrank a weaker note that actually matches.

Installed as a tool, the same commands are `chief-knowledge`:

```bash
dotnet pack -c Release src/Chief.Knowledge
dotnet tool install --global --add-source src/Chief.Knowledge/bin/Release Chief.Knowledge
chief-knowledge search "reconnect.js"
```

### How ranking works

The index is SQLite. Exact terms (PR numbers, filenames, trips) go through FTS5. Meaning goes through a local embedding model. The two ranked lists are merged with Reciprocal Rank Fusion (k = 60). There is no cross-encoder.

The default model is `Xenova/all-MiniLM-L6-v2` quantized ONNX, run on CPU with ONNX Runtime. No API key. The first `rebuild` or `search` downloads `model_quantized.onnx` into `knowledge/.index/models/` and checks its SHA-256 (`CHIEF_KNOWLEDGE_MODEL_DIR` overrides the cache). The vocab ships in the tool. Every vector row stores that model id. A later model does not get compared with the old vectors; search skips them and tells you to rebuild.

`CHIEF_KNOWLEDGE_EMBEDDER=hash` selects a small hashing embedder that works offline. It does not rank by meaning. It exists so the interface can be swapped, and so a machine with no network can still build an index.

On linux-x64, if `vec0.so` (sqlite-vec 0.1.6, hash-checked) loads, vector search uses it. Otherwise the same vectors are ranked in process. The pure path is the fallback and is what the tests force. The model id still lives on the `vectors` table either way.

A newer note and a note flagged `important` get a small multiplier on the fused score. They do not outrank a clearly better match.

## Privacy

`check` and `rebuild` fail closed. If any note is marked anything but `public`, or if any file under `knowledge/` looks like it holds a bearer key, a password assignment, a token, a session secret, a trip password, or a URL with credentials, the command exits 1 and does not write an index. A failed rebuild leaves the previous index in place.

Sensitive notes belong in a separate local-only store outside the repo, for example `~/.local/share/muse-chief-relay/private-knowledge/`. This tool never reads or writes that directory. Do not copy it into `knowledge/`, and do not point `--knowledge` at it and then commit the result.

Never write a hack.chat channel name into this repo. Not in a note, not in an example, not in a commit message.

## Why this is C#

Chief.Bridge is C# on .NET 8, and `dotnet test` is already the suite that has to stay green. The knowledge CLI is a second .NET tool, `chief-knowledge`, instead of a Python or Node program, so chief and Fuse rebuild and search with the same SDK they use for the bridge. The ONNX runtime stays in this tool. The bridge process, which has to stay up, does not load it.
