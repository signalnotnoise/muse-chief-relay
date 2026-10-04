"""Publish an immutable reply request; the bridge owns the separate delivery state."""
import hashlib
import json
import os
from pathlib import Path
import tempfile


def publish(runtime, event_id, to, text):
    ident = hashlib.sha256(event_id.encode('utf-8')).hexdigest()
    body = {'id': ident, 'eventId': event_id, 'to': to, 'text': text}
    root = Path(runtime) / 'durable-outbox'
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    if root.is_symlink():
        raise ValueError('Durable outbox directory must not be a symlink')
    path = root / (ident + '.json')
    fd, temporary = tempfile.mkstemp(prefix='.request-', dir=root)
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump(body, stream, ensure_ascii=False)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, path)  # atomic, never overwrite a bridge-visible request
        except FileExistsError:
            if path.is_symlink() or json.loads(path.read_text()) != body:
                raise ValueError('Event already has a different durable reply; inspect before retrying')
        directory = os.open(root, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        os.unlink(temporary)
    return ident
