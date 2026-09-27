# Changelog

Merged work, newest first. Times are ET.

## Unreleased

- **Tool packaging + `watch` hardening** (open, #14). `Chief.Bridge` is installable as a .NET tool:
  `dotnet pack` produces `Chief.Bridge.0.1.0.nupkg`, and `dotnet tool install --global Chief.Bridge`
  puts a `chief-bridge` command on the PATH. `watch --wait` retries a transient inbox read (a torn read,
  a locked file) instead of exiting. A one-shot `watch` that can't read the inbox exits 2. A missing inbox
  is still an empty first poll; a path that exists but isn't a readable file (for example `inbox.jsonl` is a
  directory) is that exit 2.
- **#13 Chief replies like Fuse: webhook hook poller replaces the wake-on-exit listener** (open). Fuse replies
  in 10–20 s because a 5 s poll script wakes a fresh worker for every inbound chat. chief took about a minute, and
  sometimes never woke: it relied on a background `watch --wait` exiting to wake it, and on 2026-09-27 at
  05:05 that watcher did exit 0 with Alex's "hello", but the wake never reached chief. So the wake now comes
  from a webhook, like Fuse's loop: bridge → `hook` poller → webhook routine → chief drains with `watch` →
  reply via the outbox.
  - **`Chief.Bridge hook`**, a C# port of the local `hookpoll.py` stopgap. It watches `inbox.jsonl`
    (file-system events plus a `poll_s` poll, default 5 s) and POSTs `{"source","channel","chats":[…]}` to
    the webhook with `Authorization: Bearer <key>`. The first chat after a quiet spell fires at once; later
    ones are batched by `cooldown_s` (default 15). Optional `trips` filter; the bridge's own nick is always
    skipped. The URL and key come **only** from environment variables (`url_env`/`auth_env`, default
    `CHIEF_HOOK_URL`/`CHIEF_HOOK_AUTH`) and are never logged, written to status, or put in the config.
    https is required except to loopback, redirects aren't followed. Improvements over `hookpoll.py`: the
    offset moves only after a 2xx (the Python one saved it first, so a crash or restart lost held chats);
    exponential retry backoff capped by `max_retry_s`; handles a truncated or rotated inbox and half-written
    lines through the same reader as `watch`; clean SIGTERM (status `stopped`, exit 0); refuses to start
    (exit 4) while another poller holds the exclusive lock on the same offset (`<state>.lock`, released on
    exit or crash; the status file is not the lock); heartbeats during an in-flight webhook request so a slow
    POST (up to `timeout_s`) is not reported as NOT RUNNING; `--test` sends one fake chat.
  - **Hook status** `<base>/.hook.offset.status` (heartbeat every 5 s, last fire time, HTTP result, count,
    pending, failures, next retry). `status` (and so `hc status`) prints
    `hook: running | FAILING | NOT RUNNING | unknown | not configured` and `hook last fire: …`, plus
    `undrained: N chat(s) not yet read by watch` (read-only) and whether auto-ack is on. New
    `status --state <file>` for a non-default watch offset. Unknown arguments to `status` are now a usage
    error.
  - **Auto-acknowledgement** (`auto_ack`, off by default, from #12) is kept. When a
    trusted trip addresses the bridge, the bridge itself posts `(auto) got it, thinking…` (or
    `(auto) got task <id>, thinking…`) in well under a second. `mention_trips` (people) trigger it with a
    mention or a task, `task_trips` (agents such as Fuse) only with a task, so there's no bot loop. Global
    `cooldown_s` (default 60, minimum 10) and `max_per_hour` (default 20). When the hook poller is NOT
    RUNNING or FAILING it sends `offline_text` instead (`…chief's wake-up hook isn't working right now, so
    the reply may be late`), keyed on the hook poller rather than #12's watch listener. The default texts
    drop "full reply in about a minute". `auto_ack.watch_state` is gone (an old config that still has it
    loads fine; the field is ignored).
  - **Removed** from #12, because they only served the wake-on-exit listener the hook replaces:
    `watch --settle` (the hook's cooldown batches bursts now), the watch status file with
    `listener: armed | waking | NOT ARMED` in `status`, the "another watcher is armed" warning, the
    `re-arm now` stderr line on timeout, and `auto_ack.watch_state`. `watch` itself is back to how it was
    before #12; chief now uses it without `--wait` to drain.
  - Docs: README (Webhook poller section, auto-ack table), `agents/chief.md` (the new loop and why
    `watch --wait` was dropped, checking the wake-up path), `docs/protocol.md` (local webhook payload,
    `(auto)` lines), `docs/security.md` (webhook secret handling), `config.example.json` (`hook` block).
    `.hook.offset*` is gitignored.
  - 185 tests (167 after #12): 50 new for the hook, against a local HTTP listener; the watch-status and
    `--settle` tests went with those features.
- **#12 Chief responsiveness: bridge auto-ack, `watch --settle`, listener status** (merged 2026-09-27 05:25). chief only acts
  when a background `watch --wait` exits and wakes it, so every reply costs a full wake (about a minute),
  and a missed re-arm goes unnoticed. On 2026-09-27 a listener exited and wasn't re-armed, and Alex's
  hello and a task sat for several minutes until he asked. This doesn't make chief think faster. It makes
  the wait visible and harder to lose.
  - **Auto-acknowledgement** (`auto_ack` in `config.json`, off by default). When a trusted trip addresses
    the bridge, the bridge itself posts `(auto) got it, thinking… full reply in about a minute`, or
    `(auto) got task <id>, …` for a task, in well under a second (about 0.16 s in the live test).
    `mention_trips` (people) trigger it with a mention or a task. `task_trips` (agents such as Fuse) trigger it
    only with a task addressed to the bridge, never with plain chat, so there's no bot loop. It never fires for
    the bridge's own nick or trip, untripped senders, or unlisted trips. There's a global `cooldown_s`
    (default 60, minimum 10) and `max_per_hour` (default 20). When the watch status says no listener is
    armed, it sends `offline_text` instead (`…chief's listener isn't armed right now, so the reply may be
    late`). It's plain chat, not a protocol `ack`, so task state and the status view are unchanged. Sent
    rows are logged with `"auto":"ack"`; held-back ones as `note` rows.
  - **`watch --wait --settle <s>`** (0–60 s, off by default). After the first chat, `watch` keeps collecting
    until the burst has been quiet for `<s>` seconds (capped at 4 × `<s>`), so "hello" plus a question comes
    back as one wake instead of two. Chats already in hand are delivered even if the timeout or a signal
    lands during the window. `agents/chief.md` now uses `--settle 3`.
  - **Watch status file** `<offset file>.status`: `armed`/`settling` with a 5 s heartbeat, then `delivered`,
    `timed_out`, `stopped` or `polled`, plus pid, deadline, exit code and count. Written atomically.
    `--wait` warns if another live watcher is armed on the same offset file. A timeout now also prints
    `re-arm now` on stderr.
  - **`status`** (and so `hc status`) prints `listener: armed | waking | NOT ARMED | unknown` with the
    reason: dead pid, stale heartbeat, or how long ago the last watch exited. It also prints how many chats
    are waiting past the saved offset (read-only) and whether auto-ack is on. New `status --state <file>`.
    Unknown arguments to `status` are now a usage error.
  - Docs: README (auto-ack config table, listener table, `--settle`, status file), `agents/chief.md` (settle,
    "exit 3 is a wake too", checking the listener, what the auto-ack means for chief), `docs/protocol.md`
    (`(auto)` lines are receipts, not protocol acks), `docs/security.md` (how auto-ack uses trips; also fixes
    the garbled "Fuse is alex confirmed…" sentence), `config.example.json` (disabled `auto_ack` block).
  - 85 new tests (167 total).
  - Superseded in part by #13: `--settle`, the watch status file and the listener status were removed
    again, and the auto-ack offline check now looks at the hook poller.
- **#11 Muse: masked password field for trips, and no default channel** (merged 2026-09-27 04:37). Both `docs/muse/` and
  `web/muse/` (kept identical).
  - New optional **Password (optional, for a trip)** field (`type="password"`,
    `autocomplete="current-password"`). On join the client sends `nick#password` only when a password
    is set, and shows your own trip once `onlineSet` reports it (`joined as alex !Ab12Cd`, plus a *trip*
    line in the sidebar; `(no trip)` otherwise).
  - The password is never displayed, logged, stored (`localStorage`/`sessionStorage`/cookies) or put in
    the URL. The field is cleared on Connect; the password lives in one closure variable so automatic
    rejoins (backoff, tab visible, back online) keep the same trip. Disconnect, a permanently rejected
    join, or closing the tab forgets it.
  - A legacy `name#password` typed into the Nick box moves everything after the `#` into the masked field
    as you type; at submit, any leftover `name#password` is split and the Nick box reset to the name.
    Only the name is echoed. Before, the password was visible while typing and echoed back in
    `joining #… as name#password`.
  - The channel field no longer defaults to the deployment's channel. It starts empty (placeholder
    `your-channel-name`), and a blank Connect is refused with a message under the field. The client
    doesn't remember the channel or write it to the URL (it never did either). The real channel name was
    also removed from the README, `docs/protocol.md` and `agents/chief.md`.
  - hack.chat's `session` frame is no longer dumped into the transcript. It carries a resumable session
    token that restores your nick **and trip** without the password, and the client used to print it on
    screen as raw JSON. Any other unrecognised frame is shown with `token`/`pass`/`password` redacted.
  - README and `docs/security.md` document how to get a trip, what happens to the password, and the
    limits (hack.chat ignores anything after a second `#` in the password).
- **#9 Inbox watcher: `Chief.Bridge watch`** (landed on main via #10, merged 2026-09-26 22:53). Adapted from Fuse's original patch
  (`tools/watch_inbox.sh`) as a C# subcommand, so the bridge side stays on the .NET solution. It prints
  new inbound chats from `inbox.jsonl` as a JSON array of `{nick, trip, text, ts}`, skips the bridge's own
  nick, and keeps an offset file (default `<base>/.inbox_watch.offset`). The first run bootstraps silently
  with `[]`. Changes from the script: the offset is in bytes and only complete lines are consumed, so a
  half-written line is never skipped. The offset file is written atomically. Rotation is detected by
  hashing the file's first bytes. A new `--wait [--timeout <s>]` mode blocks until a chat arrives, exits
  3 on timeout and 143/130 on SIGTERM/SIGINT. It's for agents that are woken when a background command
  finishes. An unusable offset file (including JSON without a valid `head`) prints a warning, also after
  `--wait`, and re-bootstraps. `--timeout` accepts 0 to 922337203685 seconds; anything else (NaN, `1e308`)
  is a usage error (exit 2). 39 new unit tests.
- **Agent instructions for chief: `agents/chief.md`.** Based on Fuse's original patch: the watch-and-reply
  loop (polling and `--wait` variants, using `Chief.Bridge watch`), replying via the outbox or `say`, no
  bot loops with Fuse, the protocol summary, what needs Alex, and the trust rules (Fuse's `!EtBBNv` is
  retired; Fuse is untripped until Alex confirms a new trip).
- **#8 Removed: legacy Python bridge** (landed on main via #10, merged 2026-09-26 22:53). Deleted `legacy/python/`, which held `bridge.py`,
  `bin/hc`, `parse_msg.py`, `test_pump.py`, `requirements.txt` and its README. Also deleted the root
  `relay_poll.py`, the operator poller for the old Python setup (it hard-coded `/workspace/hackchat`).
  Removed the matching `.gitignore` entries and all doc and landing-page references. Chief.Bridge is the
  only bridge. `tools/status.py` (the status publisher) stays. Past entries below still mention
  `legacy/` as history.
- **#7 Chief.Bridge reliability fixes** (merged 2026-09-26 20:10).
  - Reconnect delay: it now goes back to 1 s after a session confirmed by `onlineSet` or 60 s of uptime. Before, the reset line was unreachable, so after a few drops every reconnect waited 30 s for the rest of the process's life.
  - Clean shutdown on SIGTERM and SIGINT. `state.json` ends with `alive: false` and `[chief] stopped` is logged. Before, the process exited from the ProcessExit handler and left `state.json` saying `alive: true`.
  - Outbox: only complete, newline-terminated lines are sent, and the read position advances line by line only after a send succeeds. A line that fails to send is sent again after the reconnect. Before, the position moved before the send, so a line was lost if the send failed, and a half-written line could go out truncated.
  - UTF-8 is decoded once per whole WebSocket message. Before, it was decoded per fragment, which could corrupt a multi-byte character split across fragments.
  - `say` and `status` take `--config <path>` and honour `MUSE_RELAY_CONFIG`. They no longer fall back to parent directories or `config.example.json`. The bridge run no longer searches parent directories or the app directory. An explicit path that doesn't exist is now an error, not a silent fallthrough. See the README table.
  - `connected: true` only after `onlineSet`. A `warn` before it (e.g. `Nickname taken`), or no `onlineSet` within 15 s, counts as a rejected join and is retried with backoff. Before, the bridge could sit "connected" outside the channel.
  - Frames that aren't JSON objects are logged as `{"raw": ...}` and skipped instead of ending the session.
  - Relaxed JSON encoder, so `'` and non-ASCII text aren't `\u`-escaped.
  - hack.chat's `session` token, and any outbound `pass`, are logged as `<redacted>`.
  - The outbox is drained only after the join is confirmed. `state.json` is written atomically and includes `pid`, and `status` flags a stale state.
  - New xunit project `tests/Chief.Bridge.Tests` (43 tests).
- **Correction to #6.** PR #6's description said the .NET bridge wasn't affected by the outbox bug. That was only half right. Chief.Bridge didn't have the stale-pump race, because its pump belonged to one connection. It did have the related bug: the read position advanced before the send succeeded, so a line whose send failed was lost, and a half-written line could be sent early. The fixes above address this.

## 2026-09-25

- **#6 Legacy bridge outbox fix** (merged 07:41). After a reconnect, the old outbox pump thread in
  `legacy/python/bridge.py` kept running and raced the new one, dropping lines into the dead socket.
  Now a pump exits when its connection isn't live, and the position advances only after a send
  succeeds. Adds `legacy/python/test_pump.py`.
- **#5 Docs refresh** (merged 07:41). README, `docs/protocol.md`, new `docs/security.md` and this
  changelog.
- **#4 Status panel** (merged 06:13). `docs/status/` renders `docs/status.json`: per-state counts,
  task cards, and a coverage timeline. All values go in via `textContent`. `?demo` loads a fixture built
  by `status.py` from a synthetic log. Handles missing and malformed files. Ships the fixture only; no
  real-log status file is committed.
- **#3 Status view v1** (merged 04:41). Optional `repo` field on tasks. `tools/status.py` builds
  `docs/status.json` fail-closed: `publish_repos` allowlist (case-insensitive), every message needs a
  trip in `publish_trips` (the bridge's own included), and an empty list publishes nothing. Both
  bridges take an optional `pass` so they can have a trip. 13 unit tests in `tools/test_status.py`.
  The design came from alex's knowledge-graph idea, worked through with Fuse.
- **#2 Landing page** (merged 03:41). `docs/index.html` plus `docs/landing.css` at the GitHub Pages
  root, linking to the client already published at `docs/muse/`.
- **#1 Client auto-reconnect** (merged 03:41). Exponential backoff with jitter (1 s → 30 s). Immediate
  retry when the tab becomes visible or the browser comes back online. A rejected join no longer leaves
  the client showing "connected" outside the channel. Review nits from Fuse applied.

## 2026-09-24

- Initial relay: `Chief.Bridge` (.NET 8) desktop bridge, `web/muse` browser client, `docs/protocol.md`,
  the legacy Python bridge under `legacy/python/`, and the client published on Pages at `docs/muse/`.
