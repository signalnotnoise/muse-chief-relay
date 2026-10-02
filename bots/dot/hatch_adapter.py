#!/usr/bin/env python3
"""Hatch decision adapter; never registers a hook or invokes a network callback.

The inbox it reads is the room inbox.jsonl. chatbridge.inbox.wake is the
separate per-agent contract and is off unless mentions.enabled is true.
Payloads here set untrusted true. A trip in the payload is evidence only.
"""
import argparse
import json
from pathlib import Path
import sqlite3
import tempfile
import time
from mention_hook import open_db, poll


def decide(inbox, database):
    outcome = poll(inbox, database, mention_only=True)
    with open_db(database) as db:
        db.execute('CREATE TABLE IF NOT EXISTS wake_attempts (event_id TEXT PRIMARY KEY, attempted REAL NOT NULL)')
        db.commit()
        db.execute('BEGIN IMMEDIATE')
        rows = db.execute('SELECT e.id,e.payload FROM events e LEFT JOIN wake_attempts a ON a.event_id=e.id WHERE e.delivered=0 AND a.event_id IS NULL ORDER BY e.created,e.id LIMIT 20').fetchall()
        if not rows:
            return {'mode': 'silent', 'label': outcome['status'], 'payload': {}}
        # wake() exits immediately, so bookkeeping must precede it. This is
        # an attempted wake, NOT a confirmed delivery; preserve payloads.
        db.executemany('INSERT INTO wake_attempts VALUES (?,?)', [(key,time.time()) for key,_ in rows])
        payload = {'source': 'dot-relay-mention', 'untrusted': True,
                   'event_ids': [key for key,_ in rows],
                   'messages': [json.loads(data)['message'] for _,data in rows]}
        return {'mode': 'wake', 'label': 'new @dot relay mentions', 'payload': payload}


def dry_decide(inbox, database):
    with tempfile.TemporaryDirectory(prefix='dot-hatch-dry-') as temp:
        copied = Path(temp) / 'state.sqlite'
        if database.exists():
            source = sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True)
            target = sqlite3.connect(copied)
            try: source.backup(target)
            finally: source.close(); target.close()
        decision = decide(inbox, copied)
        return {'mode': 'silent', 'label': 'dry run; no wake or persistent state changes',
                'payload': {'dry_run': True, 'would_wake': decision['mode']=='wake', 'count': len(decision['payload'].get('messages',[]))}}


def retry(database, event_id):
    with open_db(database) as db:
        db.execute('CREATE TABLE IF NOT EXISTS wake_attempts (event_id TEXT PRIMARY KEY, attempted REAL NOT NULL)')
        return db.execute('DELETE FROM wake_attempts WHERE event_id=?', (event_id,)).rowcount


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    root=Path(__file__).resolve().parent/'runtime'
    parser.add_argument('--inbox',type=Path,default=root/'inbox.jsonl')
    parser.add_argument('--database',type=Path,default=root/'hatch-mentions.sqlite')
    parser.add_argument('--dry-run',action='store_true')
    parser.add_argument('--retry',metavar='EVENT_ID')
    args=parser.parse_args()
    if args.retry:
        if args.dry_run: parser.error('--retry cannot be combined with --dry-run')
        print(json.dumps({'retry_enabled':retry(args.database,args.retry)}))
    else:
        print(json.dumps(dry_decide(args.inbox,args.database) if args.dry_run else decide(args.inbox,args.database)))


if __name__=='__main__':main()
