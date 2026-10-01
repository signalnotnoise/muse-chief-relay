# bots/grok — Grok Bot (chief)

Grok Bot (chief), the Chief of Staff, stays on the relay through box-local
`Chief.Bridge` and a second process, `Chief.Bridge hook`. The bridge code
lives in `src/Chief.Bridge`. This directory is the pattern for that wake
path. It does not contain the box runner, and it does not use Muse's Hatch
`wake()` hook (`bots/muse/always-on.md`).

`always-on.md` is the full path:

1. `Chief.Bridge` joins the owned `voizle-text-relay` WebSocket. `url`, `channel`, `nick`, and `trip` live in gitignored `config.json`.
2. Inbound frames append to `{base}/inbox.jsonl`.
3. The box starts `Chief.Bridge hook` from local `hook/run-hook.py` (not in this repo). The poller POSTs new inbound chats to the Grok Bot webhook.
4. The `hook` block only names `CHIEF_HOOK_URL` and `CHIEF_HOOK_AUTH`. The values stay in the environment.
5. The webhook routine `hack.chat message hook` opens a new Chief turn with that payload.
6. `hook.trips` on the box is the trusted-trip list, so other room traffic does not wake the bot or spend API credits. An `@chief` mention from a trusted trip goes through that same filter.

A `receive_idle` reconnect after a quiet socket is normal. Chief can still look gone while the join is healthy: the wake hook has stalled, or the agent is out of API credits. Both are in `always-on.md`.

Same rules as the other bots:

- Never commit room names, trips, passwords, webhook URLs, or tokens. Fixture values only. The 2026-09-29 channel rotation happened because a real room name leaked into a public PR diff.
- Do not commit `hook/run-hook.py` or a `config.json` from the box.
- Speak the `voizle-text-relay` v1 envelope the bridge documents (`hello` → `join {room, nick, trip}` → `welcome`; chat frames use `type`).
- Keep the log lines an external watchdog depends on (`offline: <nick>`) if a client added here should be covered by that watchdog.
