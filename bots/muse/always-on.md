# Always-on triggers on the Hatch runtime (Muse's setup)

How an inbound relay chat becomes a new turn for Muse on the Hatch runtime.
`relay-inbox-watch.sh` is that hook. It wakes when any nick other than the
bridge's own nick posts a chat. It does not match a mention string.

This file is Muse's setup only. Grok Bot (chief) is woken by ChatBridge's
hook poller (`chief-bridge hook`, compatibility command `Chief.Bridge hook`)
and a webhook. That path is `bots/grok/always-on.md`. The desktop process is
ChatBridge. This hook still tails the room `inbox.jsonl`.

## The primitive: hooks and `wake()`

- Hook scripts are run by the Hatch runtime on a tight poll loop (this one
  runs every few seconds).
- A hook script sources `$HATCH_HOOK_RUNTIME`, which provides two shell
  functions:
  - `wake("<label>", '<json-payload>')` — spawns a new Muse worker turn with the payload attached.
  - `silent("<reason>", '{}')` — does nothing this cycle.
- **Both terminate the script process immediately.** Bookkeeping must happen *before* the call. Nothing after them ever executes.

## The concrete setup

The directory is `bots/muse/`. The script's environment variable is still
`FUSE_RELAY_DIR`. Home-relative defaults below are the script's own defaults,
not a room name.

| Piece | Value |
|---|---|
| Hook script (live) | `~/hooks/scripts/relay-inbox-watch.sh` |
| Reference copy | `bots/muse/relay-inbox-watch.sh` in this repo |
| Inbox tailed | `$FUSE_RELAY_DIR/inbox.jsonl` (default `~/workspace/fuse-relay/inbox.jsonl`). Appended by ChatBridge (compatibility launch `Chief.Bridge`), one JSON object per line: `{"dir":"in","msg":{...},"ts":...}` |
| Offset file | `$HOOK_STATE_DIR/relay-inbox-watch.offset` (default `~/hooks/state/relay-inbox-watch.offset`), a line count |
| Env overrides | `FUSE_RELAY_DIR`, `HOOK_STATE_DIR` |
| Own nick | `nick` in `$FUSE_RELAY_DIR/config.json`. If that file is missing or has no nick, the script falls back to `Fuse`. |
| Wake label | `wake("new relay channel messages", {"messages": [...]})` |
| Dry run | `HATCH_HOOK_DRY_RUN=1` — the pass runs and does not write the offset |

## The pattern

1. **No separate listener.** The bridge already logs every inbound frame. This hook tails `inbox.jsonl`.
2. **Each run:** read everything after the offset, filter it, advance the offset, then `wake` or `silent`.
3. **Offset discipline** (each of these was learned from a real outage):
   - Write the offset **before** calling `wake`. Writing it after caused a wake storm — about 330 duplicate workers in 30 minutes — because the offset never advanced.
   - Single-pass read: `tail -n "+$((LAST + 1))"` into a temp file, then count what was actually read. The old `wc -l` followed by `tail -n` raced with appends and skipped messages (2026-09-27: 4 messages swallowed).
   - If total lines are below the saved offset, the log rotated: reset the offset to 0.
   - First run: set the offset to the current line count and call `silent`, so old history does not flood wakes.
4. **Filter with python3** (bash can't parse JSON):
   - `row["dir"] == "in"`
   - `(msg["cmd"] or msg["type"]) == "chat"` (hack.chat uses `cmd`, voizle-text-relay uses `type`)
   - `msg["nick"] !=` the bridge nick
   - Skip malformed lines (fail-closed).

## Why it's always-on

- The offset file survives a restart of the hook process. It is written before `wake()`, so a crash after that write and before the turn starts can skip a batch. A crash before the write replays those lines on the next poll.
- The Hatch runtime schedules the script. This repo ships the reference copy; it does not start that loop.

## Boundary

`wake()` starts the turn in Muse's Hatch runtime, as one of Muse's workers.
It does not wake Grok Bot. Grok Bot's path is `bots/grok/always-on.md`.

ChatBridge mention inboxes are a different file. They appear only when
`mentions.enabled` is true, and this script does not open
`{base}/agents/`. A sender trip on `chatbridge.inbox.wake` is untrusted
evidence. This hook's filter is the bridge nick.
