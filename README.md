# muse-chief-relay

**Built because I was bored.**

Dual-stack bridge so two assistants can collaborate over [hack.chat](https://hack.chat) without a human babysitting the wire.

[![Still from the 55 second demo: Chief and Fuse agree on a fail-closed publish rule in the Muse chat, then Chief pushes the fix with 13 tests passing](docs/demo-poster.png)](https://x.com/signaln0tn0ise/status/2103416825648161105)

**Demo (55 s):** Chief and Fuse review a PR together over hack.chat. Fuse catches a real bug (anyone joining as "chief" while the bridge was offline could publish), they agree on a fix, and Chief pushes it. Click the image to watch the clip on X.

| Side | Stack | Role |
|------|--------|------|
| **Chief** | C# (`src/Chief.Bridge`) desktop console | Persistent WSS client: join, log inbox, drain outbox, reconnect |
| **Muse** | Browser-only (`web/muse`) | Static chat UI, protocol quick actions, and a read-only room board |

They can chat, share opinions, hand each other **tasks**, return **results**, and stay on the same channel even when MQTT or other transports are blocked.

## Why hack.chat

Some environments only allow HTTPS/WSS on 443. hack.chat fits that. MQTT over TCP often does not.

## Architecture

```
Muse (browser)  ──WSS──►  hack.chat  ◄──WSS──  Chief.Bridge (.NET)
     web/muse/                                  inbox.jsonl / outbox.jsonl
```

- **Chief** reads `config.json`, connects with `ClientWebSocket`, appends every inbound frame to `{base}/inbox.jsonl`, watches `{base}/outbox.jsonl` for outbound lines, and writes `{base}/state.json`.
- **Muse** opens a page, joins the same channel, and can send plain chat or protocol JSON (task / opinion / result). It also GETs the room board and renders it. That fetch does not write.
- **Room board** (optional, committed): `boards/<room>.jsonl` is the shared task board, decision log, and scratch pad for one room. The Muse page only reads it.
- **Status view** (optional): `tools/status.py` turns an inbox log into `docs/status.json`, and `docs/status/` renders it. Publishing fails closed (see below). It is not the room board, and it is not a knowledge graph.

Wire format: [docs/protocol.md](docs/protocol.md).

## Quick start — Chief (desktop)

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download). Chief.Bridge is the only bridge in this repo; the old Python bridge was removed (see CHANGELOG).

```bash
cp config.example.json config.json
# edit channel + nick (and optionally base and pass)

export PATH="$HOME/.dotnet:$PATH"   # if needed

# Run it straight from the repo:
dotnet run --project src/Chief.Bridge

# …or install it as a .NET tool, which puts `chief-bridge` on your PATH:
dotnet pack -c Release src/Chief.Bridge
dotnet tool install --global --add-source src/Chief.Bridge/bin/Release Chief.Bridge
chief-bridge --config /path/to/config.json
```

CLI helpers (same binary):

```bash
# With the tool installed, from anywhere, pointing at a specific deployment:
chief-bridge status --config /path/to/config.json
chief-bridge say --config /path/to/config.json "hello from chief"
chief-bridge --config /path/to/config.json          # run the bridge

# …or straight from the repo:
dotnet run --project src/Chief.Bridge -- status
```

`--config <path>` (or `--config=<path>`) works before or after the subcommand. Use `--` to end options when the chat text itself starts with `--`, for example `say -- --config is literal`.

Config resolution:

| Command | Order |
|---|---|
| bridge run | `--config` or the first argument → `MUSE_RELAY_CONFIG` → `./config.json` → `./config.example.json` (prints a warning) |
| `say`, `status`, `watch`, `hook` | `--config` → `MUSE_RELAY_CONFIG` → `./config.json`. Nothing else. |

- `./` means the current working directory. No parent directories and no app directory are searched, so a stray `config.json` elsewhere is never picked up. (Before the Unreleased fixes, the bridge also walked up to five parent directories and checked the app directory, and `say`/`status` ignored any path you gave them.)
- An explicit path, or `MUSE_RELAY_CONFIG`, that points at a missing file is an error (exit code 2). It never falls through to another file.
- `say` prints the outbox it wrote to, and `status` prints the config it read, so you can see which deployment you touched.

Runtime files (`inbox.jsonl`, `outbox.jsonl`, `unread.jsonl`, `state.json`) live under `base` from config (default: directory of the config file). **Do not commit them.**

### How the bridge behaves

- **Join.** After connecting, the bridge sends `join` and waits for hack.chat's `onlineSet`. Only then does `state.json` say `connected: true`, and only then does it start sending outbox lines. A `warn` before `onlineSet` (for example `Nickname taken`) counts as a rejected join. So does no `onlineSet` within 15 s. Either way the bridge disconnects and retries.
- **Reconnect.** The process does not stop because a connection failed. DNS errors, connection refused, TLS failures, a handshake that never finishes (abandoned after 20 s), a server close (including during the join), a join `warn` of any text, and a missed `onlineSet` are all retried until the process is stopped. The delay starts at 1 s and doubles to a 30 s cap, then a random factor between 0.8 and 1.2 is applied, and the wait is still never more than 30 s. It goes back to 1 s after any session that was confirmed by `onlineSet` or stayed up for 60 s. Each attempt is logged (attempt number, reason, whether the join was confirmed) with the trip password redacted. Each attempt also uses its own HTTP handler, so one bad handshake cannot stick the next one to a shared connection pool.
- **What does stop the bridge.** A bad config exits immediately (exit code 2) instead of retrying: the config file is missing or isn't JSON, `channel` or `nick` is empty, `auto_ack` is enabled but invalid, or `url` is not an absolute `ws://` or `wss://` URI. Those cannot succeed on the next try. SIGINT / SIGTERM stops it cleanly (exit 0) after writing `state.json` with `alive: false`. A warn from the server, including one that says the nick is illegal, is **not** treated as a bad config: the same channel carries rate limits and transient kicks, and guessing which text is permanent is how a bridge gives up during an outage.
- **Outbox.** The bridge sends only complete, newline-terminated lines. A line still being written waits for its newline. The read position moves past a line only after its send succeeds. If a send fails, the session ends and the line is sent again after the reconnect. The position lives in memory for the life of the process. Lines already in `outbox.jsonl` when the bridge starts are **not** replayed, and neither are lines left unsent when it stops. A line whose send was cut off at the exact moment of a drop can, in principle, arrive twice.
- **Shutdown.** SIGTERM or Ctrl+C (SIGINT) closes the socket, writes `state.json` with `alive: false`, and logs `[chief] stopped`. A second signal isn't intercepted, so the runtime's default handling ends the process if shutdown ever hangs.
- **`state.json`** holds `alive`, `connected`, `reconnecting`, `at` (unix seconds), `channel`, `nick` and `pid`. It is written atomically (temp file, then rename). `status` reports whether that pid is still running. If the file says `alive: true` but the pid is gone, `status` calls the state stale.
- **`status`** also prints the hook poller's state and its last fire (see "Webhook poller" below), how many chats `watch` hasn't drained yet (`undrained`, read-only; `--state <offset file>` for a non-default offset), and whether auto-ack is on. Any other argument is now a usage error (exit 2); before, extra arguments were ignored.
- **Auto-ack** (optional, off by default): an instant `(auto) got it…` line when a trusted trip addresses the bridge. See "Auto-acknowledgement" below.
- **Logs.** `inbox.jsonl` records every frame in and out. Frames that aren't JSON objects are logged as `{"raw": "..."}` and otherwise ignored. The join is logged without the pass. Any outbound `pass` field and the `token` in hack.chat's `session` frame are logged as `<redacted>`. JSON is written with a relaxed encoder, so `'` and non-ASCII text stay readable, for example `café ✓ 日本`.

Unit tests: `dotnet test MuseChiefRelay.sln` (xunit, `tests/Chief.Bridge.Tests`).

### Watching the inbox

`watch` turns the inbox into an event feed. It prints new inbound chats as a JSON array and remembers where it stopped:

```bash
chief-bridge watch --config /path/to/config.json
# [{"nick":"Alex","trip":null,"text":"hello","ts":1790468200}]

chief-bridge watch --config /path/to/config.json --wait --timeout 1800   # block until something arrives
```

- **What counts:** inbound `chat` frames (`dir: "in"`), minus the bridge's own nick. The nick comes from the config; override it with `--nick`. Everything else is skipped: other frame types, outbound copies, blank and malformed lines. Each item is `{nick, trip, text, ts}`; `trip` is `null` when the sender has no tripcode (hack.chat omits the field).
- **Offset:** default `<base>/.inbox_watch.offset`, or pick one with `--state <file>`. It's a byte offset that always sits on a line boundary. Only complete, newline-terminated lines are consumed, so a line the bridge is still writing is read on the next run, not skipped. The file is written atomically. Run one watcher per offset file. If the offset file can't be used (not a bare number and not the `{"offset":…,"head":…}` JSON that `watch` writes, including JSON without a valid `head`), `watch` prints a warning on stderr (with `--wait` too, when it exits) and starts over from the end of the inbox, like a first run.
- **First run** only records the offset and prints `[]`, so history never floods the first poll. A missing inbox prints `[]`, and once it appears, everything in it counts as new.
- **Truncated or rotated inbox:** if the file is shorter than the offset, or its first bytes changed, the offset goes back to 0 and the new contents are reported.
- **Order:** the array is printed before the offset is saved. A crash in between repeats a message instead of losing it.
- **`--wait`** blocks until at least one new chat qualifies. It wakes on file-system events and polls every second as a fallback. Then it prints the array and exits 0. With `--timeout <seconds>` (0 to 922337203685) it gives up, prints `[]` and exits **3**. On SIGTERM or SIGINT it prints `[]` and exits 143 or 130. Usage and config errors exit 2, as does a one-shot run whose inbox can't be read. A missing inbox is an empty poll; a path that is there but isn't a readable file (a directory, or no permission) is exit 2.
- **Transient read failures:** if a poll can't read the inbox (a torn read, a locked file), `watch --wait` doesn't die: it records a warning, printed on stderr when the wait ends, and retries on the next loop.
- **Ways to run it.** An agent woken by the webhook poller (below) runs plain `watch` to drain everything new, which is what chief does now. A scheduler can poll `watch` every few seconds. An agent that is woken when a background command finishes can run `watch --wait` in the background and start it again after each exit, but chief found that wake unreliable (see `agents/chief.md`). Messages that arrive in between are waiting for the next run.
- **Replying:** use `say` (`chief-bridge say --config <path> <text>`), or append `{"cmd":"chat","text":"..."}` lines to `outbox.jsonl`.
- `watch` only reads `inbox.jsonl` (and writes its own offset file), so it's safe to run next to a live bridge.

### Webhook poller (`hook`)

`hook` is a small always-on process that turns new chats into a webhook call, for an agent that only runs when something calls it:

```
hack.chat ──► Chief.Bridge (always on) ──► inbox.jsonl ──► Chief.Bridge hook (always on) ──POST──► webhook ──► agent wakes
                     ▲                                                                                              │
                     └───────────── outbox.jsonl ◄──── say ◄──── reply ◄──── drain with `watch` ◄───────────────────┘
```

```bash
export CHIEF_HOOK_URL=…  CHIEF_HOOK_AUTH=…       # from your secret store; never in config.json
Chief.Bridge hook --config /path/to/config.json   # runs until SIGTERM / Ctrl+C
Chief.Bridge hook --config /path/to/config.json --test   # one fake chat, prints e.g. "hook test: HTTP 200"
```

Config (`hook` block; omit it and `hook` refuses to run):

```json
"hook": {
  "url_env": "CHIEF_HOOK_URL",
  "auth_env": "CHIEF_HOOK_AUTH",
  "auth_scheme": "Bearer",
  "poll_s": 5,
  "cooldown_s": 15,
  "trips": ["Ab12Cd", "Xy34Zw"]
}
```

| Field | Default | Meaning |
|---|---|---|
| `url_env` | `CHIEF_HOOK_URL` | **Name** of the environment variable holding the webhook URL. The URL itself never goes in the config. It must be `https://`, or `http://` to a loopback address (127.0.0.1, ::1, localhost). |
| `auth_env` | `CHIEF_HOOK_AUTH` | **Name** of the variable holding the bare key. `""` sends no `Authorization` header. |
| `auth_scheme` | `Bearer` | Sent as `Authorization: <scheme> <key>`. `""` sends the bare key. |
| `poll_s` | `5` | Fallback poll interval (0.1–300). File-system events usually wake it sooner, so a chat typically fires in well under a second. |
| `cooldown_s` | `15` | Minimum gap between fires (0–3600). The first chat after a quiet spell fires at once. Chats inside the gap go out together in the next fire. |
| `max_retry_s` | `120` | A failed fire is retried after `max(cooldown_s, 1)`, doubling up to this |
| `timeout_s` | `15` | Per-request timeout |
| `trips` | `[]` | Only chats from these trips fire the hook, and only they are sent. `[]` means every sender except the bridge's own nick. A leading `!` is dropped. |
| `state` | `.hook.offset` | Offset file (relative to `base`); the status file is this plus `.status`. Both are gitignored. |
| `source` | `chief-bridge-hook` | The payload's `source` field |
| `max_text` / `max_batch` | `2000` / `50` | Chat text is cut to `max_text` characters; at most the newest `max_batch` chats go in one fire |

Payload (`Content-Type: application/json`), the same shape as the local `hookpoll.py` it replaces:

```json
{"source":"chief-bridge-hook","channel":"your-channel-name","chats":[{"nick":"Alex","trip":"Ab12Cd","text":"hello chief","ts":1790500100}]}
```

`"omitted": n` is added when more than `max_batch` chats were waiting.

How it behaves:

- **Reading** uses the same code as `watch`, with its own offset file: byte offsets on line boundaries, a half-written line waits for its newline, a truncated or rotated inbox starts over from 0, and the first run starts at the end so history never fires. The bridge's own nick is skipped, as are other frames, outbound copies and malformed lines.
- **Deliver first, save second.** The offset moves past a batch only after a **2xx**. Chats held by the cooldown or a failed fire stay on disk past the saved offset, so a restart or a crash sends them instead of losing them. A crash between the 2xx and the save can send one batch twice. (The Python `hookpoll.py` saved the offset first and kept pending chats in memory only.)
- **Failures** (any non-2xx, a timeout, a network error) are retried with backoff: `max(cooldown_s, 1)`, doubling, capped at `max_retry_s`. Redirects aren't followed, so a 3xx counts as a failure, and the key is never re-sent to another host.
- **One poller per offset file.** `hook` takes an exclusive lock on `<state>.lock` and holds it until the process exits (a crash releases it). A second `hook` on that offset exits **4**. The status file is a report, not the lock: two processes can both read "not running" before either writes a heartbeat.
- **SIGTERM / Ctrl+C** stops it cleanly: status `stopped`, exit 0. Anything not yet delivered stays queued in the inbox for the next start.
- **Exit codes:** 0 stopped cleanly or `--test` OK, 2 usage/config error (including a missing environment variable), 4 already running, 5 `--test` got a non-2xx or no answer.
- **Output** is one line per fire, for example `[chief] hook: fired 2 chat(s) -> HTTP 200`, never the URL or the key. Network errors are reported by kind only (`error ConnectionError`, `timeout`), because exception messages can contain the host.

**Status.** The poller writes `<state>.status` atomically: pid, `running`/`stopped`, a heartbeat every 5 s (also while a webhook request is in flight, so a slow endpoint up to `timeout_s` does not look like a dead poller), the last fire (time, `HTTP 200` or an error kind, chat count), pending chats, consecutive failures, next retry, and ok/failed counters. `status` (and so `hc status`) shows:

```
hook: running (pid 402405, heartbeat 2s ago, poll 5s, cooldown 15s, 2 trusted trip(s))
hook last fire: 05:05:12 (40s ago): HTTP 200, 2 chat(s); 0 pending; 7 ok / 0 failed since start
hook: FAILING: last 3 fire(s) failed (last: HTTP 401), next retry in 1m; pid 402405, …
hook: NOT RUNNING: pid 402405 died without a clean stop (last heartbeat 6m ago)
hook: NOT RUNNING: stopped 12s ago (pid 402405)
undrained: 2 chat(s) not yet read by watch (oldest from Alex at 05:05:10)
```

A heartbeat older than `max(15 s, 3 × poll_s + 5 s)` counts as NOT RUNNING. `unknown` means a hook block exists but the poller has never run; `not configured` means there's no hook block.

### Auto-acknowledgement

The agent behind the bridge only acts once something wakes it, so its real reply takes a while. The bridge can cover that gap by answering straight away when a **trusted trip** addresses it. Off unless you enable it:

```json
"auto_ack": {
  "enabled": true,
  "mention_trips": ["Ab12Cd"],
  "task_trips": ["Xy34Zw"],
  "cooldown_s": 60
}
```

| Field | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Turn it on |
| `mention_trips` | `[]` | Trips (people) whose messages get an ack when they name the bridge's nick or give it a task. A leading `!` is dropped. |
| `task_trips` | `[]` | Trips (other agents) whose messages get an ack **only** for a task addressed to the bridge. Their plain chat never does, so agent chatter can't start a loop. |
| `cooldown_s` | `60` | At most one ack per this many seconds, across all senders. Minimum 10. |
| `max_per_hour` | `20` | At most this many acks in any rolling hour |
| `text` | `(auto) got it, thinking…` | For a mention. `{from}` is replaced by the sender's nick. |
| `task_text` | `(auto) got task {id}, thinking…` | For a task. `{id}` is the task id (or `?`). |
| `offline_text` | `(auto) got it, but chief's wake-up hook isn't working right now, so the reply may be late` | Sent instead when the hook poller's status is **NOT RUNNING** or **FAILING**, so the ack never promises a reply nothing will wake the agent for. `running`, `unknown` and `not configured` get the normal text. |

- **Addressed** means the nick as a whole word, case-insensitive (`chief`, `@chief`, `chief's`, but not `chiefly` or `Chief.Bridge`), a JSON task with `"to"` equal to the nick, or `TASK to <nick>: …`. Other protocol lines (`ack`, `result`, `opinion`, `ping`, tasks for someone else) never count, even if they mention the nick.
- **Never** for the bridge's own nick (any case), its own trip (learned from `onlineSet`), untripped senders, or trips on neither list. Nicks aren't identity.
- The ack is plain chat, **not** a protocol `ack`: it doesn't change task state or the status view. It's sent through the same socket as the outbox, ahead of queued outbox lines, within about a second, and only while the join is confirmed. It's best effort: if a send fails, the ack is dropped, not retried after the reconnect.
- It's logged in `inbox.jsonl` as an `out` row with `"auto":"ack"`. A trusted, addressed message that was held back by the cooldown or the hourly cap is logged as `{"dir":"note","msg":{"auto_ack":"suppressed","reason":"cooldown","to":"<nick>"}}`.
- Enabling it with both trip lists empty, `cooldown_s` under 10, `max_per_hour` under 1, or an empty or over-300-character text is a config error (exit 2), for every command that loads the config.

## Configuration (`config.json`)

Copy `config.example.json` to `config.json`. `config.json` is gitignored. Keep it that way, because it can hold your pass.

| Field | Used by | Meaning |
|---|---|---|
| `url` | bridge | hack.chat WebSocket endpoint, normally `wss://hack.chat/chat-ws` |
| `origin` | bridge | Origin header sent on connect (`https://hack.chat`) |
| `channel` | bridge | Channel to join. Anyone who knows the name can read it. |
| `nick` | bridge | Nick for the bridge, e.g. `chief` |
| `pass` | bridge | Optional hack.chat password. It gives the nick a **tripcode**. It is sent only in the join frame and is never written to logs. |
| `base` | bridge | Directory for runtime files. Default: the config file's directory. |
| `auto_ack` | bridge | Optional instant acknowledgement for trusted trips. Off by default. See "Auto-acknowledgement". |
| `hook` | `hook` | Webhook poller settings: env var **names** for the URL and key, poll, cooldown, trip filter. See "Webhook poller". |
| `publish_repos` | `tools/status.py` | Allowlist of `owner/name` repos whose tasks can appear in the status view. Default: this repo. Compared case-insensitively. |
| `publish_trips` | `tools/status.py` | Tripcodes allowed to publish. Every task, ack and result must carry one, **including the bridge's own**. An empty or missing list publishes nothing. |

### Tripcodes and the pass

hack.chat derives a short tripcode (e.g. `Ab12Cd`) from the password you join with. The same password always gives the same trip, and nobody else can produce it without the password. Nicks are first come, first served, so anyone can join as `chief` or `Muse`. **Trust the trip, not the nick.**

- Give the bridge a trip by setting `pass`. Treat the pass like a password: keep it out of chat, logs, commits and screenshots.
- Put the bridge's own trip in `publish_trips`, or its acks and results won't count.
- Give Muse a trip by filling in the client's optional **Password** field when you join (details below). Without it, Muse's messages carry no trip: they won't count for publishing, and operators who gate commands on trips will treat them as chat.

## Status view and fail-closed publishing

```bash
python3 tools/status.py --config config.json --inbox inbox.jsonl --out docs/status.json
python3 tools/test_status.py        # unit tests
```

- Only tasks with a `repo` on the `publish_repos` list are published. Untagged tasks, and tasks for any other repo, are left out. A private-repo task stays private simply by leaving `repo` off.
- Every counted message must carry a trip in `publish_trips`. An empty list means nothing is published, so a fresh fork shows nothing until it's configured.
- Shortcut tasks (`TASK to chief: ...`) have no id or repo, so they never show up.
- The output includes `coverage`: the windows the bridge was actually connected. Anything said outside them is missing.
- `docs/status/` renders the file. `docs/status/?demo` shows a bundled fixture (`docs/status/sample.json`, built by `status.py` from a synthetic log).
- A real `status.json` is a public artifact. Even with zero tasks it reveals when the relay was online. **Don't commit one without the repo owner's OK.** This repo currently ships the fixture only.

## Room board, public status page, and the private Voizle knowledge graph

These are three different stores. They do not feed each other. Detail: [boards/README.md](boards/README.md) and [docs/protocol.md](docs/protocol.md).

**Room board** (`boards/<room>.jsonl`). Committed room state for the humans and agents in one hack.chat room: tasks, decisions, and scratch. This room's file is `boards/fuse-grok-6f4e970cd8.jsonl`. It is public the way the CHANGELOG is public (anyone with the repo can read it). It is not a place for secrets. Schema: `boards/schema.json`. A later task line with the same `id` replaces the earlier card. Updates are commits. The Muse page only reads the file.

**Public status page** (`docs/status/`, merged PR #3). `tools/status.py` builds `docs/status.json` from an inbox log and the page renders that file. Publishing fails closed: a task appears only when its `repo` is on `publish_repos` (default `signalnotnoise/muse-chief-relay`) and every counted message carries a trip on `publish_trips`. Untagged tasks and tasks for any other repo are left out. Shortcut tasks never appear. A real `docs/status.json` is a public artifact even when it lists zero tasks (`coverage` still shows when the relay was online). This repo ships the fixture only.

**Private Voizle knowledge graph.** Alex's knowledge graph is the Voizle KG. It lives locally under his Voizle work and is produced there (`generate-graph.js`, `graph-data.js`). It is not part of muse-chief-relay. It must never be published into `docs/status.json`, the public status page, a room board, or any other file in this repository. Do not add a Voizle repo to `publish_repos`. Do not paste graph nodes, edges, or generated `graph-data.js` output here. The graph stays on Alex's machine.

A chat task (`{"type":"task","to":"chief",...}` on hack.chat) is not a board row, and a board row is not a status-page task. The status page never reads `boards/`, and the board never reads `status.json`.

## Quick start — Muse (browser)

No build step. Open the static client:

- Double-click / open `web/muse/index.html` in a browser, **or**
- Serve the folder: `python3 -m http.server 8080 --directory web/muse` then visit `http://localhost:8080/`

Type the same channel as Chief (the `channel` in its `config.json`; examples here use `your-channel-name`). The Channel box starts empty and Connect refuses a blank one with a message under the field. The client has no built-in channel, doesn't remember one between visits and never puts it in the URL, because anyone who knows a channel name can read it. The nick defaults to `Muse`. hack.chat WSS works from `file://` and any static HTTPS host. The same client is published at `docs/muse/`. Keep `web/muse/` and `docs/muse/` identical.

The page shows the room board above the join form: tasks, decisions, and scratch from `boards/fuse-grok-6f4e970cd8.jsonl` by default. The fetch is `GET ../../boards/<room>.jsonl`. That path resolves when the site is served from the repository root:

```bash
python3 -m http.server 8080
# http://localhost:8080/web/muse/   or   http://localhost:8080/docs/muse/
```

`python3 -m http.server 8080 --directory web/muse` still runs the chat client. The board request then 404s and the panel says the file is missing; chat is unchanged. GitHub Pages publishes `docs/` only, so the same missing state shows on the public client until the site root includes `boards/`. `?board=<room>` selects another file whose name is one segment of letters, digits, `.`, `_`, or `-`. The page never writes the board.

### Getting a trip in Muse (optional password)

1. On the join screen, fill in **Channel**, **Nick** (e.g. `alex`) and **Password (optional, for a trip)**. Pick a password you don't use anywhere else and leave `#` out of it (hack.chat ignores everything after a second `#`).
2. Press **Connect**. Once hack.chat confirms the join, the transcript shows `joined as alex !Ab12Cd` and the sidebar shows the trip under *trip*. Without a password you'll see `joined as alex (no trip)`.
3. Tell Chief's operator that trip (out-of-band, not just in the channel) so it can go on the trusted list. The same password always gives the same trip, from any browser.

What happens to the password:

- It's sent to hack.chat once per join, inside the join frame as `name#password` (hack.chat's own trip syntax), and nowhere else.
- It is **never shown**: the field is masked and emptied as soon as you press Connect, and every echo (join line, sidebar, online list) shows only the name.
- It is **never logged or stored**: no console output, no `localStorage`/`sessionStorage`/cookies, nothing in the URL.
- It stays in memory in a single JS variable for the life of the tab, so an automatic rejoin (dropped socket, tab back in view, network back) keeps the same trip. **Disconnect**, a first join that is rejected for good, or closing/reloading the tab forgets it; after that you type it again. A drop after you have successfully joined does not.
- Old habit, `alex#password` in the Nick box? That still works. As soon as you type the `#`, the rest moves into the masked Password field. Only `alex` is ever displayed.
- hack.chat's session token (which can restore your trip without the password) is never shown either.
- Your browser's password manager may offer to save it. That's your call and your browser's storage, not the page's.

Full details and limits: [docs/security.md](docs/security.md#muse-web-client-password-handling).

<!-- MUSE-SIDE SECTION: written by Fuse (usage, reconnect behavior, rejected joins). -->
Reconnect in brief: if the socket drops, the client retries with exponential backoff (1 s doubling to a 30 s cap, ±20% jitter). It retries immediately when the tab becomes visible again or the browser comes back online. If hack.chat rejects the join, the client never sits "connected" outside the channel. **After a successful join, every later rejection is retried** until you press Disconnect (a taken nick is usually your own stale session, and any other warn during an outage is treated the same way). On the very first join, a nick-taken or rate-limit warning is retried at most 3 times, and any other rejection (an invalid nick, a bad channel) shows "join rejected" and stops. Those 3 are join warnings only: socket closes before the first `onlineSet` do not spend them, and a socket that closes before that first join keeps retrying. That isn't bad input. The decision lives in `reconnect.js`, loaded before `app.js`. `docs/muse/` and `web/muse/` are kept identical, including that file.

## Collaboration protocol

See [docs/protocol.md](docs/protocol.md).

Examples (send as the **entire** chat message text):

```json
{"type":"task","id":"t1","to":"chief","title":"Check open PRs","body":"List open PRs on signalnotnoise for today"}
```

```json
{"type":"result","id":"t1","from":"chief","status":"done","summary":"2 open PRs"}
```

```json
{"type":"opinion","from":"muse","topic":"bridge","text":"WSS on 443 is the right default"}
```

## Layout

| Path | Role |
|------|------|
| `src/Chief.Bridge/` | The desktop WSS bridge (.NET 8); the only bridge in this repo |
| `tests/Chief.Bridge.Tests/` | xunit tests for the bridge's outbox reader, frame handling, config, CLI, inbox watcher, webhook poller (against a local HTTP listener) and auto-ack |
| `agents/chief.md` | Relay instructions for the chief agent: watch loops, replying, protocol, authority, trust |
| `web/muse/` | Primary Muse browser client (chat, plus a read-only room board) |
| `docs/protocol.md` | Wire protocol, and how it differs from the board and the status page |
| `docs/security.md` | Trust model: trips, pass handling, what needs a human, and what must stay off the public page |
| `docs/index.html` | Landing page (GitHub Pages root) |
| `docs/muse/` | Published copy of the Muse client (keep identical to `web/muse/`) |
| `docs/status/` | Public status panel (renders `docs/status.json`; `?demo` for the fixture). Fail-closed. Not the room board. |
| `boards/` | Committed room boards (`boards/<room>.jsonl`), schema, and the board / status / Voizle boundary |
| `tools/status.py` | Fail-closed status generator (+ `test_status.py`) |
| `config.example.json` | Config template (see Configuration) |
| `CHANGELOG.md` | What changed, by PR |

## Safety

- Public demo channels are as private as their names.
- Do not put tokens, cookies, or personal data in chat payloads.
- Treat task bodies as untrusted input before running shell or account actions.
- Trust tripcodes, not nicks. Keep a human in the loop for merges, deploys, posts and spending.

Full trust model: [docs/security.md](docs/security.md).

## License

MIT
