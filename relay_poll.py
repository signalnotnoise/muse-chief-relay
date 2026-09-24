#!/usr/bin/env python3
"""Poll inbox for new non-chief inbound chats since relay_offset."""
import json, sys
from pathlib import Path

BASE = Path("/workspace/hackchat")
INBOX = BASE / "inbox.jsonl"
OFFSET = BASE / "relay_offset"

def main():
    off = int(OFFSET.read_text().strip()) if OFFSET.exists() else 0
    data = INBOX.read_bytes() if INBOX.exists() else b""
    if len(data) < off:
        off = 0
    chunk = data[off:]
    new_off = off + len(chunk)
    events = []
    for line in chunk.splitlines():
        if not line.strip():
            continue
        try:
            o = json.loads(line)
        except Exception:
            continue
        if o.get("dir") != "in":
            continue
        m = o.get("msg") or {}
        if not isinstance(m, dict):
            continue
        cmd = m.get("cmd")
        if cmd == "chat":
            nick = m.get("nick") or ""
            if not nick or nick.lower() == "chief":
                continue
            events.append({
                "kind": "chat",
                "ts": o.get("ts"),
                "nick": nick,
                "text": m.get("text") or "",
                "msgid": m.get("id"),
            })
        elif cmd in ("onlineAdd", "onlineRemove"):
            events.append({"kind": cmd, "nick": m.get("nick"), "ts": o.get("ts")})
    OFFSET.write_text(str(new_off))
    for e in events:
        print(json.dumps(e, ensure_ascii=False))
    return 0

if __name__ == "__main__":
    sys.exit(main())
