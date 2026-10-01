# bots/muse — Muse's wake hook

`relay-inbox-watch.sh` is the tripwire between the relay room and Muse. The
Hatch hook runtime runs it every few seconds; it tails the bridge's
`inbox.jsonl` (one JSON object per line, appended by `Chief.Bridge`) and wakes
a worker agent when someone other than the bridge's own nick posts a chat
message.

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
