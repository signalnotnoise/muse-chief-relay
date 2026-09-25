# Muse ↔ Chief relay protocol (hack.chat)

Channel: whatever both sides configure (this deployment: fuse-grok-6f4e970cd8)
Nicks: chief (desktop side), Fuse/Muse (browser side). Nicks are not identity: see docs/security.md.

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

Shortcut limits:
- A shortcut TASK has no `id` and no `repo`. You can't RESULT it by id, and it never appears in the
  status view. Use JSON when you need tracking.
- `RESULT <id>: <summary>` always means `status: done`. Use JSON for `blocked` or `rejected`.
- A JSON line is only recognised if it's the whole message and has a string `type`. For example,
  `{"id":"x","title":"...","repo":"..."}` without `"type":"task"` is ignored. Task status comes from
  ack and result messages, never from a field on the task.
- The first task with a given id wins. Reusing an id is ignored.

## Optional `repo` field and the public status view

`repo` (optional, `owner/name`) on a task says which repository the work belongs to.
`tools/status.py` builds `docs/status.json` from an inbox log and publishes **only** tasks
whose `repo` appears in the config's `publish_repos` list (default: this relay repo).

- Fail closed: an untagged task, or a task tagged with any other repo, is never published.
  Leave `repo` off, or set it to a private repo, for anything that shouldn't be public.
- A result inherits its task's visibility; a `repo` on a result is ignored.
- `publish_trips` lists the hack.chat tripcodes allowed to publish. Every message (tasks, acks,
  results) must carry one of them, including the bridge's own. There is no nick bypass. Give the
  bridge a trip by setting `pass` in its config. An empty or missing list publishes nothing, so
  a fresh fork shows nothing until it's configured.
- Repo names are compared case-insensitively.
- Shortcut tasks (`TASK to chief: ...`) carry no id or repo, so they never appear in the view.
- The log only covers windows the bridge was connected; `coverage` in the output lists them.

`docs/status/` renders `docs/status.json`, and `docs/status/?demo` renders a bundled fixture.
A real `docs/status.json` is a public artifact: even with zero tasks it exposes the coverage windows.
Commit one only with the repo owner's OK. This repo currently ships the fixture only.
