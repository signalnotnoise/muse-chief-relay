"""Outbox pump across a reconnect: nothing is lost or sent into a dead socket.
Run: python3 legacy/python/test_pump.py  (needs websocket-client installed)"""
import json, os, sys, time, tempfile, importlib
from pathlib import Path
d = tempfile.mkdtemp()
cfg = {"url":"wss://x","origin":"https://hack.chat","channel":"c","nick":"chief","base":d}
Path(d,"config.json").write_text(json.dumps(cfg))
os.environ["MUSE_RELAY_CONFIG"] = str(Path(d,"config.json"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridge
class FakeWS:
    def __init__(s, name): s.name, s.sent, s.closed = name, [], False
    def send(s, data):
        if s.closed: raise Exception("Connection is already closed.")
        s.sent.append(json.loads(data))
def say(t):
    with open(bridge.OUTBOX, "a") as f: f.write(json.dumps({"cmd":"chat","text":t})+"\n")
b = bridge.Bridge()
w1 = FakeWS("w1"); b.on_open(w1); time.sleep(0.5)
say("one"); time.sleep(0.8)
assert [m.get("text") for m in w1.sent if m.get("cmd")=="chat"] == ["one"], w1.sent
# drop: socket dies before on_close fires (ping timeout), then reconnect
w1.closed = True
say("lost-window"); time.sleep(0.8)   # send fails on w1: must not be dropped
b.on_close(w1, None, None)
w2 = FakeWS("w2"); b.on_open(w2); time.sleep(0.8)
for i in range(5): say(f"m{i}")
time.sleep(1.2)
got2 = [m.get("text") for m in w2.sent if m.get("cmd")=="chat"]
assert got2 == ["lost-window","m0","m1","m2","m3","m4"], got2
assert [m.get("text") for m in w1.sent if m.get("cmd")=="chat"] == ["one"]
print("OK", got2)
b._stop = True
