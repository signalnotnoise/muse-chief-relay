"""Offline MCP Events backend tests: malformed subscribe/challenge inputs."""
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import tempfile
import unittest

import mcp_events
from mcp_events import Events, ProtocolError


def make_secret():
    return 'whsec_' + base64.b64encode(secrets.token_bytes(32)).decode()


class McpEventsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='dot-mcp-events-')
        self.addCleanup(self.tmp.cleanup)
        self.bot = Path(self.tmp.name)
        runtime = self.bot / 'runtime'
        runtime.mkdir()
        (runtime / 'unread.jsonl').symlink_to('/dev/null')
        (runtime / 'outbox.jsonl').write_text('')
        self.channel = 'fixture-room'
        config = {
            'url': 'wss://relay.example.test',
            'channel': self.channel,
            'nick': 'dot',
            'auto_ack': {'enabled': False},
            'base': 'runtime',
            'dot_mode': 'participate',
            'durable_outbox': True,
            'approved_recipients': ['Alex'],
            'mcp_events': {'principal': 'local-owner', 'auth_token_env': 'DOT_MCP_GATEWAY_TOKEN'},
        }
        (self.bot / 'config.json').write_text(json.dumps(config))
        self.token = 'x' * 40
        os.environ['DOT_MCP_GATEWAY_TOKEN'] = self.token
        self.addCleanup(os.environ.pop, 'DOT_MCP_GATEWAY_TOKEN', None)
        # Never touch the network in tests: skip DNS/callback-target checks.
        self._callback_target = mcp_events.callback_target
        mcp_events.callback_target = lambda url: (None, None)
        self.addCleanup(setattr, mcp_events, 'callback_target', self._callback_target)
        self.room_key = hashlib.sha256(self.channel.encode()).hexdigest()

    def service(self, sender):
        return Events(self.bot / 'config.json', sender=sender, clock=lambda: 1_700_000_000.0)

    def params(self, delivery):
        return {
            'name': 'dot.mention.created',
            'arguments': {'room_key': self.room_key, 'mention_only': True},
            'delivery': delivery,
        }

    def delivery(self, url='https://callback.example.test/hook'):
        return {'mode': 'webhook', 'url': url, 'secret': make_secret()}

    def test_delivery_none_is_rejected(self):
        svc = self.service(sender=lambda *a: (200, b'{}'))
        with self.assertRaises(ProtocolError) as ctx:
            svc.subscribe(self.params(None))
        self.assertEqual(ctx.exception.code, -32602)

    def test_delivery_list_is_rejected(self):
        svc = self.service(sender=lambda *a: (200, b'{}'))
        with self.assertRaises(ProtocolError) as ctx:
            svc.subscribe(self.params(['https://callback.example.test/hook']))
        self.assertEqual(ctx.exception.code, -32602)

    def test_challenge_non_object_is_challenge_failed(self):
        for body in (b'[]', b'null', b'42', b'"text"'):
            with self.subTest(body=body):
                svc = self.service(sender=lambda *a: (200, body))
                with self.assertRaises(ProtocolError) as ctx:
                    svc.subscribe(self.params(self.delivery()))
                self.assertEqual(ctx.exception.code, -32015)
                self.assertEqual(ctx.exception.reason, 'challenge_failed')

    def test_challenge_wrong_value_is_challenge_failed(self):
        svc = self.service(sender=lambda *a: (200, b'{"challenge": "wrong"}'))
        with self.assertRaises(ProtocolError) as ctx:
            svc.subscribe(self.params(self.delivery()))
        self.assertEqual(ctx.exception.reason, 'challenge_failed')

    def test_subscribe_happy_path(self):
        def sender(url, body, headers):
            challenge = json.loads(body)['challenge']
            return 200, json.dumps({'challenge': challenge}).encode()
        svc = self.service(sender=sender)
        result = svc.subscribe(self.params(self.delivery()))
        self.assertTrue(result['id'].startswith('sub_'))
        self.assertIn('refreshBefore', result)

    def test_mixed_case_config_is_refused(self):
        (self.bot / 'config.json').write_text('{"nick": "dot", "NICK": "other"}')
        with self.assertRaises(ValueError):
            Events(self.bot / 'config.json', sender=lambda *a: (200, b'{}'))


if __name__ == '__main__':
    unittest.main()
