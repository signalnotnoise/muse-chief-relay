# bots/design — Design's room client

`design_bot.py` is Design's channel client: a small Python bot that joins the
owned relay room over `voizle-text-relay` v1, stays connected with
exponential-backoff reconnect, and speaks only when directly addressed by its
nick. Design mentions that aren't direct address get logged to
`mentions.jsonl` instead of answered.

This process is not ChatBridge. It keeps its own socket, its own
`mentions.jsonl`, and its own `design-outbox.jsonl`. ChatBridge
(`src/ChatBridge`, compatibility launch `Chief.Bridge` / `chief-bridge`) is
the desktop bridge other agents use. `inbox.jsonl`, `outbox.jsonl`, `hook`,
and `say` on that bridge are unchanged. `mentions.enabled` defaults to
false.

If a ChatBridge config also lists Design's nick under `agents` and sets
`mentions.enabled` to true, an explicit `@` tag, a JSON `to`, or
`TASK to <nick>:` files one event in that bridge's
`{base}/agents/<id>/inbox.jsonl`. The adapter object is
`chatbridge.inbox.wake`. `trip` there is untrusted identity evidence: a
present trip does not authorize the sender. This client's address check is
still the configured nick as a whole word (`redesign` is not an address).
Trust for the bridge's auto-ack and hook stays on `mention_trips`,
`task_trips`, and `hook.trips`. The root `config.example.json` shows the
bridge shape with routing left off. Do not put that bridge block into
Design's `config.json`, and do not commit a room name, a real trip, or a
webhook URL.

## Setup

Requires Python 3.11+ and the `websockets` package.

```sh
cd bots/design
python3 -m venv venv
./venv/bin/pip install websockets
cp config.example.json config.json
# edit config.json: set channel, nick, trip
./venv/bin/python design_bot.py
```

`config.json` is gitignored — never commit the real one. Room names and trips
are the only access boundary the relay has; the 2026-09-29 channel rotation
happened because a room name leaked into a public PR diff.

## Outbox

To make the bot say something, append one JSON object per line to
`design-outbox.jsonl` (atomic write, object + trailing newline):

```json
{"type": "chat", "text": "..."}
```

The bot tails the file every 2 seconds. A blank line, a malformed line, and a
line that is not a chat are logged and permanently consumed. The offset stays
put only when a send fails, so that line is retried on the next poll.

## Log contract

`design-bot.log` keeps `offline: <nick>` / `online: <nick>` lines for the
external watchdog, which treats "joined with no later offline" as healthy.
Don't rename those lines without updating the watchdog too.
