#!/usr/bin/env python3
"""Shared hash-pinned local bridge launcher (one implementation for all bots).

Invoked via a bot's thin launch-bridge.sh wrapper, which exports the
parameter contract below and then execs this file. Never import this module;
it parses argv and execs the bridge on success.

Wrapper contract (environment):
  BRIDGE_CONFIG_ENV  name of the env var holding the default --config path
  BRIDGE_SHA256_ENV  name of the env var holding the default expected DLL hash
  BRIDGE_DLL_ENV     name of the env var holding the default --dll path
  BRIDGE_DLL_DEFAULT default DLL path used when neither --dll nor the env var
                     is set (the wrapper resolves it repo-relative)
  BRIDGE_DOTNET_ENV  name of the env var holding the default --dotnet
                     executable (default: DOTNET_BIN)
  BRIDGE_NICK        expected bridge nick, enforced by config validation
  BRIDGE_MODE_KEY    per-bot participation mode key (e.g. dot_mode/fuse_mode/chief_mode)
  BRIDGE_DOCS        docs path named in refusal/help text
                     (default: bots/shared/README.md)
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys

# Private file-creation mask for this process and the exec'd bridge. This is
# the single place the mask is set; thin wrappers must not set their own.
os.umask(0o077)

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from check_config import validate


def required_env(name, default=None):
    value = os.environ.get(name, default)
    if not value:
        print(f'Refusing launch: wrapper must export {name}', file=sys.stderr)
        raise SystemExit(2)
    return value


CONFIG_ENV = required_env('BRIDGE_CONFIG_ENV')
SHA256_ENV = required_env('BRIDGE_SHA256_ENV')
DLL_ENV = required_env('BRIDGE_DLL_ENV')
DOTNET_ENV = os.environ.get('BRIDGE_DOTNET_ENV', 'DOTNET_BIN')
DLL_DEFAULT = required_env('BRIDGE_DLL_DEFAULT')
NICK = required_env('BRIDGE_NICK')
MODE_KEY = required_env('BRIDGE_MODE_KEY')
DOCS = os.environ.get('BRIDGE_DOCS', 'bots/shared/README.md')

parser = argparse.ArgumentParser(
    prog='launch-bridge.sh',
    description='Hash-pinned local v1 bridge. check is offline; start connects to the configured room.',
    epilog=f'See {DOCS}. There is no automatic restart or independent model wake.')
parser.add_argument('action', nargs='?', default='start', choices=(
    'check', 'start', 'status', 'stop', 'outbox-status', 'outbox-resolve'))
parser.add_argument('reply_id', nargs='?', help='Only for outbox-resolve: the existing reply ID')
parser.add_argument('decision', nargs='?', choices=('requeue', 'drop'), help='Only for outbox-resolve')
parser.add_argument('--config', default=os.environ.get(CONFIG_ENV),
                    help=f'Required explicit config path, or {CONFIG_ENV}')
parser.add_argument('--sha256', default=os.environ.get(SHA256_ENV),
                    help=f'Required expected DLL SHA-256, or {SHA256_ENV}')
parser.add_argument('--dll', default=os.environ.get(DLL_ENV) or DLL_DEFAULT)
parser.add_argument('--dotnet', default=os.environ.get(DOTNET_ENV) or 'dotnet',
                    help=f'Executable path/name, or {DOTNET_ENV} (default: dotnet on PATH)')
args = parser.parse_args()


def refuse(message):
    print('Refusing launch: ' + message, file=sys.stderr)
    raise SystemExit(2)


def canonical_object(pairs):
    # .NET binds property names case-insensitively; Python dict lookups do not.
    # Reject aliases and duplicates at every depth so the safety validator and
    # bridge cannot interpret different settings from the same configuration.
    result = {}
    for key, value in pairs:
        if key != key.lower() or key in result:
            raise ValueError('config keys must be lowercase and unique')
        result[key] = value
    return result


if not args.config:
    refuse(f'select --config or {CONFIG_ENV}; no config is chosen automatically')
if not args.sha256 or not re.fullmatch(r'[0-9a-fA-F]{64}', args.sha256):
    refuse(f'set --sha256 or {SHA256_ENV} to the reviewed DLL hash (64 hex characters)')
if args.action == 'outbox-resolve':
    if not args.reply_id or not args.decision:
        refuse('outbox-resolve requires REPLY_ID and requeue|drop; stop the bridge first')
elif args.reply_id or args.decision:
    refuse('extra positional arguments are only allowed for outbox-resolve')

try:
    dll = Path(args.dll).expanduser().resolve(strict=True)
    config_path = Path(args.config).expanduser().resolve(strict=True)
    if not dll.is_file() or not config_path.is_file():
        refuse('DLL and config must be existing files')
    digest = hashlib.sha256()
    with dll.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    if digest.hexdigest() != args.sha256.lower():
        refuse('DLL hash changed; review the build before deliberately updating the expected hash')
    config = json.loads(config_path.read_text(), object_pairs_hook=canonical_object)
    if not isinstance(config, dict):
        refuse('config must be a JSON object')
    runtime = config_path.parent / 'runtime'
    if runtime.is_symlink() or not runtime.is_dir():
        refuse('runtime beside the config must be an ordinary persistent directory')
    validate(config, config_path.parent, nick=NICK, mode_key=MODE_KEY)
    if config.get('protocol_v2', False) is not False:
        refuse('this local launcher requires protocol_v2 absent or false')
    if not isinstance(config.get('mentions'), dict) or config['mentions'].get('enabled') is not False:
        refuse('this local launcher requires mentions.enabled explicitly false')
    if not isinstance(config.get('durable_outbox', False), bool):
        refuse('durable_outbox must be a JSON boolean')
    if config.get(MODE_KEY, 'receive-only') == 'receive-only' and config.get('durable_outbox'):
        refuse('receive-only mode must not enable durable_outbox')
    if args.action.startswith('outbox-') and config.get('durable_outbox') is not True:
        refuse('outbox commands require an already configured durable_outbox; do not enable it just to inspect')
except (OSError, RuntimeError):
    refuse('cannot read the selected DLL/config/runtime; check paths and permissions')
except (ValueError, TypeError, KeyError, AttributeError):
    # Do not print invalid JSON or values that could contain private room data.
    refuse(f'config safety validation failed; see {DOCS} and check_config.py')

dotnet = shutil.which(os.path.expanduser(args.dotnet))
if not dotnet:
    refuse('selected dotnet executable is unavailable; install .NET 8 or select --dotnet/'
           + DOTNET_ENV)
dotnet = str(Path(dotnet).resolve())

# Deliberately do not forward credentials, plugin state, alternate bridge config,
# or model-provider variables. Proxies/CA paths retain the intended VM networking.
keys = {
    'PATH', 'HOME', 'LANG', 'LC_ALL', 'LC_CTYPE', 'DOTNET_ROOT', 'DOTNET_CLI_HOME',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
    'XDG_DATA_HOME', 'TMPDIR', 'SSL_CERT_FILE', 'SSL_CERT_DIR',
    'ALL_PROXY', 'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'WS_PROXY', 'WSS_PROXY',
    'all_proxy', 'http_proxy', 'https_proxy', 'no_proxy', 'ws_proxy', 'wss_proxy',
    'BUNDLE_HTTP_PROXY', 'BUNDLE_HTTPS_PROXY',
}
env = {key: value for key, value in os.environ.items() if key in keys}
env['HIVEMIND_MESSAGE_MIRROR'] = '0'

if args.action == 'check':
    print('Verified DLL hash, explicit config, local safety controls, and dotnet executable.')
    print('Offline check only: no connection, runtime-version check, observer, or independent wake tested.')
    raise SystemExit(0)

commands = {
    'start': [], 'status': ['status'], 'stop': ['stop'],
    'outbox-status': ['outbox', 'status'],
    'outbox-resolve': ['outbox', 'resolve', args.reply_id, args.decision],
}
command = [dotnet, str(dll), *commands[args.action], '--config', str(config_path)]
try:
    # Stable working directory; base=runtime remains relative to the config file.
    os.chdir(config_path.parent)
    os.execve(dotnet, command, env)
except OSError:
    refuse('could not execute the selected dotnet host; check its permissions and installation')
