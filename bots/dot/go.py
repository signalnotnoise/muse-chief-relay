#!/usr/bin/env python3
"""First-run setup and foreground supervision for the bridge and MCP Events backend."""
import argparse
import getpass
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import subprocess
import sys
import time
import urllib.request
from check_config import load_config, validate

BOT = Path(__file__).resolve().parent
REPO = BOT.parent.parent


def setup(path, room=None, approved=None):
    path = path.resolve()
    runtime = path.parent / 'runtime'
    runtime.mkdir(mode=0o700, parents=True, exist_ok=True)
    if runtime.is_symlink():
        raise ValueError('Runtime must be an ordinary persistent directory')
    if path.exists():
        cfg = load_config(path)
        validate(cfg, path.parent)  # preserve existing configuration; never silently opt it in
        if cfg.get('dot_mode') != 'participate' or cfg.get('durable_outbox') is not True:
            raise ValueError('Existing config preserved. Enable participate and durable_outbox as described in AGENTS.md')
    else:
        room = room or getpass.getpass('Room to join (hidden; saved only in ignored config): ').strip()
        approved = approved or input('Participants Dot may reply to (comma-separated exact nicknames): ').split(',')
        approved = [item.strip() for item in approved if item.strip()]
        if not room or not approved:
            raise ValueError('Room and explicitly approved participants are required')
        cfg = load_config(BOT / 'config.example.json')
        cfg.update(channel=room, dot_mode='participate', approved_recipients=approved,
                   durable_outbox=True, mcp_events={'principal': 'local-owner', 'auth_token_env': 'DOT_MCP_GATEWAY_TOKEN'})
        unread = runtime / 'unread.jsonl'
        if not unread.exists() and not unread.is_symlink():
            unread.symlink_to('/dev/null')
        outbox = runtime / 'outbox.jsonl'
        if outbox.is_symlink():
            raise ValueError('Existing receiver-only outbox preserved; follow the documented participation migration')
        outbox.touch(exist_ok=True)
        validate(cfg, path.parent)
        with path.open('x') as stream:
            json.dump(cfg, stream, indent=2)
            stream.write('\n')
        path.chmod(0o600)
    if not cfg.get('mcp_events', {}).get('principal'):
        raise ValueError('Existing config preserved. Configure mcp_events as described in AGENTS.md')
    token_path = runtime / 'gateway-token'
    if token_path.is_symlink():
        raise ValueError('Gateway token must be an ordinary private file')
    if not token_path.exists():
        with token_path.open('x') as stream:
            stream.write(secrets.token_urlsafe(48))
        token_path.chmod(0o600)
    token = token_path.read_text().strip()
    if len(token) < 32:
        raise ValueError('Gateway token file is invalid')
    return cfg, token


def sdk(runtime):
    candidates = [shutil.which('dotnet'), str(Path.home() / '.dotnet/dotnet'), str(runtime / 'tooling/dotnet/dotnet')]
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            result = subprocess.run([candidate, '--list-sdks'], capture_output=True, text=True, check=True)
            if any(line.startswith('8.') for line in result.stdout.splitlines()):
                return candidate
    installer = runtime / 'tooling/dotnet-install.sh'
    installer.parent.mkdir(parents=True, exist_ok=True)
    print('Installing Microsoft .NET 8 SDK in private runtime/tooling...', flush=True)
    with urllib.request.urlopen('https://dot.net/v1/dotnet-install.sh', timeout=30) as response:
        installer.write_bytes(response.read())
    target = runtime / 'tooling/dotnet'
    subprocess.run(['bash', str(installer), '--channel', '8.0', '--install-dir', str(target), '--no-path'], check=True)
    return str(target / 'dotnet')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=BOT / 'config.json')
    parser.add_argument('--room', help='Prefer the hidden interactive prompt to keep private rooms out of shell history')
    parser.add_argument('--approve', action='append', help='Exact nickname explicitly authorized for automatic replies; repeat per participant')
    parser.add_argument('--setup-only', action='store_true', help='Prepare private state without downloading, starting, or connecting')
    parser.add_argument('--port', type=int, default=8766)
    args = parser.parse_args()
    os.umask(0o077)
    path = args.config.resolve()
    children = []
    stopping = False
    def stop(*_):
        nonlocal stopping
        stopping = True
    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)
    try:
        cfg, token = setup(path, args.room, args.approve)
        if args.setup_only:
            print('Private setup prepared. MCP connection and subscription are still required.')
            return 0
        dotnet = sdk(path.parent / 'runtime')
        env = os.environ.copy()
        env.update(DOTNET_ROOT=str(Path(dotnet).parent), DOTNET_CLI_TELEMETRY_OPTOUT='1',
                   **{cfg['mcp_events'].get('auth_token_env', 'DOT_MCP_GATEWAY_TOKEN'): token})
        print('Building the bridge...', flush=True)
        subprocess.run([dotnet, 'build', str(REPO / 'src/ChatBridge'), '-c', 'Release', '--nologo'], env=env, check=True)
        bridge = [dotnet, str(REPO / 'src/ChatBridge/bin/Release/net8.0/ChatBridge.dll'), '--config', str(path)]
        events = [sys.executable, str(BOT / 'mcp_events.py'), '--config', str(path), '--port', str(args.port)]
        print('Starting bridge and MCP Events backend. Ctrl+C stops both.', flush=True)
        print('Dot wake is NOT connected until the HTTPS/OAuth gateway, plugin connection, and authorized subscription are verified.', flush=True)
        print('See bots/dot/AGENTS.md for the connection and actual wake/reply acceptance test.', flush=True)
        # Keep room names and raw inbox contents out of launcher output.
        log = path.parent / 'runtime/bridge.log'
        with log.open('ab') as output:
            children.append(subprocess.Popen(bridge, env=env, stdout=output, stderr=output))
            children.append(subprocess.Popen(events, env=env))
            while not stopping:
                if any(child.poll() is not None for child in children):
                    print('A service stopped. Stopping its companion; inspect private runtime logs.', flush=True)
                    return 1
                time.sleep(.5)
        return 0
    except (ValueError, OSError, subprocess.SubprocessError):
        print('Setup stopped. Existing config is preserved. Check AGENTS.md and private config; no secret values are printed.', file=sys.stderr)
        return 2
    finally:
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
        for child in children:
            try:
                child.wait(timeout=15)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait()


if __name__ == '__main__':
    raise SystemExit(main())
