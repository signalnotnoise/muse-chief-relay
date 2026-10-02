#!/usr/bin/env python3
"""One-shot @dot inbox hook. Produces local events; never contacts a model or network.

Reads the room inbox.jsonl that ChatBridge appends. A v2 handoff is a chat line
with v2_handoff set; a raw delivery frame is not a wake. The per-agent queue under
agents/dot/ is a separate file. Mentions stay off unless mentions.enabled is true.
An opt-in v2 handoff can also file that agent inbox; protocol_v2 defaults off.
A sender trip on either path is untrusted evidence. approved_recipients is the
send gate. This module does not treat a trip as authorization.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import time

MENTION = re.compile(r'(?<![\w@])@dot(?![\w-]|\.[A-Za-z0-9])', re.IGNORECASE)


def classify(text, participants=()):
    if MENTION.search(text):
        return 'addressed_to_dot'
    if re.search(r'(?<![\w@])@[\w.-]+', text):
        return None
    for nick in participants:
        if nick.casefold() == 'dot':
            continue
        name = re.escape(nick)
        if re.search(rf'^\s*(?:(?:hey|hi|hello|ok|okay)[,\s]+)?{name}\b(?:\s*[:,]|\s+(?:can|could|would|will|do|does|did|is|are|what|why|how|where|when)\b)', text, re.I):
            return None
        if re.search(rf'[,?]\s*{name}[?.!\s]*$', text, re.I):
            return None
    if '?' in text or re.match(r'^\s*(?:what|why|how|where|when|who|which|can|could|would|does|do|is|are|anyone|anybody)\b', text, re.I):
        return 'open_question_candidate'
    return None


def event_key(msg):
    if msg.get('id') is not None:
        stable = {'id': msg['id']}
    else:
        stable = {key: msg.get(key) for key in ('nick', 'trip', 'text', 'ts', 'time')}
    return hashlib.sha256(json.dumps(stable, sort_keys=True).encode()).hexdigest()


def open_db(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path, timeout=10)
    db.execute('CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
    db.execute('CREATE TABLE IF NOT EXISTS seen (id TEXT PRIMARY KEY)')
    db.execute('CREATE TABLE IF NOT EXISTS events (id TEXT PRIMARY KEY, payload TEXT NOT NULL, created REAL NOT NULL, delivered INTEGER NOT NULL DEFAULT 0)')
    db.commit()
    return db


def poll(inbox, database, participants=(), mention_only=False):
    if not inbox.exists():
        return {'status': 'inbox_missing', 'queued': 0}
    with open_db(database) as db:
        db.execute('BEGIN IMMEDIATE')
        prior = db.execute("SELECT value FROM state WHERE key='cursor'").fetchone()
        with inbox.open('rb') as stream:
            stat = inbox.stat()
            def cursor(offset):
                stream.seek(0)
                head = stream.read(min(256, offset))
                return {'offset': offset, 'inode': stat.st_ino, 'device': stat.st_dev, 'head_size': len(head), 'head': hashlib.sha256(head).hexdigest()}
            if prior is None:
                # Remember historical IDs without waking, so a later log rotation or
                # reconnect cannot turn skipped history into fresh mentions.
                stream.seek(0)
                while stream.tell() < stat.st_size:
                    historical = stream.readline(stat.st_size - stream.tell())
                    if not historical:
                        break
                    if not historical.endswith(b'\n'):
                        continue
                    try:
                        row = json.loads(historical)
                        msg = row.get('msg', {}) if isinstance(row, dict) else {}
                        if row.get('dir') != 'in' or not isinstance(msg, dict):
                            continue
                        kind = msg.get('type', msg.get('cmd'))
                        entries = [msg] if kind == 'chat' else msg.get('replay', []) if kind == 'welcome' else []
                        if isinstance(entries, list):
                            for item in entries:
                                if isinstance(item, dict):
                                    db.execute('INSERT OR IGNORE INTO seen VALUES (?)', (event_key(item),))
                    except (ValueError, TypeError, AttributeError):
                        continue
                mark = cursor(stat.st_size)
                db.execute("INSERT INTO state VALUES ('cursor', ?)", (json.dumps(mark),))
                return {'status': 'initialized_at_end', 'queued': 0}
            mark = json.loads(prior[0])
            head = stream.read(mark['head_size'])
            rotated = (mark['inode'], mark['device']) != (stat.st_ino, stat.st_dev)
            changed = hashlib.sha256(head).hexdigest() != mark['head']
            offset = 0 if rotated or changed or stat.st_size < mark['offset'] else mark['offset']
            stream.seek(offset)
            queued = 0
            for _ in range(2000):
                line = stream.readline()
                if not line or not line.endswith(b'\n'):
                    break  # Do not commit an incomplete newly appended record.
                offset = stream.tell()
                try:
                    row = json.loads(line)
                    if not isinstance(row, dict) or row.get('dir') != 'in':
                        continue
                    msg = row.get('msg')
                    if not isinstance(msg, dict):
                        continue
                    kind = msg.get('type', msg.get('cmd'))
                    if kind == 'welcome':
                        replay = msg.get('replay', [])
                        if isinstance(replay, list):
                            for item in replay:
                                if isinstance(item, dict):
                                    db.execute('INSERT OR IGNORE INTO seen VALUES (?)', (event_key(item),))
                        continue
                    if kind != 'chat':
                        continue
                    key = event_key(msg)
                    fresh = db.execute('INSERT OR IGNORE INTO seen VALUES (?)', (key,)).rowcount
                    if not fresh or str(msg.get('nick', '')).casefold() == 'dot':
                        continue
                    if participants and msg.get('nick') not in participants:
                        continue
                    text = msg.get('text')
                    if not isinstance(text, str):
                        continue
                    # v2_handoff is a chat line the bridge fsynced before the wire ack.
                    # A raw delivery frame has no marker and stays ignored.
                    handoff = row.get('v2_handoff')
                    if isinstance(handoff, str) and handoff:
                        reason = 'v2_handoff'
                    else:
                        reason = ('addressed_to_dot' if MENTION.search(text) else None) if mention_only else classify(text, participants)
                    if reason is None:
                        continue
                    payload = {'event_id': key, 'source': 'dot-relay-mention', 'untrusted': True, 'reason': reason,
                               'message': {'nick': msg.get('nick'), 'trip': msg.get('trip'), 'text': text[:4000], 'ts': msg.get('ts', row.get('ts'))}}
                    db.execute('INSERT OR IGNORE INTO events VALUES (?, ?, ?, 0)', (key, json.dumps(payload), time.time()))
                    queued += 1
                except (ValueError, TypeError):
                    continue
            db.execute("UPDATE state SET value=? WHERE key='cursor'", (json.dumps(cursor(offset)),))
            return {'status': 'queued' if queued else 'silent', 'queued': queued}


def next_event(database):
    with open_db(database) as db:
        row = db.execute('SELECT payload FROM events WHERE delivered=0 ORDER BY created, id LIMIT 1').fetchone()
        return json.loads(row[0]) if row else None


def acknowledge(database, event_id):
    with open_db(database) as db:
        return db.execute('UPDATE events SET delivered=1 WHERE id=? AND delivered=0', (event_id,)).rowcount


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    runtime = Path(__file__).resolve().parent / 'runtime'
    parser.add_argument('--config', type=Path, default=runtime.parent / 'config.json')
    parser.add_argument('--inbox', type=Path, default=runtime / 'inbox.jsonl')
    parser.add_argument('--database', type=Path, default=runtime / 'mentions.sqlite')
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--wait', action='store_true', help='Block for the next local event; a task worker may relay it through supported notifications')
    modes.add_argument('--next-event', action='store_true', help='Print the next private event for an authorized wake adapter')
    modes.add_argument('--ack', metavar='EVENT_ID', help='Mark delivered only after the wake provider confirms acceptance')
    args = parser.parse_args()
    def participants():
        try:
            return json.loads(args.config.read_text()).get('approved_recipients', [])
        except (OSError, ValueError):
            return []
    if args.wait:
        print(json.dumps({'status': 'waiting', 'mode': 'local-event'}), flush=True)
        while True:
            poll(args.inbox, args.database, participants())
            event = next_event(args.database)
            if event is not None:
                print(json.dumps(event), flush=True)
                return
            time.sleep(1)
    elif args.next_event:
        print(json.dumps(next_event(args.database)))
    elif args.ack:
        print(json.dumps({'acknowledged': acknowledge(args.database, args.ack)}))
    else:
        print(json.dumps(poll(args.inbox, args.database, participants())))


if __name__ == '__main__':
    main()
