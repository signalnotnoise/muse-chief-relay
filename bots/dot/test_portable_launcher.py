"""Offline launcher tests: synthetic config/DLL and a fake host, never a relay."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest


class PortableLauncherTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='dot-launcher-test-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        # Relocate the code and use spaces to catch hidden VM-path assumptions.
        self.repo = self.root / 'checkout with spaces'
        self.bot = self.repo / 'bots/dot'
        self.bot.mkdir(parents=True)
        source = Path(__file__).parent
        for name in ('env.sh', 'launch-bridge.sh', 'check_config.py'):
            shutil.copyfile(source / name, self.bot / name)
        # The thin wrappers delegate to bots/shared; mirror the repo layout so
        # the relocated copy resolves ../shared exactly like the real checkout.
        shutil.copytree(source.parent / 'shared', self.repo / 'bots/shared',
                        ignore=shutil.ignore_patterns('__pycache__', 'test_*'))
        self.launcher = self.bot / 'launch-bridge.sh'
        self.dll = self.repo / 'src/ChatBridge/bin/Release/net8.0/ChatBridge.dll'
        self.dll.parent.mkdir(parents=True)
        self.dll.write_bytes(b'fixture DLL; never executable')
        self.digest = hashlib.sha256(self.dll.read_bytes()).hexdigest()
        self.install = self.root / 'private install'
        self.runtime = self.install / 'runtime'
        self.runtime.mkdir(parents=True)
        for name in ('outbox.jsonl', 'unread.jsonl'):
            (self.runtime / name).symlink_to('/dev/null')
        self.config = self.install / 'config.json'
        self.cfg = json.loads((source / 'config.example.json').read_text())
        self.cfg.update(url='wss://relay.example.invalid/relay',
                        origin='https://relay.example.invalid', channel='fixture-room')
        self.write_config()
        self.capture = self.root / 'capture.json'
        self.host = self.root / 'fake dotnet'
        self.host.write_text(
            f'#!{sys.executable}\n'
            'import json, os, pathlib, sys\n'
            'mask = os.umask(0o077)\n'
            'os.umask(mask)\n'
            'pathlib.Path("created-by-fake-host").write_text("fixture")\n'
            f'pathlib.Path({str(self.capture)!r}).write_text(json.dumps({{\n'
            ' "args": sys.argv[1:], "env": dict(os.environ),\n'
            ' "cwd": os.getcwd(), "umask": mask, "pid": os.getpid()\n'
            '}))\n')
        self.host.chmod(0o700)
        self.env = {
            'PATH': os.pathsep.join((str(Path(sys.executable).parent), os.defpath)),
            'HOME': str(self.root / 'home'), 'LANG': 'C.UTF-8',
            'DOT_TOOLING_DIR': str(self.root / 'tooling'),
            'DOTNET_BIN': str(self.host), 'DOT_BRIDGE_CONFIG': str(self.config),
            'DOT_BRIDGE_SHA256': self.digest,
        }

    def write_config(self):
        self.config.write_text(json.dumps(self.cfg))

    def run_launcher(self, *args, extra_env=None):
        env = self.env.copy()
        env.update(extra_env or {})
        return subprocess.run(['bash', str(self.launcher), *args], cwd=self.root,
                              env=env, text=True, capture_output=True, timeout=10)

    def assert_refused(self, result, message=None):
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertFalse(self.capture.exists(), 'The fake host should not have executed')
        if message:
            self.assertIn(message, result.stderr)

    def participation(self):
        self.cfg.update(dot_mode='participate', approved_recipients=['fixture-owner'],
                        durable_outbox=True)
        (self.runtime / 'outbox.jsonl').unlink()
        (self.runtime / 'outbox.jsonl').touch()
        self.write_config()

    def test_offline_check_does_not_execute_host_or_create_tooling(self):
        result = self.run_launcher('check')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Offline check only', result.stdout)
        self.assertFalse(self.capture.exists())
        self.assertFalse((self.root / 'tooling').exists())

    def test_default_start_is_relocated_release_with_explicit_config(self):
        result = self.run_launcher()
        self.assertEqual(result.returncode, 0, result.stderr)
        capture = json.loads(self.capture.read_text())
        self.assertEqual(capture['args'], [str(self.dll), '--config', str(self.config)])
        self.assertEqual(capture['cwd'], str(self.install))

    def test_cli_paths_override_environment_and_accept_relative_paths(self):
        result = self.run_launcher('start', '--config', 'private install/config.json',
                                   '--dll', str(self.dll), '--sha256', self.digest.upper(),
                                   '--dotnet', str(self.host), extra_env={
                                       'DOT_BRIDGE_CONFIG': '/not/the/config',
                                       'DOT_BRIDGE_DLL': '/not/the/dll',
                                       'DOT_BRIDGE_SHA256': '0' * 64,
                                       'DOTNET_BIN': '/not/the/host'})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(self.capture.read_text())['args'][0], str(self.dll))

    def test_only_selected_environment_reaches_host_and_mirror_is_off(self):
        result = self.run_launcher('start', extra_env={
            'HIVEMIND_MESSAGE_MIRROR': '1', 'OPENAI_API_KEY': 'fake-never-a-key',
            'DOT_MCP_GATEWAY_TOKEN': 'fake-never-a-token', 'UNRELATED_SECRET': 'fixture',
            'CHATBRIDGE_CONFIG': '/wrong', 'MUSE_RELAY_CONFIG': '/wrong',
            'HTTPS_PROXY': 'http://proxy.example.invalid:8080',
            'SSL_CERT_FILE': '/fixture/ca.pem'})
        self.assertEqual(result.returncode, 0, result.stderr)
        env = json.loads(self.capture.read_text())['env']
        self.assertEqual(env['HIVEMIND_MESSAGE_MIRROR'], '0')
        for key in ('OPENAI_API_KEY', 'DOT_MCP_GATEWAY_TOKEN', 'UNRELATED_SECRET',
                    'CHATBRIDGE_CONFIG', 'MUSE_RELAY_CONFIG', 'DOT_BRIDGE_CONFIG',
                    'DOT_BRIDGE_SHA256', 'DOTNET_BIN', 'DOT_TOOLING_DIR'):
            self.assertNotIn(key, env)
        self.assertEqual(env['HTTPS_PROXY'], 'http://proxy.example.invalid:8080')
        self.assertEqual(env['SSL_CERT_FILE'], '/fixture/ca.pem')
        self.assertEqual(env['DOTNET_CLI_TELEMETRY_OPTOUT'], '1')

    def test_private_umask_and_exec_preserve_process_identity(self):
        process = subprocess.Popen(['bash', str(self.launcher), 'start'], cwd=self.root,
                                   env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stderr)
        capture = json.loads(self.capture.read_text())
        self.assertEqual(capture['pid'], process.pid)
        self.assertEqual(capture['umask'], 0o077)
        self.assertEqual(stat.S_IMODE((self.install / 'created-by-fake-host').stat().st_mode), 0o600)

    def test_native_exit_code_is_preserved(self):
        self.host.write_text(f'#!{sys.executable}\nraise SystemExit(17)\n')
        self.assertEqual(self.run_launcher('status').returncode, 17)

    def test_requires_config_even_with_ambient_bridge_config(self):
        self.assert_refused(self.run_launcher('check', extra_env={
            'DOT_BRIDGE_CONFIG': '', 'MUSE_RELAY_CONFIG': str(self.config)}), 'select --config')

    def test_requires_expected_hash_and_rejects_malformed_hash(self):
        for value in ('', 'not-a-hash', 'z' * 64, 'a' * 63):
            with self.subTest(value=value):
                self.assert_refused(self.run_launcher('check', extra_env={
                    'DOT_BRIDGE_SHA256': value}), '64 hex characters')

    def test_rejects_changed_binary(self):
        self.dll.write_bytes(b'different build')
        self.assert_refused(self.run_launcher('start'), 'DLL hash changed')

    def test_missing_files_or_host_fail_closed(self):
        for option in ('--config', '--dll', '--dotnet'):
            with self.subTest(option=option):
                self.assert_refused(self.run_launcher('check', option, '/does/not/exist'))

    def test_nonexecutable_host_is_refused(self):
        self.host.chmod(0o600)
        self.assert_refused(self.run_launcher('check'), 'dotnet executable is unavailable')

    def test_invalid_json_does_not_disclose_private_value(self):
        self.config.write_text('{"channel": "private-fixture" INVALID')
        result = self.run_launcher('check')
        self.assert_refused(result, 'config safety validation failed')
        self.assertNotIn('private-fixture', result.stderr)

    def test_invalid_config_shapes_fail_closed(self):
        for cfg in ([], None, {'url': 123}, {'url': 'wss://relay.example.invalid', 'channel': []}):
            with self.subTest(cfg=cfg):
                self.config.write_text(json.dumps(cfg))
                self.assert_refused(self.run_launcher('check'))

    def test_case_insensitive_dotnet_aliases_are_rejected_at_every_depth(self):
        original = self.cfg.copy()
        for key, value in (('PROTOCOL_V2', True), ('BASE', '/tmp/other-state'),
                           ('PASS', 'fixture-password'), ('NICK', 'other'),
                           ('auto_ack', {'enabled': False, 'Enabled': True}),
                           ('mentions', {'enabled': False, 'ENABLED': True})):
            with self.subTest(key=key):
                self.cfg = {**original, key: value}
                self.write_config()
                self.assert_refused(self.run_launcher('check'), 'config safety validation failed')

    def test_duplicate_keys_are_rejected_including_nested_objects(self):
        for old, replacement in (
                ('"nick": "dot"', '"nick": "other", "nick": "dot"'),
                ('"enabled": false', '"enabled": true, "enabled": false')):
            with self.subTest(old=old):
                self.config.write_text(json.dumps(self.cfg).replace(old, replacement, 1))
                self.assert_refused(self.run_launcher('check'), 'config safety validation failed')

    def test_unsafe_config_options_are_refused(self):
        original = self.cfg.copy()
        for key, value in (
                ('nick', 'someone-else'), ('channel', 'your-channel-name'),
                ('base', '/tmp'), ('pass', 'fixture'), ('hook', {}),
                ('auto_ack', {'enabled': True}), ('protocol_v2', True),
                ('mentions', {'enabled': True}), ('mentions', {}),
                ('durable_outbox', True), ('durable_outbox', 'false')):
            with self.subTest(key=key):
                self.cfg = {**original, key: value}
                self.write_config()
                self.assert_refused(self.run_launcher('start'))

    def test_runtime_symlink_is_refused(self):
        target = self.install / 'actual-runtime'
        self.runtime.rename(target)
        self.runtime.symlink_to(target, target_is_directory=True)
        self.assert_refused(self.run_launcher('check'), 'ordinary persistent directory')

    def test_receive_only_requires_dev_null_links(self):
        for name in ('unread.jsonl', 'outbox.jsonl'):
            with self.subTest(name=name):
                path = self.runtime / name
                path.unlink()
                path.touch()
                self.assert_refused(self.run_launcher('check'))
                path.unlink()
                path.symlink_to('/dev/null')

    def test_participation_requires_approved_recipients_and_regular_outbox(self):
        self.participation()
        self.cfg['approved_recipients'] = []
        self.write_config()
        self.assert_refused(self.run_launcher('check'))
        self.cfg['approved_recipients'] = ['fixture-owner']
        self.write_config()
        (self.runtime / 'outbox.jsonl').unlink()
        (self.runtime / 'outbox.jsonl').symlink_to('/dev/null')
        self.assert_refused(self.run_launcher('check'))

    def test_native_control_command_mapping(self):
        self.participation()
        for action, expected in (
                (['status'], ['status']), (['stop'], ['stop']),
                (['outbox-status'], ['outbox', 'status']),
                (['outbox-resolve', 'fixture-id', 'requeue'], ['outbox', 'resolve', 'fixture-id', 'requeue']),
                (['outbox-resolve', 'fixture-id', 'drop'], ['outbox', 'resolve', 'fixture-id', 'drop'])):
            with self.subTest(action=action):
                result = self.run_launcher(*action)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(self.capture.read_text())['args'],
                                 [str(self.dll), *expected, '--config', str(self.config)])

    def test_outbox_commands_refuse_without_durable_configuration(self):
        self.assert_refused(self.run_launcher('outbox-status'), 'already configured durable_outbox')

    def test_no_extra_arguments_or_implicit_restart(self):
        for args in (['restart'], ['status', 'extra'], ['outbox-resolve'],
                     ['outbox-resolve', 'fixture-id'], ['start', '--unknown']):
            with self.subTest(args=args):
                self.assert_refused(self.run_launcher(*args))

    def test_help_needs_no_config_hash_or_runtime(self):
        result = self.run_launcher('--help', extra_env={
            'DOT_BRIDGE_CONFIG': '', 'DOT_BRIDGE_SHA256': '', 'DOTNET_BIN': '/absent'})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('launch-bridge.sh', result.stdout)
        self.assertFalse(self.capture.exists())

    def test_env_helper_uses_system_host_without_private_install(self):
        result = self.run_launcher('start')
        self.assertEqual(result.returncode, 0, result.stderr)
        env = json.loads(self.capture.read_text())['env']
        self.assertNotIn('DOTNET_ROOT', env)
        self.assertEqual(env['NUGET_PACKAGES'], str(self.root / 'tooling/nuget'))

    def test_env_helper_selects_private_host_and_is_idempotent(self):
        dotnet_root = self.root / 'tooling/dotnet'
        dotnet_root.mkdir(parents=True)
        shutil.copyfile(self.host, dotnet_root / 'dotnet')
        (dotnet_root / 'dotnet').chmod(0o700)
        result = self.run_launcher('start', extra_env={'DOTNET_BIN': ''})
        self.assertEqual(result.returncode, 0, result.stderr)
        env = json.loads(self.capture.read_text())['env']
        self.assertEqual(env['DOTNET_ROOT'], str(dotnet_root))
        result = subprocess.run(['bash', '-c', 'source "$1"; source "$1"; printf "%s" "$PATH"',
                                 'fixture', str(self.bot / 'env.sh')], env=self.env,
                                capture_output=True, text=True, check=True)
        self.assertEqual(result.stdout.split(os.pathsep).count(str(dotnet_root)), 1)

    def test_env_helper_rejects_relative_tooling_and_runtime_roots(self):
        for key in ('DOT_TOOLING_DIR', 'DOTNET_ROOT'):
            with self.subTest(key=key):
                self.assert_refused(self.run_launcher('check', extra_env={key: 'relative'}), 'absolute path')

    def test_explicit_env_overrides_are_preserved(self):
        result = self.run_launcher('start', extra_env={
            'DOTNET_ROOT': str(self.root / 'custom-runtime'),
            'DOTNET_CLI_HOME': str(self.root / 'custom-home'),
            'NUGET_PACKAGES': str(self.root / 'custom-packages'),
            'NUGET_HTTP_CACHE_PATH': str(self.root / 'custom-http'),
            'XDG_DATA_HOME': str(self.root / 'custom-data')})
        self.assertEqual(result.returncode, 0, result.stderr)
        env = json.loads(self.capture.read_text())['env']
        for key in ('DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES',
                    'NUGET_HTTP_CACHE_PATH', 'XDG_DATA_HOME'):
            self.assertIn('/custom-', env[key])


if __name__ == '__main__':
    unittest.main()
