#!/usr/bin/env bash
# Local bridge only. See LOCAL_SETUP.md before starting or recovering a queue.
# No installer, observer, model invocation, supervisor, or implicit restart.
set -euo pipefail
umask 077
BOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$BOT_DIR/env.sh"
exec python3 -B - "$BOT_DIR" "$@" <<'PY'
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys

bot = Path(sys.argv[1])
sys.path.insert(0, str(bot))
# Same first-pass import as bots/fuse/launch-bridge.sh: check_config.py is not
# in this directory. It still lives with Dot until shared-helper extraction.
# This script does not add bots/dot to sys.path.
from check_config import validate

parser = argparse.ArgumentParser(
    prog='launch-bridge.sh',
    description='Hash-pinned local v1 bridge. check is offline; start connects to the configured room.',
    epilog='See bots/grok/LOCAL_SETUP.md. There is no automatic restart or independent model wake.')
parser.add_argument('action', nargs='?', default='start', choices=(
    'check', 'start', 'status', 'stop', 'outbox-status', 'outbox-resolve'))
parser.add_argument('reply_id', nargs='?', help='Only for outbox-resolve: the existing reply ID')
parser.add_argument('decision', nargs='?', choices=('requeue', 'drop'), help='Only for outbox-resolve')
parser.add_argument('--config', default=os.environ.get('GROK_BRIDGE_CONFIG'),
                    help='Required explicit config path, or GROK_BRIDGE_CONFIG')
parser.add_argument('--sha256', default=os.environ.get('GROK_BRIDGE_SHA256'),
                    help='Required expected DLL SHA-256, or GROK_BRIDGE_SHA256')
parser.add_argument('--dll', default=os.environ.get('GROK_BRIDGE_DLL') or str(
    bot.parent.parent / 'src/ChatBridge/bin/Release/net8.0/ChatBridge.dll'))
parser.add_argument('--dotnet', default=os.environ.get('DOTNET_BIN') or 'dotnet',
                    help='Executable path/name, or DOTNET_BIN (default: dotnet on PATH)')
args = parser.parse_args(sys.argv[2:])


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
    refuse('select --config or GROK_BRIDGE_CONFIG; no config is chosen automatically')
if not args.sha256 or not re.fullmatch(r'[0-9a-fA-F]{64}', args.sha256):
    refuse('set --sha256 or GROK_BRIDGE_SHA256 to the reviewed DLL hash (64 hex characters)')
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
    validate(config, config_path.parent)
    if config.get('protocol_v2', False) is not False:
        refuse('this local launcher requires protocol_v2 absent or false')
    if not isinstance(config.get('mentions'), dict) or config['mentions'].get('enabled') is not False:
        refuse('this local launcher requires mentions.enabled explicitly false')
    if not isinstance(config.get('durable_outbox', False), bool):
        refuse('durable_outbox must be a JSON boolean')
    # Mode key is still dot_mode, matching bots/dot and the fuse-vm-install first
    # pass. A Grok-specific mode field is intentionally not introduced here.
    if config.get('dot_mode', 'receive-only') == 'receive-only' and config.get('durable_outbox'):
        refuse('receive-only mode must not enable durable_outbox')
    if args.action.startswith('outbox-') and config.get('durable_outbox') is not True:
        refuse('outbox commands require an already configured durable_outbox; do not enable it just to inspect')
except (OSError, RuntimeError):
    refuse('cannot read the selected DLL/config/runtime; check paths and permissions')
except (ValueError, TypeError, KeyError, AttributeError):
    # Do not print invalid JSON or values that could contain private room data.
    refuse('config safety validation failed; see LOCAL_SETUP.md and check_config.py')

dotnet = shutil.which(os.path.expanduser(args.dotnet))
if not dotnet:
    refuse('selected dotnet executable is unavailable; install .NET 8 or select --dotnet/DOTNET_BIN')
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
PY
