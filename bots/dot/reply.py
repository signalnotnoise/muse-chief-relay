#!/usr/bin/env python3
"""Queue one model-authored reply for the existing bridge; no generated replies."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import sqlite3
import time
from check_config import validate
from durable_reply import publish


def queue_reply(config_path, to, text, event_id, send=False):
    config_path = config_path.resolve()
    config = json.loads(config_path.read_text())
    validate(config, config_path.parent)
    if config.get('dot_mode') != 'participate':
        raise ValueError('Sending is disabled; participation must be explicitly authorized and configured')
    if to not in config.get('approved_recipients', []):
        raise ValueError('Recipient is not on the authorized ongoing-reply list')
    if not isinstance(text, str) or not text.strip() or len(text) > 4000:
        raise ValueError('Reply must contain 1–4000 characters')
    if not event_id or len(event_id) > 200:
        raise ValueError('A source event ID is required')
    runtime = config_path.parent/'runtime'
    if not send:
        return {'status': 'preview_only', 'recipient': to, 'characters': len(text), 'event_id': event_id}
    with (runtime/'reply.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if config.get('durable_outbox') is True:
            ident = publish(runtime, event_id, to, text)
            return {'status': 'durably_queued_not_yet_confirmed_sent', 'recipient': to, 'event_id': event_id, 'reply_id': ident}
        db = sqlite3.connect(runtime/'replies.sqlite')
        try:
            db.execute('CREATE TABLE IF NOT EXISTS replies (id TEXT PRIMARY KEY, status TEXT, at REAL)')
            if db.execute('SELECT 1 FROM replies WHERE id=?', (event_id,)).fetchone():
                raise ValueError('This event already has a reply attempt; check the outbound log before retrying')
            # Reserve before append: after a crash, do not blindly send duplicates.
            db.execute('INSERT INTO replies VALUES (?, ?, ?)', (event_id, 'attempted', time.time()));db.commit()
            payload = (json.dumps({'cmd':'chat','text':text}, ensure_ascii=False)+'\n').encode()
            path=runtime/'outbox.jsonl'
            fd=os.open(path,os.O_WRONLY|os.O_APPEND|os.O_NOFOLLOW)
            try:
                written=os.write(fd,payload)
                if written != len(payload):raise OSError('Incomplete outbox write; inspect before retrying')
                os.fsync(fd)
            finally:os.close(fd)
            db.execute('UPDATE replies SET status=? WHERE id=?', ('queued', event_id));db.commit()
            return {'status':'queued_not_yet_confirmed_sent','recipient':to,'event_id':event_id}
        finally:db.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',type=Path,default=Path(__file__).resolve().parent/'config.json')
    parser.add_argument('--to',required=True)
    parser.add_argument('--text-file',required=True,type=Path,help='Local file containing the exact approved model-authored reply')
    parser.add_argument('--event-id',required=True)
    parser.add_argument('--send',action='store_true',help='Actually queue; omitted means preview only')
    args=parser.parse_args()
    try: print(json.dumps(queue_reply(args.config,args.to,args.text_file.read_text(),args.event_id,args.send)))
    except (ValueError,OSError) as exc:parser.exit(2,f'Reply not queued: {exc}\n')


if __name__=='__main__':main()
