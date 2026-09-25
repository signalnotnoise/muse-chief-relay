#!/usr/bin/env python3
"""Persistent hack.chat bridge: join, log inbox, drain outbox, reconnect."""
from __future__ import annotations
import json, threading, time, traceback
from pathlib import Path
import websocket

BASE = Path(__file__).resolve().parent
_cfg_path = Path(__import__("os").environ.get("MUSE_RELAY_CONFIG", BASE / "config.json"))
if not _cfg_path.exists():
    _cfg_path = BASE / "config.example.json"
CFG = json.loads(_cfg_path.read_text())
if CFG.get("base"):
    BASE = Path(CFG["base"]).expanduser().resolve()

URL = CFG["url"]
ORIGIN = CFG.get("origin", "https://hack.chat")
CHANNEL = CFG["channel"]
NICK = CFG["nick"]
# Optional hack.chat password: gives this nick a tripcode. Never logged.
PASS = CFG.get("pass") or ""
INBOX = BASE / "inbox.jsonl"
OUTBOX = BASE / "outbox.jsonl"
STATE = BASE / "state.json"
UNREAD = BASE / "unread.jsonl"
PIDF = BASE / "bridge.pid"

def write_json(path: Path, obj):
    path.write_text(json.dumps(obj))

def append_jsonl(path: Path, obj):
    with path.open("a") as f:
        f.write(json.dumps(obj) + "\n")
        f.flush()

def log_event(dir_: str, msg):
    row = {"ts": time.time(), "dir": dir_, "msg": msg}
    append_jsonl(INBOX, row)
    # surface inbound chats from others as unread
    if dir_ == "in" and isinstance(msg, dict) and msg.get("cmd") == "chat":
        nick = msg.get("nick")
        if nick and nick != NICK:
            append_jsonl(UNREAD, {"ts": row["ts"], "nick": nick, "text": msg.get("text", ""), "channel": msg.get("channel", CHANNEL)})

class Bridge:
    def __init__(self):
        self.ws = None
        self._out_pos = OUTBOX.stat().st_size if OUTBOX.exists() else 0
        self._greeted = False
        self._stop = False

    def set_state(self, **extra):
        data = {"alive": True, "at": time.time(), "channel": CHANNEL, "nick": NICK, **extra}
        write_json(STATE, data)

    def on_message(self, ws, message):
        try:
            data = json.loads(message)
        except Exception:
            data = {"raw": message}
        log_event("in", data)
        if isinstance(data, dict) and data.get("cmd") == "onlineSet" and not self._greeted:
            # don't auto-spam; greeting is optional via outbox
            self._greeted = True

    def on_error(self, ws, error):
        log_event("err", {"error": str(error)})

    def on_close(self, ws, status, msg):
        log_event("close", {"status": status, "msg": msg})
        write_json(STATE, {"alive": False, "at": time.time(), "channel": CHANNEL, "nick": NICK})

    def on_open(self, ws):
        self.set_state(connected=True)
        join = {"cmd": "join", "channel": CHANNEL, "nick": NICK}
        if PASS:
            ws.send(json.dumps({**join, "pass": PASS}))
        else:
            ws.send(json.dumps(join))
        log_event("out", join)  # logged without the password
        t = threading.Thread(target=self.pump_outbox, args=(ws,), daemon=True)
        t.start()

    def pump_outbox(self, ws):
        while not self._stop:
            time.sleep(0.35)
            try:
                if not OUTBOX.exists():
                    continue
                size = OUTBOX.stat().st_size
                if size < self._out_pos:
                    self._out_pos = 0
                if size == self._out_pos:
                    continue
                with OUTBOX.open() as f:
                    f.seek(self._out_pos)
                    chunk = f.read()
                    self._out_pos = f.tell()
                for line in chunk.splitlines():
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                    except Exception:
                        obj = {"cmd": "chat", "text": line}
                    if "cmd" not in obj:
                        obj = {"cmd": "chat", "text": str(obj.get("text", obj))}
                    ws.send(json.dumps(obj))
                    log_event("out", obj)
            except Exception as e:
                log_event("err", {"error": f"outbox: {e}"})

    def run_once(self):
        self.ws = websocket.WebSocketApp(
            URL,
            on_open=self.on_open,
            on_message=self.on_message,
            on_error=self.on_error,
            on_close=self.on_close,
        )
        self.ws.run_forever(origin=ORIGIN, ping_interval=25, ping_timeout=10)

    def run_forever(self):
        backoff = 1
        while not self._stop:
            try:
                self.run_once()
            except Exception:
                log_event("err", {"error": traceback.format_exc()[-500:]})
            write_json(STATE, {"alive": False, "at": time.time(), "channel": CHANNEL, "nick": NICK, "reconnecting": True})
            time.sleep(backoff)
            backoff = min(backoff * 2, 30)

def main():
    PIDF.write_text(str(__import__("os").getpid()))
    Bridge().run_forever()

if __name__ == "__main__":
    main()
