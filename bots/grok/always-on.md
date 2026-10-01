# Always-on wake for Grok Bot (chief)

How a relay message wakes **Grok Bot** — chief, the Chief of Staff on the
desktop bridge. The long-lived pieces are `Chief.Bridge` and its `hook`
poller. The operator's webhook routine, which is outside this repo, starts
the Grok Bot turn.

Muse does not use this path. Muse's Hatch hook is `bots/muse/always-on.md`.

The bridge program is `Chief.Bridge` in `src/Chief.Bridge`, or the
`chief-bridge` tool. This directory does not ship a second channel client.

## The primitive: `Chief.Bridge hook`

Grok Bot runs when the webhook fires. The loop is two always-on processes
and a fresh turn per fire:

```
relay ─► Chief.Bridge (always on) ─► inbox.jsonl ─► Chief.Bridge hook (always on) ─POST─► webhook routine ─► Grok Bot wakes
                ▲                                                                                         │
                └──────── outbox.jsonl ◄── say ◄── reply ◄── drain with `watch` ◄─────────────────────────┘
```

- **The bridge** holds the WebSocket and appends every frame to `<base>/inbox.jsonl`. It is the only process on the socket. It reconnects on its own.
- **The hook poller**, `Chief.Bridge hook --config <path>`, watches that file (file-system events, plus a poll every `hook.poll_s`, default 5 s). A qualifying chat is POSTed as JSON. After a fire it waits `hook.cooldown_s` (default 15 s). Chats that arrive in the gap go out together in the next fire.
- **The webhook routine** is the URL the operator put in the poller's environment. That call is what wakes Grok Bot. The URL and the key are not stored in this repo.
- **The woken turn** drains with `Chief.Bridge watch --config <path>` (no `--wait`) and replies with `say` or one line on `outbox.jsonl`, inside the `hook/reply.lock` flock, then stops. The next qualifying chat wakes it again.

`watch --wait` is not this loop. On 2026-09-27 a background `watch --wait` exited and the wake never reached chief. See `agents/chief.md`.

## The concrete setup

Paths are under the bridge `base` directory: the `base` field in `config.json`, or the config file's directory when `base` is omitted. `config.json`, the inbox, and the offset files stay uncommitted. The channel name, a trip password, and the webhook URL and key stay in the operator's config and environment, not in this file.

| Piece | Where it comes from |
|---|---|
| Hook command | `Chief.Bridge hook --config <path>` (installed tool: `chief-bridge hook --config <path>`) |
| Config | Gitignored `config.json`. The `hook` block's shape is `config.example.json`. Field list: README, "Webhook poller". |
| Inbox | `<base>/inbox.jsonl` |
| Hook offset | `<base>/.hook.offset`, unless `hook.state` overrides it. The status file is that path plus `.status`. |
| Watch offset | `<base>/.inbox_watch.offset` (a different file from the hook offset) |
| Reply lock | `<base>/hook/reply.lock` |
| Webhook URL | The environment variable named by `hook.url_env` (default name `CHIEF_HOOK_URL`). `config.json` stores the name only. |
| Webhook key | The environment variable named by `hook.auth_env` (default name `CHIEF_HOOK_AUTH`). `hook.auth_scheme` defaults to `Bearer`. `auth_env` of `""` sends no `Authorization` header. |
| Own nick | `nick` in `config.json`. The example file uses `chief`. |
| Trip filter | `hook.trips`. `[]` (the example default) means every sender except the bridge's own nick. A non-empty list fires only for those trips. A leading `!` is dropped. |
| One-shot check | `Chief.Bridge hook --config <path> --test` posts one fake chat and prints a status such as `hook test: HTTP 200`. The line does not include the URL or the key. |

The POST is `Content-Type: application/json` with `source` (default `chief-bridge-hook`), `channel` (the channel in that bridge's config), and `chats` (`nick`, `trip`, `text`, `ts`). `omitted` appears only when the batch was capped. The field list is in `docs/protocol.md` ("Local wake-up webhook"). Use the placeholder channel from that doc. Do not paste a real channel name into a commit.

Log lines name the HTTP status or the error kind (`timeout`, `error ConnectionError`). They do not name the host.

## The pattern

1. **The bridge is the listener.** `hook` reads `inbox.jsonl` with the same line rules as `watch`. There is no Hatch script and no `$HATCH_HOOK_RUNTIME`.
2. **What qualifies.** An inbound chat from a nick other than the bridge. Other frames, outbound copies, and malformed lines are skipped. When `hook.trips` is non-empty, a sender whose trip is not listed is skipped too. An empty list is not a trust decision: untripped senders can fire the webhook, and the turn still applies `agents/chief.md`.
3. **Offset discipline.** This is the opposite of Muse's hook, because the poller is still alive after the POST:
   - **Deliver first, save second.** The offset moves past a batch only after a **2xx**. A non-2xx, a timeout, or a network error leaves the chats on disk and retries with backoff. Redirects are not followed, so the key is not sent to another host.
   - Leave Muse's "write the offset before `wake()`" on Muse's hook. Writing the offset first here drops chats that never got a 2xx.
   - A crash between the 2xx and the save can deliver one batch twice. The turn treats the POST as a wake-up and reads the room with `watch`.
   - The first run starts at the end of the inbox, so history does not fire.
   - A truncated or rotated inbox starts over from 0. A half-written line waits for its newline.
   - One poller per offset file. A second `hook` on that offset exits **4**.
4. **Drain after the wake.** `watch` prints a JSON array of new inbound chats. That array is the source of truth. An empty array means another wake already drained them. Run one drain at a time per watch-offset file.
5. **Reply under the lock, then stop.** The flock is `<base>/hook/reply.lock`. Restart the bridge only from a shell that does not hold it. See README, "Reply lock", and `agents/chief.md`.

## Why it stays up

- The bridge process reconnects by itself (backoff, and the receive-idle timer). A quiet socket is not, by itself, a stopped wake path. An external process watchdog should use a longer clock than `receive_idle_s` (360 s when that timer stays 300 s). See `knowledge/receive-idle-watchdog.md`.
- `hook` runs until SIGTERM or Ctrl+C. Chats not yet delivered stay in `inbox.jsonl` for the next start.
- `Chief.Bridge status` reports `running`, `FAILING`, or `NOT RUNNING`, and how many chats `watch` has not read (`undrained`). A poller that is not running still logs chats. It does not wake Grok Bot. Restarting it needs the webhook variables in the environment, so that restart is an operator step.

When `auto_ack` is on, the bridge may send a short `(auto)` line before Grok Bot is awake. That line is a receipt. If the poller is `NOT RUNNING` or `FAILING`, the offline text says the wake-up hook is not working. See README, "Auto-acknowledgement".

## Boundary

The POST starts a Grok Bot turn through the webhook configured for this bridge. It does not call Hatch `wake()`, and it does not start one of Muse's workers.

- Webhook secret handling: `docs/security.md`, "Wake-up webhook secret".
- The loop the agent runs: `agents/chief.md`, "How you get woken".
- Wire protocol versus this local POST: `docs/protocol.md`, "Local wake-up webhook".
