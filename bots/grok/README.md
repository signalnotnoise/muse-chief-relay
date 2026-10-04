# bots/grok — Grok Bot (chief)

Grok Bot (chief), the Chief of Staff, stays on the relay through box-local
ChatBridge and a second process, the hook poller. The program is
`src/ChatBridge`. `src/Chief.Bridge`, `Chief.Bridge.dll`, and `chief-bridge`
are the compatibility launch names; `chat-bridge` is the current tool
command. This directory is the pattern for that wake path. It does not
contain the box runner, and it does not use Muse's Hatch `wake()` hook
(`bots/muse/always-on.md`).

A separate portable launcher for a new local checkout is documented in
[LOCAL_SETUP.md](LOCAL_SETUP.md) (`env.sh` and `launch-bridge.sh` on branch
`grok-vm-install`). That launcher does not start the webhook wake path below,
and it must not be pointed at an already-running chief/Grok bridge checkout
with its own private config and `./hc` helper.

`always-on.md` is the full path:

1. ChatBridge joins the owned `voizle-text-relay` WebSocket. On the box the process is still started as `Chief.Bridge`. `url`, `channel`, `nick`, and `trip` live in gitignored `config.json`.
2. Inbound frames append to `{base}/inbox.jsonl`. `outbox.jsonl`, `watch`, `hook`, and `say` are unchanged.
3. The box starts `Chief.Bridge hook` (same program: `chat-bridge hook`) from local `hook/run-hook.py` (not in this repo). The poller POSTs new inbound chats to the Grok Bot webhook.
4. The `hook` block only names `CHIEF_HOOK_URL` and `CHIEF_HOOK_AUTH`. The values stay in the environment.
5. The webhook routine `hack.chat message hook` opens a new Chief turn with that payload.
6. `hook.trips` on the box is the trusted-trip list, so other room traffic does not wake the bot or spend API credits. An `@chief` mention from a trusted trip goes through that same filter. A trip is trusted only when it is on that list (or on `mention_trips` / `task_trips` for auto-ack).

`mentions.enabled` defaults to false. The root `config.example.json` leaves it false and lists `agents` with fixture nicks only. While it is false, ChatBridge does not create `{base}/agents/`, and this wake path stays the room `inbox.jsonl` plus `hook`. When an operator turns it on, an explicit `@nick`, JSON `to`, or `TASK to <nick>:` also files one event per tagged agent. The adapter object is `chatbridge.inbox.wake`. `trip` on that object is untrusted identity evidence: a present value does not authorize the sender, and a null does not mean a check failed. The hook POST still uses `hook.trips`. See `docs/chatbridge.md`.

A `receive_idle` reconnect after a quiet socket is normal. Chief can still look gone while the join is healthy: the wake hook has stalled, or the agent is out of API credits. Both are in `always-on.md`.

Same rules as the other bots:

- Never commit room names, trips, passwords, webhook URLs, or tokens. Fixture values only. The 2026-09-29 channel rotation happened because a real room name leaked into a public PR diff.
- Do not commit `hook/run-hook.py` or a `config.json` from the box.
- Speak the `voizle-text-relay` v1 envelope the bridge documents (`hello` → `join {room, nick, trip}` → `welcome`; chat frames use `type`).
- Keep the log lines an external watchdog depends on (`offline: <nick>`) if a client added here should be covered by that watchdog.
