"""Strict config parsing: duplicate and mixed-case keys must be refused."""
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

import check_config
from check_config import load_config, validate
import go
from mcp_events import Events


STRICT_CONFIG_ERROR = '^config keys must be lowercase and unique$'


class StrictConfigTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='dot-strict-config-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def write(self, name, text):
        path = self.root / name
        path.write_text(text)
        return path

    def test_rejects_exact_duplicate_keys(self):
        path = self.write('dup.json', '{"nick": "dot", "nick": "other"}')
        with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
            load_config(path)

    def test_rejects_mixed_case_alias(self):
        # The bridge binds property names case-insensitively, so NICK would
        # override nick after passing a case-sensitive validator.
        path = self.write('alias.json', '{"nick": "dot", "NICK": "other"}')
        with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
            load_config(path)

    def test_rejects_mixed_case_key_alone(self):
        path = self.write('case.json', '{"Nick": "dot"}')
        with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
            load_config(path)

    def test_rejects_nested_duplicates(self):
        path = self.write('nested.json', '{"mcp_events": {"principal": "a", "PRINCIPAL": "b"}}')
        with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
            load_config(path)

    def test_accepts_real_example_config(self):
        cfg = load_config(Path(__file__).parent / 'config.example.json')
        self.assertEqual(cfg['nick'], 'dot')

    def test_valid_config_still_validates(self):
        (self.root / 'runtime').mkdir()
        for name in ('outbox.jsonl', 'unread.jsonl'):
            (self.root / 'runtime' / name).symlink_to('/dev/null')
        cfg = json.loads((Path(__file__).parent / 'config.example.json').read_text())
        cfg['channel'] = 'fixture-room'
        path = self.write('config.json', json.dumps(cfg))
        validate(load_config(path), self.root)


class StrictConfigIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='dot-strict-integration-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        runtime = self.root / 'runtime'
        runtime.mkdir()
        (runtime / 'unread.jsonl').symlink_to('/dev/null')
        (runtime / 'outbox.jsonl').write_text('')
        self.token = 'fixture-token-' + 'x' * 32
        (runtime / 'gateway-token').write_text(self.token)
        self.config = {
            'url': 'wss://relay.example.test',
            'channel': 'fixture-room',
            'nick': 'dot',
            'auto_ack': {'enabled': False},
            'base': 'runtime',
            'dot_mode': 'participate',
            'durable_outbox': True,
            'approved_recipients': ['fixture-participant'],
            'mcp_events': {
                'principal': 'fixture-owner',
                'auth_token_env': 'DOT_MCP_GATEWAY_TOKEN',
            },
        }
        self.valid_text = json.dumps(self.config)
        self.path = self.root / 'config.json'
        self.path.write_text(self.valid_text)
        # Restore any preexisting token instead of deleting it after the test.
        token_env = mock.patch.dict(os.environ, {'DOT_MCP_GATEWAY_TOKEN': self.token})
        token_env.start()
        self.addCleanup(token_env.stop)
        self.assertEqual(load_config(self.path), self.config)
        validate(load_config(self.path), self.root)

    def ambiguous_configs(self):
        # Each mutation changes only a key. A permissive parser would otherwise
        # leave the fixture valid, so another validation error cannot pass a test.
        for scope, key, value in (
                ('top-level', 'nick', 'dot'),
                ('nested', 'principal', 'fixture-owner')):
            original = json.dumps(key) + ': ' + json.dumps(value)
            self.assertEqual(self.valid_text.count(original), 1)
            for kind, replacement in (
                    ('duplicate', original + ', ' + original),
                    ('alias', original + ', ' + json.dumps(key.upper()) + ': ' + json.dumps(value))):
                yield scope + ' ' + kind, self.valid_text.replace(original, replacement, 1)

    def test_main_rejects_ambiguous_keys_for_strict_loader_reason(self):
        with mock.patch.object(sys, 'argv', ['check_config.py', str(self.path)]):
            with mock.patch.object(sys, 'stdout', io.StringIO()):
                self.assertEqual(check_config.main(), 0)
            for name, text in self.ambiguous_configs():
                with self.subTest(case=name):
                    self.path.write_text(text)
                    with mock.patch.object(sys, 'stderr', io.StringIO()) as stderr:
                        self.assertEqual(check_config.main(), 2)
                    self.assertEqual(stderr.getvalue(),
                                     'Refusing start: config keys must be lowercase and unique\n')

    def test_go_setup_rejects_ambiguous_keys(self):
        # Prove the complete existing-config setup path accepts this fixture.
        self.assertEqual(go.setup(self.path), (self.config, self.token))
        for name, text in self.ambiguous_configs():
            with self.subTest(case=name):
                self.path.write_text(text)
                with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
                    go.setup(self.path)
                self.assertEqual(self.path.read_text(), text)

    def test_mcp_events_rejects_ambiguous_keys_on_start_and_reload(self):
        sender = mock.Mock(side_effect=AssertionError('Tests must not contact a callback'))
        service = Events(self.path, sender=sender)
        self.addCleanup(service.close)
        self.assertEqual(service.config(),
                         (self.config, self.config['mcp_events'], self.token))
        for name, text in self.ambiguous_configs():
            with self.subTest(case=name):
                self.path.write_text(text)
                with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
                    service.config()
                with self.assertRaisesRegex(ValueError, STRICT_CONFIG_ERROR):
                    invalid_service = Events(self.path, sender=sender)
                    # If startup unexpectedly succeeds, still close its database.
                    invalid_service.close()
        sender.assert_not_called()


if __name__ == '__main__':
    unittest.main()
