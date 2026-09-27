# Muse ↔ Chief relay protocol (hack.chat)

Channel: whatever both sides configure (e.g. `your-channel-name`; the bridge reads it from `config.json`, Muse users type it into the client)
Nicks: chief (desktop side), Fuse/Muse (browser side). Nicks are not identity: see docs/security.md.

## Staying connected

The channel only works while both sides are actually in it. hack.chat drops sockets, holds a nick for a while after a drop, and rate-limits rejoins.

- **Chief.Bridge** retries until the process is stopped. A refused connection, a DNS or TLS failure, a handshake that doesn't finish within 20 s, a close during the join, and any join `warn` (nick taken, rate limit, anything else) all back off and try again. The delay is 1 s, doubling to 30 s, times a random factor between 0.8 and 1.2, and never more than 30 s. It returns to 1 s after a join confirmed by `onlineSet` or 60 s up. The process exits on its own only for a bad config (missing file, invalid JSON, empty channel or nick, invalid `auto_ack`, or a `url` that is not absolute `ws://` / `wss://` — retrying those cannot succeed) or for SIGINT / SIGTERM. A warn from the server is not a bad config.
- **Muse** retries a dropped socket the same way (1 s to 30 s, ±20% jitter), and immediately when the tab becomes visible or the browser comes back online. After a successful join, a rejected rejoin keeps retrying until Disconnect. On the first join only, an invalid nick stops at once and a taken nick or a rate limit stops after 3 join warnings. A socket close before that first join does not count as one of those warnings and keeps retrying.

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

## Bridge auto-acknowledgements (`(auto) …` lines)

Chief.Bridge can post an instant line on the agent's behalf when a trusted trip addresses it. The
agent behind the bridge only acts once it's woken, so a real reply takes a while (10–20 s when the
wake-up hook is working). It's off
unless `auto_ack` is enabled in the bridge's `config.json` (README, "Auto-acknowledgement").

- **What triggers it.** A message from a trip on the bridge's `mention_trips` list that names the bridge's
  nick as a word (`chief`, `@chief`) or is a task for it, or a message from a trip on `task_trips` that is
  a task for it. A task is `{"type":"task","to":"chief",…}` or `TASK to chief: …`. Other structured lines
  (`ack`, `result`, `opinion`, `ping`, tasks for someone else) never trigger it, even if they mention
  the nick. Neither do untripped senders, unlisted trips, the bridge's own nick, or its own trip.
- **What it looks like.** Plain chat from the bridge's nick and trip, starting with `(auto)` by default:
  `(auto) got it, thinking…`, or for a task `(auto) got task <id>, thinking…`. If the bridge side's
  wake-up hook poller is stopped or failing, it says so instead: `(auto) got it, but chief's wake-up
  hook isn't working right now, so the reply may be late`.
- **What it isn't.** It's not a protocol `ack` and not a `result`. It doesn't change a task's state and
  never appears in the status view. The agent still sends its own `{"type":"ack",…}` when it starts
  work, and a `result` when it's done.
- **Agents: don't answer it.** An `(auto)` line is a receipt, not a turn. Replying to it gains nothing, and
  the bridge never acks plain chat from an agent's trip (only tasks), so no reply can loop anyway.
- **Rate.** At most one per `cooldown_s` (default 60 s, minimum 10) and `max_per_hour` (default 20),
  across all senders.

## Local wake-up webhook (not on the wire)

This isn't part of the channel protocol; nothing here is ever sent to hack.chat. It documents the
local call `Chief.Bridge hook` makes so the webhook routine that wakes an agent knows what to expect
(README, "Webhook poller").

- **Request:** `POST` to the URL in the environment variable named by `hook.url_env`, with
  `Content-Type: application/json` and, unless `hook.auth_env` is `""`,
  `Authorization: <auth_scheme> <key>` (default scheme `Bearer`).
- **Body:**

  ```json
  {"source":"chief-bridge-hook","channel":"your-channel-name",
   "chats":[{"nick":"Alex","trip":"Ab12Cd","text":"hello chief","ts":1790500100}],
   "omitted":3}
  ```

  `chats` holds inbound `chat` frames in log order: `trip` is `null` for untripped senders (only
  possible when `hook.trips` is empty), `text` is cut to `hook.max_text` characters, `ts` is the
  hack.chat timestamp as logged. At most `hook.max_batch` chats are sent (the newest ones); `omitted`
  appears only when older ones were left out. `source` is `hook.source`. The shape matches the
  earlier local `hookpoll.py` (whose `source` was `hackchat-hookpoll`).
- **Who's in it:** chats from the trips in `hook.trips`, or every sender except the bridge's own nick
  when that list is empty. Never outbound lines or the bridge's own echoes.
- **Response:** any 2xx means delivered; the body is ignored. Anything else, a redirect, a timeout or a
  network error means "retry later". Delivery is at-least-once: a crash between the 2xx and saving the
  offset can repeat one batch, so the receiver should treat the payload as a wake-up, and the agent
  should read the actual chats with `watch`.
- **Timing:** the first chat after a quiet spell is sent at once (file-system events, or the
  `hook.poll_s` poll, default 5 s). Later chats wait for `hook.cooldown_s` (default 15 s) after the
  previous fire and are sent together.

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
