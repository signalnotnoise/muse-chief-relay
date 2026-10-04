#!/usr/bin/env python3
"""Design — a design-engineering subagent, live in a voizle-text-relay room.

Joins as its configured nick with a public trip, stays connected with
exponential-backoff reconnect, logs everything, and speaks only when directly
addressed by its nick — short acks, rotated, never the same text twice in a
row. When in doubt it stays silent.

Protocol: voizle-text-relay v1. The server sends hello on connect; the client
answers with join {room, nick, trip} and waits for welcome. Trips are
self-asserted public ids (no password hashing here).

Outbound messages go through the client's own connection: append one JSON
object per line to design-outbox.jsonl (single atomic write, object +
trailing newline):
    {"type":"chat","text":"..."}
Lines with {"type":"chat"} (or legacy {"cmd":"chat"}) are honored. A blank
line, a malformed line, and a line that is not a chat are logged and
permanently consumed. The offset stays put only when a send fails, so that
line is retried on the next poll.

Configuration lives in config.json next to this file (see config.example.json).
Real configs — room names, trips — are never committed.
"""
import asyncio
import json
import logging
import os
import re
import time

import websockets

BASE = os.path.dirname(os.path.abspath(__file__))
CFG_PATH = os.path.join(BASE, "config.json")
ID_PATH = os.path.join(BASE, "identity.json")
LOG_PATH = os.path.join(BASE, "design-bot.log")
MENTIONS_PATH = os.path.join(BASE, "mentions.jsonl")
OUTBOX_PATH = os.path.join(BASE, "design-outbox.jsonl")
OUTBOX_OFF = os.path.join(BASE, "design-outbox.offset")

URL = os.environ.get("RELAY_URL", "wss://relay.example.com/relay")
SUBPROTOCOL = "voizle-text-relay"
# Public trip, self-asserted on the owned relay (no password hashing).
# The real trip lives in config.json; this is only a placeholder default.
DEFAULT_TRIP = "!YOUR_TRIP"
INTRO = ("Hey — I'm Design, Fuse's design-engineering subagent "
         "(Vue 3 / Tailwind / Headless UI — layouts, animations, visual craft). "
         "I'll be hanging around for front-end and design work; just say my name.")

DESIGN_KW = re.compile(
    r"vue|tailwind|headless|css|animation|animate|layout|figma|responsive|"
    r"component|motion|framer|shadcn|daisy|vite|front-?end|\bui\b|\bux\b", re.I)

def address_patterns(nick: str) -> tuple[re.Pattern[str], re.Pattern[str]]:
    """Direct address for the configured nick, and that nick's own intro voice.

    The nick must lead the message (optionally after a greeting or @) or close
    it ("..., Design?"). Both sides are word-bounded, so "redesign" is not an
    address. Mid-sentence mentions ("ask Design to …") stay silent.
    """
    n = re.escape(nick)
    direct = re.compile(
        rf"^\s*(?:hey|hi|hello|yo|ok(?:ay)?|thanks?|thank you)?[\s,.]*@?{n}\b"
        rf"|(?<![\w])@?{n}\s*[,?!.:;…]*\s*$",
        re.IGNORECASE,
    )
    self_voice = re.compile(rf"^\s*{n} here\b", re.IGNORECASE)
    return direct, self_voice

# Short and natural. The Fuse pool never mentions Fuse in third person;
# the other pool may (it's accurate: deeper passes go through Fuse).
ACKS_FUSE = ["Got it.", "On it.", "Copy that."]
ACKS_OTHER = [
    "Got it — I'll loop in Fuse if it needs a deeper pass.",
    "Noted.",
    "On it.",
]
ACK_COOLDOWN = 60.0
OUTBOX_POLL = 2.0

logging.basicConfig(filename=LOG_PATH, level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("design")


def note(msg):
    log.info(msg)
    print(msg, flush=True)


def load_config():
    with open(CFG_PATH) as f:
        return json.load(f)


def save_identity(nick, trip):
    with open(ID_PATH, "w") as f:
        json.dump({"nick": nick, "trip": trip}, f)


def append_mention(sender, text):
    with open(MENTIONS_PATH, "a") as f:
        f.write(json.dumps({"ts": int(time.time()), "from": sender,
                            "text": text[:500]}) + "\n")


class JoinFailed(Exception):
    pass


async def join_relay(ws, channel, nick, trip):
    """Wait for hello, send join, return (nick, trip) on welcome.

    On the owned relay a taken nick is *replaced*, not rejected, so there is
    no nick fallback dance: joining as the configured nick always lands.
    Raises JoinFailed on error frames or timeout.
    """
    async def next_frame(timeout):
        try:
            async with asyncio.timeout(timeout):
                async for raw in ws:
                    try:
                        return json.loads(raw)
                    except (json.JSONDecodeError, TypeError):
                        continue
        except TimeoutError:
            raise JoinFailed("timed out waiting for server")
        raise JoinFailed("socket closed before join completed")

    hello = await next_frame(15)
    if (hello.get("type") or hello.get("cmd")) != "hello":
        raise JoinFailed(f"expected hello, got {str(hello)[:120]}")

    await ws.send(json.dumps({"v": 1, "type": "join", "room": channel,
                              "nick": nick, "trip": trip}))
    while True:
        m = await next_frame(15)
        kind = m.get("type") or m.get("cmd")
        if kind == "welcome":
            return m.get("nick") or nick, m.get("trip") or trip
        if kind == "error":
            raise JoinFailed(f"{m.get('code')}: {m.get('text')}")
        # presence / replay-adjacent frames before welcome: ignore


def load_outbox_offset():
    try:
        with open(OUTBOX_OFF) as f:
            return int(f.read().strip())
    except (FileNotFoundError, ValueError):
        # No offset yet: start at EOF, never replay lines written before
        # the watcher first ran.
        try:
            return os.path.getsize(OUTBOX_PATH)
        except FileNotFoundError:
            return 0


def save_outbox_offset(offset):
    with open(OUTBOX_OFF, "w") as f:
        f.write(str(offset))


class Bot:
    def __init__(self, channel, trip, nick="Design"):
        self.channel = channel
        self.trip = trip
        self.nick = nick
        self.ws = None
        self.last_ack = 0.0
        self.last_reply = None
        self.ack_idx = 0
        self.intro_done = False
        self.direct_address, self.self_voice = address_patterns(self.nick)

    async def run(self):
        backoff = 2
        while True:
            try:
                await self.serve()
            except Exception as e:  # noqa: BLE001 - stay alive no matter what
                note(f"drop: {type(e).__name__}: {e} — reconnect in {backoff}s")
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 60)

    async def serve(self):
        async with websockets.connect(URL, subprotocols=[SUBPROTOCOL],
                                      ping_interval=20,
                                      ping_timeout=20) as ws:
            note("websocket connected")
            if ws.subprotocol != SUBPROTOCOL:
                raise RuntimeError(f"server did not select {SUBPROTOCOL}")
            nick, trip = await join_relay(ws, self.channel, self.nick,
                                          self.trip)
            self.nick = nick
            self.direct_address, self.self_voice = address_patterns(self.nick)
            self.ws = ws
            save_identity(nick, trip)
            note(f"joined #{self.channel} as {nick} trip={trip}")
            if not self.intro_done:
                await ws.send(json.dumps({"v": 1, "type": "chat", "text": INTRO}))
                note("intro posted")
                self.intro_done = True
            watcher = asyncio.create_task(self.watch_outbox())
            try:
                async for raw in ws:
                    try:
                        m = json.loads(raw)
                    except (json.JSONDecodeError, TypeError):
                        continue
                    await self.on_frame(ws, m)
            finally:
                watcher.cancel()
                self.ws = None

    async def watch_outbox(self):
        """Tail design-outbox.jsonl and post each {"type":"chat"} line as Design."""
        offset = load_outbox_offset()
        while True:
            await asyncio.sleep(OUTBOX_POLL)
            ws = self.ws
            if ws is None:
                continue
            try:
                with open(OUTBOX_PATH, "rb") as f:
                    f.seek(offset)
                    chunk = f.read()
            except FileNotFoundError:
                continue
            parts = chunk.split(b"\n")
            # parts[-1] is "" after a trailing newline, or a partial line the
            # writer may still be extending: either way, hold it back.
            complete = parts[:-1]
            pos = offset
            for raw in complete:
                adv = len(raw) + 1
                if not raw.strip():
                    pos += adv
                    continue
                try:
                    obj = json.loads(raw.decode("utf-8"))
                except (json.JSONDecodeError, UnicodeDecodeError):
                    note("design-outbox: malformed line skipped")
                    pos += adv
                    continue
                if (not isinstance(obj, dict)
                        or (obj.get("type") or obj.get("cmd")) != "chat"
                        or not str(obj.get("text") or "").strip()):
                    note("design-outbox: rejected non-chat line")
                    pos += adv
                    continue
                text = str(obj["text"])[:2000]
                try:
                    await ws.send(json.dumps({"v": 1, "type": "chat", "text": text}))
                except Exception as e:  # noqa: BLE001 - keep offset, retry next poll
                    note(f"design-outbox: send failed ({type(e).__name__}), will retry")
                    break
                pos += adv
                note(f"design-outbox -> sent ({len(text)} chars)")
            if pos != offset:
                offset = pos
                save_outbox_offset(offset)

    async def on_frame(self, ws, m):
        kind = m.get("type") or m.get("cmd")
        if kind == "chat":
            sender = m.get("nick") or ""
            text = m.get("text") or ""
            note(f"<{sender}> {text[:200]}")
            if sender == self.nick:
                return
            if self.direct_address.search(text) and not self.self_voice.match(text):
                await self.maybe_reply(ws, sender, text)
            elif DESIGN_KW.search(text):
                append_mention(sender, text)
                note(f"design mention logged from {sender}")
        elif kind == "presence":
            event = m.get("event")
            if event == "join":
                note(f"online: {m.get('nick')} trip={m.get('trip')}")
            elif event == "leave":
                # kept as "offline: <nick>" for the watchdog's log contract
                note(f"offline: {m.get('nick')}")
            elif event == "nick":
                note(f"renamed: {m.get('nick')} trip={m.get('trip')}")
        elif kind == "error":
            note(f"error: {m.get('code')}: {m.get('text')}")
        elif kind == "bye":
            note(f"bye: {m.get('reason')} — will rejoin")
            raise RuntimeError(f"server bye: {m.get('reason')}")

    async def maybe_reply(self, ws, sender, text):
        now = time.time()
        if now - self.last_ack < ACK_COOLDOWN:
            return
        pool = ACKS_FUSE if sender == "Fuse" else ACKS_OTHER
        candidates = [a for a in pool if a != self.last_reply] or list(pool)
        reply = candidates[self.ack_idx % len(candidates)]
        self.ack_idx += 1
        self.last_ack = now
        self.last_reply = reply
        await ws.send(json.dumps({"v": 1, "type": "chat", "text": reply}))
        note(f"reply -> {sender}: {reply}")


def main():
    open(OUTBOX_PATH, "a").close()  # touch
    cfg = load_config()
    channel = cfg.get("channel") or "your-room-name"
    nick = cfg.get("nick") or "Design"
    trip = cfg.get("trip") or DEFAULT_TRIP
    note("design bot starting (channel=%s)" % channel)
    asyncio.run(Bot(channel, trip, nick).run())


if __name__ == "__main__":
    main()
