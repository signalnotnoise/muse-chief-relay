# Always-on wake for Grok Bot (chief)

How a relay message wakes **Grok Bot** — chief, the Chief of Staff. Two
box-local processes stay up: `Chief.Bridge` on the socket, and
`Chief.Bridge hook` posting to a webhook. The webhook starts a new Chief
turn. Muse's Hatch hook is a different runtime (`bots/muse/always-on.md`).

Real `url`, channel, nick, trip, webhook URL, and bearer key stay in
gitignored config and in the box environment. This file uses placeholders.

## How a message gets there

1. **The bridge stays joined.** Box-local `Chief.Bridge` (C#, `src/Chief.Bridge`) holds the owned `voizle-text-relay` WebSocket. `config.json` supplies `url`, `channel`, `nick`, and `trip`. Commit none of those values. The shape to copy is `config.example.json` (`wss://…` placeholder, `your-channel-name`, nick `chief`, trip `Ab12Cd`). On this relay a `trip` is a public code, not a password.
2. **Every inbound frame is logged.** The bridge appends JSONL to `{base}/inbox.jsonl` (`base` is the config field, or the config file's directory). Each line is one object. Outbound copies land in the same file; the hook does not forward them.
3. **A second process polls that inbox.** `Chief.Bridge hook` POSTs new inbound chats to the Grok Bot webhook. On the box it is started from the local runner `hook/run-hook.py`. That file is not in this repo. `bots/grok/` is the pattern for what it starts, not a copy of the runner. Do not commit a runner that contains the webhook URL or key.
4. **The `hook` block names the secrets. It does not hold them.** `hook.url_env` and `hook.auth_env` are variable names. The defaults are `CHIEF_HOOK_URL` and `CHIEF_HOOK_AUTH`. `hook.auth_scheme` defaults to `Bearer`, sent as `Authorization: Bearer <key>`. The values live only in the poller's environment (box secrets). `config.example.json` shows the names. See `docs/security.md`, "Wake-up webhook secret".
5. **The webhook opens a Chief turn.** It wakes the Grok Bot routine named `hack.chat message hook`. That routine starts a new turn with the chat payload. The POST is the wake-up. The turn still drains the inbox with `watch` (below). The payload shape is in `docs/protocol.md`, "Local wake-up webhook": `source`, `channel`, `chats` of `{nick, trip, text, ts}`. Use that doc's placeholder channel. Do not paste a real channel name into a commit.
6. **Only trusted trips are forwarded.** On this box `hook.trips` is the trusted-trip list, so other room traffic does not wake Grok Bot or spend API credits. A mention of `@chief` from a sender on that list is forwarded with the rest of that sender's chats. The hook matches the trip, not the mention text. A leading `!` is dropped. Put the real codes only in gitignored `config.json`. The committed example leaves `trips` as `[]`, and an empty list forwards every nick except the bridge — that setting does not save credits. The turn still applies the trust rules in `agents/chief.md`.

```
voizle-text-relay ─► Chief.Bridge ─► {base}/inbox.jsonl ─► Chief.Bridge hook ─POST─► "hack.chat message hook" ─► new Chief turn
                                              ▲                              │
                                              └── hook/run-hook.py (on the box, not in git)
```

Check the path without printing secrets:

```bash
Chief.Bridge hook --config /path/to/config.json --test
# prints e.g. "hook test: HTTP 200", never the URL or the key
```

## Offset discipline

Same idea as Muse's hook (`bots/muse/always-on.md`): a durable offset file, a silent first run, and a rotation reset. The file is `{base}/.hook.offset` unless `hook.state` overrides it. The status file is that path plus `.status`. `watch` uses a different file, `{base}/.inbox_watch.offset`.

- **First run is a silent catch-up.** No offset file yet: record the end of the inbox and deliver nothing, so history does not fire. If the inbox file is not there yet, record 0 so lines that show up later are new.
- **Advance before a fire, or with a successful one.** New lines that contain no chat to POST move the offset immediately. A batch that is POSTed moves the offset only after HTTP 2xx. A non-2xx, a timeout, or a network error leaves the offset put, and the chats stay queued. Redirects are not followed.
- **Log rotation is detected.** A shorter file, or a file whose first bytes changed, resets the read to 0 so the new log is not treated as already consumed. Only complete lines are read.
- One poller per offset file. A second `hook` on that offset exits 4.

Muse writes its offset before `wake()` because that call exits the script. This poller keeps running, so a posted batch is committed with the 2xx, not before the POST.

## When chief looks gone

A quiet socket is normal. After the join is confirmed, `receive_idle_s` (default 300) with no inbound frame cancels that session, sets `state.json` to `reconnecting: true` with a `reason` starting `receive_idle`, and backs off. The process stays up and rejoins. An external watchdog should wait longer than that timer (360 s when the timer stays 300 s). See `knowledge/receive-idle-watchdog.md`.

The nick can also look absent while the WebSocket join is healthy. Those are different failures:

- **The wake hook stalled.** `Chief.Bridge status` shows `hook: NOT RUNNING` or `hook: FAILING`. Chats are still appended to `inbox.jsonl`. Nothing POSTs, so Grok Bot does not start. When `auto_ack` is on, the bridge says the wake-up hook is not working instead of promising a reply. Restarting the poller needs `CHIEF_HOOK_URL` and `CHIEF_HOOK_AUTH` in the environment. That is an operator step. The box starter is `hook/run-hook.py`.
- **The agent is out of API credits.** The join can stay up and the hook can still POST, but the new Chief turn does not run. The room then looks empty on chief's side even though the bridge is connected.

`status` separates them: bridge pid and `reconnecting`, versus hook `running` / `FAILING` / `NOT RUNNING` and `undrained` chats `watch` has not read.

## After the wake

The routine's payload is the wake-up, not the whole transcript. The turn runs `Chief.Bridge watch --config <path>` (no `--wait`), replies with `say` or one `outbox.jsonl` line inside `{base}/hook/reply.lock`, then stops. The next qualifying chat wakes it again. `watch --wait` is not this loop. See `agents/chief.md`.

## Boundary

This POST starts a Grok Bot turn through `hack.chat message hook`. It does not call Hatch `wake()`, and it does not start one of Muse's workers.

- Webhook secret handling: `docs/security.md`, "Wake-up webhook secret".
- Field list for the POST: `docs/protocol.md`, "Local wake-up webhook".
- Poller defaults: README, "Webhook poller".
