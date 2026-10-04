#!/usr/bin/env python3
"""Validate the configured receiver or explicitly authorized participation mode."""
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit


def _strict_object(pairs):
    # The bridge binds property names case-insensitively; Python dict lookups
    # do not. Reject aliases and duplicates at every depth so the safety
    # validator and the bridge cannot interpret different settings from the
    # same configuration file (mirrors launch-bridge.sh canonical_object).
    result = {}
    for key, value in pairs:
        if key != key.lower() or key in result:
            raise ValueError('config keys must be lowercase and unique')
        result[key] = value
    return result


def load_config(path):
    """Parse a bridge config file, rejecting duplicate or mixed-case keys."""
    return json.loads(Path(path).read_text(), object_pairs_hook=_strict_object)


def validate(config, bot_dir):
    endpoint = urlsplit(config.get('url', ''))
    if endpoint.scheme not in ('ws', 'wss') or not endpoint.hostname or endpoint.username or endpoint.password or endpoint.query or endpoint.fragment:
        raise ValueError('Set a plain ws/wss relay endpoint without credentials, query, or fragment')
    if not config.get('channel', '').strip() or config['channel'] == 'your-channel-name':
        raise ValueError('Configure the intended room locally before starting')
    if config.get('nick') != 'dot':
        raise ValueError('This deployment must use nick dot')
    if config.get('pass') or config.get('hook') is not None:
        raise ValueError('Passwords and hooks are disabled in this receive-only setup')
    ack = config.get('auto_ack')
    if not isinstance(ack, dict) or ack.get('enabled') is not False:
        raise ValueError('auto_ack.enabled must explicitly be false')
    if config.get('base') != 'runtime':
        raise ValueError('base must be runtime to use the isolated local inbox/outbox')
    trip = config.get('trip', '')
    if trip and not re.fullmatch(r'!?[A-Za-z0-9+/]{6}', trip):
        raise ValueError('Trip must be empty or a public six-character code')
    if config.get('durable_outbox') and config.get('protocol_v2'):
        raise ValueError('durable_outbox is for v1; v2 has a separate durable queue')
    mode = config.get('dot_mode', 'receive-only')
    if mode not in ('receive-only', 'participate'):
        raise ValueError('Unknown dot_mode')
    unread = bot_dir / 'runtime/unread.jsonl'
    if not unread.is_symlink() or unread.resolve() != Path('/dev/null'):
        raise ValueError('runtime/unread.jsonl must point to /dev/null')
    outbox = bot_dir / 'runtime/outbox.jsonl'
    if mode == 'receive-only':
        if not outbox.is_symlink() or outbox.resolve() != Path('/dev/null'):
            raise ValueError('Receive-only outbox must point to /dev/null')
    else:
        if not isinstance(config.get('approved_recipients'), list) or not config['approved_recipients'] or not all(isinstance(x,str) and x for x in config['approved_recipients']):
            raise ValueError('Participation requires the explicitly approved recipient list')
        if outbox.is_symlink() or not outbox.is_file():
            raise ValueError('Participation outbox must be an ordinary local file')



def main():
    path = Path(sys.argv[1]).resolve()
    try:
        validate(load_config(path), path.parent)
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print(f'Refusing start: {exc}', file=sys.stderr)
        return 2
    print('Configured safety controls verified')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
