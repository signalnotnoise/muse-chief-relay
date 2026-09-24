#!/usr/bin/env python3
"""Parse a hack.chat chat text into a relay envelope."""
from __future__ import annotations
import json, re, sys

def parse(text: str, nick: str):
    text = (text or "").strip()
    if not text:
        return {"type": "chat", "from_nick": nick, "text": text}
    if text.startswith("{") and text.endswith("}"):
        try:
            obj = json.loads(text)
            if isinstance(obj, dict) and obj.get("type"):
                obj["from_nick"] = nick
                return obj
        except Exception:
            pass
    m = re.match(r"(?i)^TASK\s+to\s+(chief|muse)\s*:\s*(.+?)(?:\s+[—\-]\s+|\s*:\s*)(.+)$", text)
    if m:
        return {"type": "task", "to": m.group(1).lower(), "title": m.group(2).strip(), "body": m.group(3).strip(), "from_nick": nick, "id": None}
    m = re.match(r"(?i)^RESULT\s+(\S+)\s*:\s*(.+)$", text)
    if m:
        return {"type": "result", "id": m.group(1), "summary": m.group(2).strip(), "from_nick": nick, "status": "done"}
    m = re.match(r"(?i)^OPINION\s*:\s*(.+)$", text)
    if m:
        return {"type": "opinion", "text": m.group(1).strip(), "from_nick": nick}
    if re.match(r"(?i)^ping$", text):
        return {"type": "ping", "from_nick": nick}
    return {"type": "chat", "from_nick": nick, "text": text}

if __name__ == "__main__":
    print(json.dumps(parse(sys.argv[1] if len(sys.argv)>1 else "", sys.argv[2] if len(sys.argv)>2 else "Fuse")))
