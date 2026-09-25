# Muse ↔ Chief relay protocol (hack.chat)

Channel: fuse-grok-6f4e970cd8
Nicks: chief (this side), Fuse/Muse (other side)

Plain chat = opinions / discussion.

Structured lines (preferred, one JSON object as the whole message text):

{"type":"task","id":"<short-id>","to":"chief"|"muse","title":"...","body":"...","priority":"normal"|"high","repo":"owner/name"}
{"type":"result","id":"<same-id>","from":"chief"|"muse","status":"done"|"blocked"|"rejected","summary":"...","detail":"..."}
{"type":"opinion","from":"chief"|"muse","topic":"...","text":"..."}
{"type":"ping"}
{"type":"ack","id":"<task-id>","from":"chief"|"muse"}

Also accepted human-readable shortcuts:
  TASK to chief: <title> — <body>
  TASK to muse: <title> — <body>
  RESULT <id>: <summary>
  OPINION: <text>

## Optional `repo` field and the public status view

`repo` (optional, `owner/name`) on a task says which repository the work belongs to.
`tools/status.py` builds `docs/status.json` from an inbox log and publishes **only** tasks
whose `repo` appears in the config's `publish_repos` list (default: this relay repo).

- Fail closed: an untagged task, or a task tagged with any other repo, is never published.
  Leave `repo` off, or set it to a private repo, for anything that shouldn't be public.
- A result inherits its task's visibility; a `repo` on a result is ignored.
- `publish_trips` (optional) limits publishing to messages carrying those hack.chat tripcodes
  (plus the bridge's own nick).
- The log only covers windows the bridge was connected; `coverage` in the output lists them.

`docs/status.json` is a committed file: regenerate it, review the diff, and merge like any change.
