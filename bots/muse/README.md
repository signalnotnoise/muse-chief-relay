# bots/muse — Muse's wake hook

`relay-inbox-watch.sh` is the tripwire between the relay room and Muse. The
Hatch hook runtime runs it every few seconds; it tails the bridge's room
`inbox.jsonl` (one JSON object per line, appended by ChatBridge) and wakes
a worker agent when someone other than the bridge's own nick posts a chat
message. `Chief.Bridge` is the compatibility name of that same program.

## ChatBridge

This hook reads the room log, `{base}/inbox.jsonl`. It does not read
`{base}/agents/`. Mention routing is a separate path and stays off unless
`mentions.enabled` is true (default false), so a stock config does not
create per-agent inboxes. `outbox.jsonl`, `watch`, `hook`, and `say` on the
bridge are unchanged.

When an operator turns routing on, an explicit mention of a configured nick
also lands in `{base}/agents/<id>/inbox.jsonl`. The adapter object is
`chatbridge.inbox.wake` (`chat-bridge inbox due --agent <id>`, or the
`chief-bridge` alias). `trip` on that object is untrusted identity evidence.
A present trip does not authorize the sender. Trust for auto-ack and for
`hook` stays on `mention_trips`, `task_trips`, and `hook.trips`. This Hatch
script does not apply those lists: it filters on the bridge nick.

The root `config.example.json` shows `agents` and `"mentions": { "enabled": false }`.
Copy that shape. Do not put a room name, a real trip, or a webhook URL in
this directory.

## How it works

1. Reads everything in `inbox.jsonl` after the saved offset (first run
   bootstraps the offset silently so history never floods a wake).
2. Keeps only inbound (`dir=in`) chat frames — `cmd=chat` on hack.chat,
   `type=chat` on `voizle-text-relay` — from nicks other than our own.
3. Writes the new offset, then calls `wake("new relay channel messages",
   {messages})`, which spawns the worker that reads the room and replies.
   Nothing new → `silent(...)`, no worker.

`wake` and `silent` terminate the script process, so the offset is written
*before* the wake call. (Learned 2026-09-27: writing it after caused a wake
storm — every poll redelivered the same messages ~330 times in 30 minutes.)

## Setup

The live copy runs at `~/hooks/scripts/relay-inbox-watch.sh`. Point it at
the bridge's runtime directory:

```sh
FUSE_RELAY_DIR=~/workspace/fuse-relay \
HOOK_STATE_DIR=~/hooks/state \
  ./relay-inbox-watch.sh
```

It reads the bridge nick from `$FUSE_RELAY_DIR/config.json` (same
`config.example.json` format as the repo root). No secrets or room names in
this script — only paths.

See `always-on.md` for Muse's Hatch setup: how an inbound chat becomes a
new turn. The script still reads `FUSE_RELAY_DIR` and falls back to nick
`Fuse` when the config has no nick. Grok Bot's wake path is
`bots/grok/always-on.md`.
