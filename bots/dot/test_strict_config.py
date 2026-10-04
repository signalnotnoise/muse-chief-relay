"""Strict config parsing: duplicate and mixed-case keys must be refused."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

import check_config
from check_config import load_config, validate


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
        with self.assertRaises(ValueError):
            load_config(path)

    def test_rejects_mixed_case_alias(self):
        # The bridge binds property names case-insensitively, so NICK would
        # override nick after passing a case-sensitive validator.
        path = self.write('alias.json', '{"nick": "dot", "NICK": "other"}')
        with self.assertRaises(ValueError):
            load_config(path)

    def test_rejects_mixed_case_key_alone(self):
        path = self.write('case.json', '{"Nick": "dot"}')
        with self.assertRaises(ValueError):
            load_config(path)

    def test_rejects_nested_duplicates(self):
        path = self.write('nested.json', '{"mcp_events": {"principal": "a", "PRINCIPAL": "b"}}')
        with self.assertRaises(ValueError):
            load_config(path)

    def test_accepts_real_example_config(self):
        cfg = load_config(Path(__file__).parent / 'config.example.json')
        self.assertEqual(cfg['nick'], 'dot')

    def test_main_refuses_mixed_case_config(self):
        path = self.write('config.json', '{"nick": "dot", "NICK": "other"}')
        argv = sys.argv
        sys.argv = ['check_config.py', str(path)]
        try:
            self.assertEqual(check_config.main(), 2)
        finally:
            sys.argv = argv

    def test_valid_config_still_validates(self):
        (self.root / 'runtime').mkdir()
        for name in ('outbox.jsonl', 'unread.jsonl'):
            (self.root / 'runtime' / name).symlink_to('/dev/null')
        cfg = json.loads((Path(__file__).parent / 'config.example.json').read_text())
        cfg['channel'] = 'fixture-room'
        path = self.write('config.json', json.dumps(cfg))
        validate(load_config(path), self.root)


if __name__ == '__main__':
    unittest.main()
