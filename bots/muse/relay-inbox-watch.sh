#!/usr/bin/env bash
# Watches the ChatBridge room inbox (inbox.jsonl) for new inbound chats and
# wakes a worker when someone other than our own bridge nick says something.
# Chief.Bridge is the compatibility name of the same program. This script
# does not read {base}/agents/. Mention inboxes exist only when
# mentions.enabled is true, and a trip on chatbridge.inbox.wake is
# untrusted evidence.
#
# This runs inside the Hatch hook runtime: `wake` and `silent` are shell
# functions it provides, and both TERMINATE this process — no code after
# them ever executes. Bookkeeping (the offset file) must happen BEFORE
# the wake call.
#
# The inbox itself is JSON, and bash can't parse JSON sanely, so the actual
# message filtering is done with embedded python3 one-liners. python3 is a
# given wherever this hook runs.
set -euo pipefail
source "$HATCH_HOOK_RUNTIME"

# Overridable locations. Defaults match a stock Fuse layout.
RELAY_DIR="${FUSE_RELAY_DIR:-$HOME/workspace/fuse-relay}"
HOOK_STATE_DIR="${HOOK_STATE_DIR:-$HOME/hooks/state}"
INBOX="$RELAY_DIR/inbox.jsonl"
CONFIG="$RELAY_DIR/config.json"
OFFSET_FILE="$HOOK_STATE_DIR/relay-inbox-watch.offset"

DRY=0
if [ "${HATCH_HOOK_DRY_RUN:-0}" = "1" ]; then
  DRY=1
fi

if [ ! -f "$INBOX" ]; then
  silent "inbox file missing (bridge not running)" '{}'
  exit 0
fi

NICK="$(CONFIG="$CONFIG" python3 -c 'import json, os; print(json.load(open(os.environ["CONFIG"])).get("nick", "Fuse"))' 2>/dev/null || echo Fuse)"

TOTAL="$(wc -l < "$INBOX" | tr -d ' ')"
LAST=0
if [ -f "$OFFSET_FILE" ]; then
  LAST="$(cat "$OFFSET_FILE" | tr -d ' \n')"
fi
if ! [[ "$LAST" =~ ^[0-9]+$ ]]; then LAST=0; fi
if [ "$TOTAL" -lt "$LAST" ]; then LAST=0; fi

# First run: bootstrap the offset silently so history doesn't flood a wake.
if [ ! -f "$OFFSET_FILE" ]; then
  if [ "$DRY" -eq 0 ]; then
    echo "$TOTAL" > "$OFFSET_FILE"
  fi
  silent "initialized offset at $TOTAL lines" '{}'
  exit 0
fi

if [ "$TOTAL" -le "$LAST" ]; then
  silent "no new inbox lines" '{}'
  exit 0
fi

# Single-pass read: everything after LAST, counted as actually read.
# (The old wc -l ... then tail -n raced with appends: the tail window could
# shift forward between the two calls and the offset would jump over lines
# that were never delivered to a wake. 2026-09-27: this swallowed 4 messages.)
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT
tail -n "+$((LAST + 1))" "$INBOX" > "$TMP"
READ="$(wc -l < "$TMP" | tr -d ' ')"
NEW_LAST="$((LAST + READ))"

NEW_MSGS="$(NICK="$NICK" python3 -c "
import json, os, sys
nick = os.environ.get('NICK', 'Fuse')
out = []
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        row = json.loads(line)
    except Exception:
        continue
    if row.get('dir') != 'in':
        continue
    msg = row.get('msg') or {}
    # hack.chat uses cmd=chat; voizle-text-relay uses type=chat (2026-09-30 cutover)
    if (msg.get('cmd') or msg.get('type')) != 'chat':
        continue
    if msg.get('nick') == nick:
        continue
    out.append({'nick': msg.get('nick'), 'trip': msg.get('trip'),
                'text': msg.get('text'), 'ts': row.get('ts')})
print(json.dumps(out))
" < "$TMP")"

if [ -z "$NEW_MSGS" ] || [ "$NEW_MSGS" = "[]" ]; then
  if [ "$DRY" -eq 0 ]; then
    echo "$NEW_LAST" > "$OFFSET_FILE"
  fi
  silent "new lines but no inbound chats from others" '{}'
  exit 0
fi

# Record the offset BEFORE waking: the runtime's `wake` emits the decision
# and then exits the process, so anything after it never runs. (2026-09-27:
# "wake first, offset after" caused a wake storm — the offset never advanced
# and every poll redelivered the same messages.) A crash between this write
# and the wake would skip messages, but that window is far smaller than the
# read race this ordering replaced.
if [ "$DRY" -eq 0 ]; then
  echo "$NEW_LAST" > "$OFFSET_FILE"
fi
wake "new relay channel messages" "{\"messages\":$NEW_MSGS}"
