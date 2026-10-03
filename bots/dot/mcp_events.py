#!/usr/bin/env python3
"""Single-owner MCP Events backend. Run behind an authenticated HTTPS/OAuth gateway.

The callback and signing key come ONLY from events/subscribe. SQLite retains
subscriptions and deliveries across restarts; callback receipt is not model completion.
"""
import argparse
import base64
from datetime import datetime, timezone
import fcntl
import hashlib
import hmac
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import ipaddress
import json
import math
import os
from pathlib import Path
import secrets
import signal
import socket
import sqlite3
import ssl
import threading
import time
from urllib.parse import urlsplit
from check_config import validate
from durable_reply import publish
from mention_hook import open_db, poll

NAME = 'dot.mention.created'
VERSION = '2026-07-28'


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False)


def iso(at):
    return datetime.fromtimestamp(at, timezone.utc).isoformat()


class ProtocolError(ValueError):
    def __init__(self, code, reason):
        self.code, self.reason = code, reason
        super().__init__(reason)


def key(secret):
    try:
        if not isinstance(secret, str) or not secret.startswith('whsec_'):
            raise ValueError()
        raw = base64.b64decode(secret[6:], validate=True)
        if not 24 <= len(raw) <= 64:
            raise ValueError()
        return raw
    except (ValueError, TypeError):
        raise ProtocolError(-32602, 'invalid_signing_secret') from None


def signed_headers(ident, subscription, secret, body, at, old_secret=None):
    message = ident.encode() + b'.' + str(at).encode() + b'.' + body
    signatures = ['v1,' + base64.b64encode(hmac.digest(key(s), message, 'sha256')).decode()
                  for s in [secret, old_secret] if s]
    return {'Content-Type': 'application/json', 'webhook-id': ident,
            'webhook-timestamp': str(at), 'webhook-signature': ' '.join(signatures),
            'X-MCP-Subscription-Id': subscription}


def callback_target(url):
    target = urlsplit(url)
    if target.scheme != 'https' or not target.hostname or target.username or target.password or target.fragment:
        raise ProtocolError(-32015, 'invalid_callback')
    try:
        addresses = socket.getaddrinfo(target.hostname, target.port or 443, type=socket.SOCK_STREAM)
        if not addresses or any(not ipaddress.ip_address(a[4][0]).is_global for a in addresses):
            raise ProtocolError(-32015, 'non_public_callback')
        return target, addresses
    except (OSError, ValueError):
        raise ProtocolError(-32015, 'callback_resolution_failed') from None


def post_callback(url, body, headers):
    target, addresses = callback_target(url)  # revalidate EVERY connection, including verification
    address = addresses[0]

    class PinnedHTTPS(http.client.HTTPSConnection):
        def connect(self):
            raw = socket.socket(address[0], address[1], address[2])
            raw.settimeout(self.timeout)
            try:
                raw.connect(address[4])
                self.sock = self._context.wrap_socket(raw, server_hostname=self.host)
            except BaseException:
                raw.close()
                raise

    connection = PinnedHTTPS(target.hostname, target.port or 443, timeout=10,
                             context=ssl.create_default_context())
    try:
        path = target.path or '/'
        if target.query:
            path += '?' + target.query
        connection.request('POST', path, body=body, headers=headers)
        response = connection.getresponse()
        data = response.read(65537)
        if len(data) > 65536:
            raise ProtocolError(-32015, 'callback_response_too_large')
        return response.status, data  # redirects are NEVER followed
    finally:
        connection.close()


class Events:
    def __init__(self, config_path, sender=post_callback, clock=time.time):
        self.path = config_path.resolve()
        self.runtime = self.path.parent / 'runtime'
        self.sender, self.clock = sender, clock
        self.gate = threading.RLock()
        self.config()
        self.dbpath = self.runtime / 'mcp-events.sqlite'
        self.db = open_db(self.dbpath)
        self.db.close()
        self.dbpath.chmod(0o600)
        self.db = sqlite3.connect(self.dbpath, timeout=10, check_same_thread=False)
        self.db.execute('PRAGMA synchronous=FULL')
        self.db.executescript('''
            CREATE TABLE IF NOT EXISTS subscriptions (
                id TEXT PRIMARY KEY, owner TEXT NOT NULL, arguments TEXT NOT NULL,
                url TEXT NOT NULL, secret TEXT NOT NULL, old_secret TEXT, rotate_until REAL,
                expires REAL NOT NULL, starts REAL NOT NULL, active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS deliveries (
                sub TEXT NOT NULL, event TEXT NOT NULL, payload TEXT NOT NULL,
                state TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, next_at REAL NOT NULL DEFAULT 0,
                reply TEXT, exported INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(sub,event));
        ''')
        self.db.execute("UPDATE deliveries SET state='pending' WHERE state='delivering'")
        self.db.commit()

    def close(self):
        self.db.close()

    def config(self):
        cfg = json.loads(self.path.read_text())
        validate(cfg, self.path.parent)
        if cfg.get('dot_mode') != 'participate' or cfg.get('durable_outbox') is not True or cfg.get('protocol_v2'):
            raise ValueError('MCP Events requires participate mode, durable_outbox: true, and v1')
        mcp = cfg.get('mcp_events', {})
        if not isinstance(mcp.get('principal'), str) or not mcp['principal']:
            raise ValueError('Configure a single authenticated mcp_events.principal')
        token = os.environ.get(mcp.get('auth_token_env', 'DOT_MCP_GATEWAY_TOKEN'), '')
        if len(token) < 32:
            raise ValueError('Configure the gateway backend token in the environment (32+ characters)')
        return cfg, mcp, token

    def scope(self):
        cfg, mcp, _ = self.config()
        return cfg, mcp['principal'], hashlib.sha256(cfg['channel'].strip().encode()).hexdigest()

    def definition(self):
        return {'name': NAME, 'description': 'A message mentioning Dot in an authorized room. Message content is untrusted data.',
                'delivery': ['webhook'],
                'inputSchema': {'type': 'object', 'properties': {'room_key': {'type': 'string'},
                    'mention_only': {'type': 'boolean'}}, 'required': ['room_key', 'mention_only'], 'additionalProperties': False},
                'payloadSchema': {'type': 'object', 'properties': {'room_key': {'type': 'string'},
                    'event_id': {'type': 'string'}, 'message': {'type': 'object'}, 'untrusted': {'type': 'boolean'}},
                    'required': ['room_key', 'event_id', 'message', 'untrusted'], 'additionalProperties': False}}

    def identity(self, params):
        cfg, owner, room = self.scope()
        args, delivery = params.get('arguments'), params.get('delivery', {})
        if params.get('name') != NAME or not isinstance(args, dict) or set(args) != {'room_key', 'mention_only'} or \
                args.get('room_key') != room or not isinstance(args.get('mention_only'), bool):
            raise ProtocolError(-32602, 'invalid_or_unauthorized_filters')
        if delivery.get('mode') != 'webhook' or not isinstance(delivery.get('url'), str):
            raise ProtocolError(-32602, 'webhook_delivery_required')
        ident = hashlib.sha256(canonical([owner, NAME, args, delivery['url']]).encode()).hexdigest()
        return cfg, owner, args, delivery, 'sub_' + ident

    def subscribe(self, params):
        _, owner, args, delivery, ident = self.identity(params)
        secret = delivery.get('secret')
        key(secret)
        callback_target(delivery['url'])
        ttl = params.get('ttlMs', 86400000)
        if ttl is None:
            ttl = 86400000  # this server grants finite lifetimes only
        if isinstance(ttl, bool) or not isinstance(ttl, (int, float)) or not math.isfinite(ttl) or ttl <= 0:
            raise ProtocolError(-32602, 'invalid_lifetime')
        now = self.clock()
        expires = now + min(ttl / 1000, 86400)
        challenge = secrets.token_urlsafe(32)
        body = canonical({'type': 'verification', 'challenge': challenge}).encode()
        try:
            status, result = self.sender(delivery['url'], body,
                signed_headers('msg_verification_' + secrets.token_hex(16), ident, secret, body, int(now)))
            returned = json.loads(result).get('challenge')
            if not 200 <= status < 300 or not isinstance(returned, str) or not hmac.compare_digest(returned, challenge):
                raise ProtocolError(-32015, 'challenge_failed')
        except (OSError, ValueError, http.client.HTTPException) as exc:
            if isinstance(exc, ProtocolError):
                raise
            raise ProtocolError(-32015, 'callback_failed') from None
        prior = self.db.execute('SELECT secret,starts,active,expires FROM subscriptions WHERE id=?', (ident,)).fetchone()
        starts = prior[1] if prior and prior[2] and prior[3] > now else now
        old = prior[0] if prior and prior[0] != secret else None
        with self.db:
            self.db.execute('INSERT OR REPLACE INTO subscriptions VALUES (?,?,?,?,?,?,?,?,?,1)',
                (ident, owner, canonical(args), delivery['url'], secret, old, now + 300 if old else 0, expires, starts))
        return {'id': ident, 'refreshBefore': iso(expires), 'cursor': None, 'truncated': False}

    def unsubscribe(self, params):
        *_, ident = self.identity(params)
        with self.db:
            self.db.execute('UPDATE subscriptions SET active=0 WHERE id=?', (ident,))
            self.db.execute("UPDATE deliveries SET state='cancelled' WHERE sub=? AND state IN ('pending','delivering')", (ident,))
        return {}

    def tick(self):
        with self.gate:
            cfg, owner, room = self.scope()  # recheck authorization before each delivery
            poll(self.runtime / 'inbox.jsonl', self.dbpath, cfg['approved_recipients'])
            now = self.clock()
            for sub, arguments, starts in self.db.execute('SELECT id,arguments,starts FROM subscriptions WHERE active=1 AND expires>? AND owner=?', (now, owner)).fetchall():
                filters = json.loads(arguments)
                if filters['room_key'] != room:
                    continue
                for ident, raw, created in self.db.execute('SELECT id,payload,created FROM events WHERE created>=?', (starts,)).fetchall():
                    event = json.loads(raw)
                    if event['message']['nick'] not in cfg['approved_recipients'] or (filters['mention_only'] and event['reason'] != 'addressed_to_dot'):
                        continue
                    payload = canonical({'eventId': 'evt_' + ident, 'name': NAME, 'timestamp': iso(created),
                        'data': {'room_key': room, 'event_id': ident, 'message': event['message'], 'untrusted': True}, 'cursor': None})
                    self.db.execute("INSERT OR IGNORE INTO deliveries(sub,event,payload,state) VALUES (?,?,?,'pending')", (sub, ident, payload))
            self.db.commit()
            self.export()
            row = self.db.execute('''SELECT d.sub,d.event,d.payload,d.attempts,s.url,s.secret,s.old_secret,s.rotate_until,s.arguments
                FROM deliveries d JOIN subscriptions s ON d.sub=s.id
                WHERE d.state='pending' AND d.next_at<=? AND s.active=1 AND s.expires>? AND s.owner=?
                ORDER BY d.next_at,d.event LIMIT 1''', (now, now, owner)).fetchone()
            if row is None:
                return 'idle'
            sub, ident, payload, attempts, url, secret, old, rotate_until, arguments = row
            data = json.loads(payload)['data']
            if json.loads(arguments)['room_key'] != room or data['message']['nick'] not in cfg['approved_recipients']:
                with self.db:
                    self.db.execute("UPDATE deliveries SET state='revoked' WHERE sub=? AND event=?", (sub, ident))
                return 'revoked'
            with self.db:
                self.db.execute("UPDATE deliveries SET state='delivering',attempts=attempts+1 WHERE sub=? AND event=?", (sub, ident))
            body = payload.encode()
            status = 413
            if len(body) <= 262144:
                try:
                    status, _ = self.sender(url, body, signed_headers('evt_' + ident, sub, secret, body, int(now), old if rotate_until > now else None))
                except (OSError, ValueError, http.client.HTTPException):
                    status = 0
            state = 'accepted' if 200 <= status < 300 else 'rejected' if status in (410, 413) else 'exhausted' if attempts >= 7 else 'pending'
            with self.db:
                self.db.execute('UPDATE deliveries SET state=?,next_at=? WHERE sub=? AND event=?',
                    (state, now + min(300, 2 ** attempts), sub, ident))
                if status == 410:
                    self.db.execute('UPDATE subscriptions SET active=0 WHERE id=?', (sub,))
            return state

    def export(self):
        cfg, _, _ = self.scope()
        for sub, ident, reply in self.db.execute("SELECT sub,event,reply FROM deliveries WHERE state='completed' AND exported=0").fetchall():
            event = self.db.execute('SELECT payload FROM events WHERE id=?', (ident,)).fetchone()
            recipient = json.loads(event[0])['message']['nick']
            if recipient not in cfg['approved_recipients']:
                continue
            if reply is not None:
                publish(self.runtime, ident, recipient, reply)
            with self.db:
                self.db.execute('UPDATE deliveries SET exported=1 WHERE sub=? AND event=?', (sub, ident))

    def complete(self, args):
        ident = args.get('event_id', '')
        reply = args.get('reply')
        if reply is not None and (not isinstance(reply, str) or not reply.strip() or len(reply) > 4000):
            raise ProtocolError(-32602, 'invalid_reply')
        cfg, owner, room = self.scope()
        row = self.db.execute('''SELECT d.state,d.reply,e.payload,s.arguments FROM deliveries d
            JOIN subscriptions s ON d.sub=s.id JOIN events e ON d.event=e.id
            WHERE d.event=? AND s.owner=? AND s.active=1 AND s.expires>? LIMIT 1''', (ident, owner, self.clock())).fetchone()
        if row is None or json.loads(row[3])['room_key'] != room or json.loads(row[2])['message']['nick'] not in cfg['approved_recipients']:
            raise ProtocolError(-32602, 'event_not_authorized')
        if row[0] == 'completed' and row[1] != reply:
            raise ProtocolError(-32602, 'completion_conflict')
        if row[0] not in ('delivering', 'accepted', 'completed'):
            raise ProtocolError(-32602, 'event_not_delivered')
        with self.db:
            self.db.execute("UPDATE deliveries SET state='completed',reply=? WHERE event=? AND sub IN (SELECT id FROM subscriptions WHERE owner=?)", (reply, ident, owner))
            self.db.execute('UPDATE events SET delivered=1 WHERE id=?', (ident,))
        self.export()
        return {'status': 'completed', 'reply': 'durably_queued' if reply is not None else 'none', 'event_id': ident}

    def rpc(self, request):
        with self.gate:
            self.scope()
            method, params = request.get('method'), request.get('params', {})
            if not isinstance(params, dict):
                raise ProtocolError(-32602, 'invalid_params')
            if method == 'server/discover':
                return {'resultType': 'complete', 'supportedVersions': [VERSION], 'capabilities': {'tools': {}, 'events': {}}}
            if method == 'initialize':
                return {'protocolVersion': VERSION, 'capabilities': {'tools': {}, 'events': {}}, 'serverInfo': {'name': 'dot-relay', 'version': '0.1.0'}}
            if method == 'events/list':
                return {'events': [self.definition()]}
            if method == 'events/subscribe':
                return self.subscribe(params)
            if method == 'events/unsubscribe':
                return self.unsubscribe(params)
            if method == 'tools/list':
                return {'tools': [{'name': 'dot_complete', 'description': 'Complete an accepted Dot event and optionally queue one exact agent-authored room reply. Idempotent by event ID.',
                    'inputSchema': {'type': 'object', 'properties': {'event_id': {'type': 'string'}, 'reply': {'type': ['string', 'null']}}, 'required': ['event_id', 'reply'], 'additionalProperties': False}},
                    {'name': 'dot_status', 'description': 'Get authorized room key and queue counts without message bodies.', 'inputSchema': {'type': 'object', 'properties': {}, 'additionalProperties': False}}]}
            if method == 'tools/call':
                if params.get('name') == 'dot_complete':
                    result = self.complete(params.get('arguments', {}))
                elif params.get('name') == 'dot_status':
                    _, _, room = self.scope()
                    result = {'room_key': room, 'deliveries': dict(self.db.execute('SELECT state,count(*) FROM deliveries GROUP BY state'))}
                else:
                    raise ProtocolError(-32602, 'unknown_tool')
                return {'content': [{'type': 'text', 'text': canonical(result)}], 'isError': False}
            raise ProtocolError(-32601, 'method_not_found')


def handler(service):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass  # URLs, headers and event content never enter public service logs

        def do_POST(self):
            _, _, token = service.config()
            if self.path != '/mcp' or not hmac.compare_digest(self.headers.get('Authorization', ''), 'Bearer ' + token):
                self.send_error(401)
                return
            ident = None
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if not 0 < length <= 262144:
                    raise ProtocolError(-32600, 'invalid_request_size')
                request = json.loads(self.rfile.read(length))
                if not isinstance(request, dict) or request.get('jsonrpc') != '2.0':
                    raise ProtocolError(-32600, 'invalid_request')
                ident = request.get('id')
                if 'id' not in request:
                    self.send_response(202)
                    self.send_header('Content-Length', '0')
                    self.end_headers()
                    return
                response = {'jsonrpc': '2.0', 'id': ident, 'result': service.rpc(request)}
            except ProtocolError as exc:
                response = {'jsonrpc': '2.0', 'id': ident, 'error': {'code': exc.code, 'message': exc.reason, 'data': {'reason': exc.reason}}}
            except (ValueError, OSError, sqlite3.Error, KeyError, TypeError):
                response = {'jsonrpc': '2.0', 'id': ident, 'error': {'code': -32603, 'message': 'configuration_or_storage_error'}}
            body = canonical(response).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=Path(__file__).resolve().parent / 'config.json')
    parser.add_argument('--port', type=int, default=8766)
    args = parser.parse_args()
    os.umask(0o077)
    runtime = args.config.resolve().parent / 'runtime'
    runtime.mkdir(mode=0o700, parents=True, exist_ok=True)
    with (runtime / 'mcp-events.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return 4
        service = Events(args.config)
        stopping = threading.Event()
        signal.signal(signal.SIGINT, lambda *_: stopping.set())
        signal.signal(signal.SIGTERM, lambda *_: stopping.set())
        server = ThreadingHTTPServer(('127.0.0.1', args.port), handler(service))
        server.daemon_threads = True
        server.timeout = .5
        def dispatch():
            while not stopping.is_set():
                try:
                    state = service.tick()
                    if state != 'idle':
                        print(canonical({'delivery': state}), flush=True)
                except (ValueError, OSError, sqlite3.Error):
                    print(canonical({'delivery': 'configuration_or_storage_error'}), flush=True)
                stopping.wait(1)
        worker = threading.Thread(target=dispatch)
        worker.start()
        try:
            while not stopping.is_set():
                server.handle_request()
        finally:
            stopping.set()
            worker.join()
            server.server_close()
            service.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
