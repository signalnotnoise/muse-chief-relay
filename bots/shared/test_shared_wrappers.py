"""Thin-wrapper tests: both bots' wrappers drive the single shared launcher.

Runs the real committed wrappers against temporary synthetic configs/DLLs and
a fake dotnet host. Never a relay, never the real runtimes, never the network.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parent.parent.parent
SHARED = REPO / 'bots' / 'shared'


class WrapperTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='shared-wrapper-test-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.dll = self.root / 'ChatBridge.dll'
        self.dll.write_bytes(b'fixture DLL; never executable')
        self.digest = hashlib.sha256(self.dll.read_bytes()).hexdigest()
        self.install = self.root / 'install'
        self.runtime = self.install / 'runtime'
        self.runtime.mkdir(parents=True)
        for name in ('outbox.jsonl', 'unread.jsonl'):
            (self.runtime / name).symlink_to('/dev/null')
        self.config = self.install / 'config.json'
        self.capture = self.root / 'capture.json'
        self.host = self.root / 'fake dotnet'
        self.host.write_text(
            f'#!{sys.executable}\n'
            'import json, os, pathlib, sys\n'
            f'pathlib.Path({str(self.capture)!r}).write_text(json.dumps({{\n'
            ' "args": sys.argv[1:], "env": dict(os.environ)}))\n')
        self.host.chmod(0o700)

    def base_config(self, nick):
        return {
            'url': 'wss://relay.example.invalid/relay',
            'origin': 'https://relay.example.invalid',
            'channel': 'fixture-room',
            'nick': nick,
            'trip': '',
            'pass': '',
            'base': 'runtime',
            'auto_ack': {'enabled': False},
            'mentions': {'enabled': False},
        }

    def write_config(self, cfg):
        self.config.write_text(json.dumps(cfg))

    def run_launcher(self, bot, *args, extra_env=None):
        env = {
            'PATH': os.pathsep.join((str(Path(sys.executable).parent), os.defpath)),
            'HOME': str(self.root / 'home'),
            'LANG': 'C.UTF-8',
            'DOTNET_BIN': str(self.host),
        }
        env.update(extra_env or {})
        return subprocess.run(
            ['bash', str(REPO / 'bots' / bot / 'launch-bridge.sh'), *args],
            cwd=self.root, env=env, text=True, capture_output=True, timeout=15)

    def assert_refused(self, result, message=None):
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertFalse(self.capture.exists(), 'The fake host should not have executed')
        if message:
            self.assertIn(message, result.stderr)

    def test_dot_wrapper_check_passes_offline(self):
        self.write_config(self.base_config('dot'))
        result = self.run_launcher('dot', 'check', extra_env={
            'DOT_BRIDGE_CONFIG': str(self.config), 'DOT_BRIDGE_SHA256': self.digest,
            'DOT_BRIDGE_DLL': str(self.dll)})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Offline check only', result.stdout)
        self.assertFalse(self.capture.exists())

    def test_fuse_wrapper_check_passes_offline(self):
        self.write_config(self.base_config('Fuse'))
        result = self.run_launcher('fuse', 'check', extra_env={
            'FUSE_BRIDGE_CONFIG': str(self.config), 'FUSE_BRIDGE_SHA256': self.digest,
            'FUSE_BRIDGE_DLL': str(self.dll)})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Offline check only', result.stdout)
        self.assertFalse(self.capture.exists())

    def test_fuse_wrapper_rejects_other_nick(self):
        self.write_config(self.base_config('dot'))
        result = self.run_launcher('fuse', 'check', extra_env={
            'FUSE_BRIDGE_CONFIG': str(self.config), 'FUSE_BRIDGE_SHA256': self.digest,
            'FUSE_BRIDGE_DLL': str(self.dll)})
        # Fail-closed like the original: the specific nick mismatch is not
        # disclosed; the launch is refused without executing the host.
        self.assert_refused(result, 'config safety validation failed')

    def test_fuse_wrapper_rejects_receive_only_with_durable_outbox(self):
        cfg = self.base_config('Fuse')
        cfg['durable_outbox'] = True
        self.write_config(cfg)
        result = self.run_launcher('fuse', 'check', extra_env={
            'FUSE_BRIDGE_CONFIG': str(self.config), 'FUSE_BRIDGE_SHA256': self.digest,
            'FUSE_BRIDGE_DLL': str(self.dll)})
        self.assert_refused(result, 'receive-only mode must not enable durable_outbox')

    def test_fuse_mode_key_is_per_bot(self):
        # dot_mode is not Fuse's mode key: it is ignored, fuse_mode governs.
        cfg = self.base_config('Fuse')
        cfg['dot_mode'] = 'participate'
        self.write_config(cfg)
        result = self.run_launcher('fuse', 'check', extra_env={
            'FUSE_BRIDGE_CONFIG': str(self.config), 'FUSE_BRIDGE_SHA256': self.digest,
            'FUSE_BRIDGE_DLL': str(self.dll)})
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_only_selected_environment_reaches_host(self):
        self.write_config(self.base_config('Fuse'))
        result = self.run_launcher('fuse', 'start', extra_env={
            'FUSE_BRIDGE_CONFIG': str(self.config), 'FUSE_BRIDGE_SHA256': self.digest,
            'FUSE_BRIDGE_DLL': str(self.dll),
            'OPENAI_API_KEY': 'fake-never-a-key', 'FUSE_TOOLING_DIR': '/fixture/tooling',
            'HTTPS_PROXY': 'http://proxy.example.invalid:8080'})
        self.assertEqual(result.returncode, 0, result.stderr)
        env = json.loads(self.capture.read_text())['env']
        self.assertEqual(env['HIVEMIND_MESSAGE_MIRROR'], '0')
        self.assertEqual(env['DOTNET_CLI_TELEMETRY_OPTOUT'], '1')
        self.assertEqual(env['HTTPS_PROXY'], 'http://proxy.example.invalid:8080')
        for key in ('OPENAI_API_KEY', 'FUSE_BRIDGE_CONFIG', 'FUSE_BRIDGE_SHA256',
                    'FUSE_BRIDGE_DLL', 'FUSE_TOOLING_DIR', 'DOTNET_BIN'):
            self.assertNotIn(key, env)

    def test_shared_launcher_refuses_without_wrapper_contract(self):
        env = {'PATH': os.defpath, 'HOME': str(self.root / 'home'), 'LANG': 'C.UTF-8'}
        result = subprocess.run(
            [sys.executable, str(SHARED / 'launch_bridge.py'), 'check'],
            cwd=self.root, env=env, text=True, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 2)
        self.assertIn('BRIDGE_CONFIG_ENV', result.stderr)

    def test_env_wrappers_select_private_tooling_dirs(self):
        for bot, var in (('dot', 'DOT_TOOLING_DIR'), ('fuse', 'FUSE_TOOLING_DIR')):
            script = (f'source {REPO / "bots" / bot / "env.sh"} && '
                      f'printf "%s\\n" "${var}" "$BRIDGE_TOOLING_VAR" '
                      '"$DOTNET_CLI_TELEMETRY_OPTOUT"')
            result = subprocess.run(['bash', '-c', script], text=True,
                                    capture_output=True, timeout=15,
                                    env={'PATH': os.defpath, 'HOME': str(self.root)})
            self.assertEqual(result.returncode, 0, result.stderr)
            tooling, contract, telemetry = result.stdout.splitlines()
            self.assertEqual(tooling, str(REPO / 'bots' / bot / 'runtime/tooling'))
            self.assertEqual(contract, var)
            self.assertEqual(telemetry, '1')


if __name__ == '__main__':
    unittest.main()
