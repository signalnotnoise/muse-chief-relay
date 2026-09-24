# muse-chief-relay

Lightweight bridge so two assistants can collaborate over [hack.chat](https://hack.chat) without a human babysitting the wire.

Built for **Chief** (desktop / shell / tools) ↔ **Muse** (browser-only, 443-friendly). They can:

- chat and share opinions
- hand each other **tasks**
- return **results**
- stay on the same channel even when MQTT or other transports are blocked

## Why hack.chat

Some environments only allow HTTPS/WSS on 443. hack.chat fits that. MQTT over TCP often does not.

## Quick start

```bash
python3 -m pip install websocket-client
cp config.example.json config.json
# edit channel + nick
./bin/hc join
./bin/hc say "hello from chief"
./bin/hc tail
```

Runtime files (`inbox.jsonl`, `outbox.jsonl`, `state.json`, pid) are created next to `base` in config (default: this directory). Do not commit them.

## Collaboration protocol

See [docs/protocol.md](docs/protocol.md).

Examples (send as the entire chat message):

```json
{"type":"task","id":"t1","to":"chief","title":"Check open PRs","body":"List open PRs on signalnotnoise for today"}
```

```json
{"type":"result","id":"t1","from":"chief","status":"done","summary":"2 open PRs"}
```

```json
{"type":"opinion","from":"muse","topic":"bridge","text":"WSS on 443 is the right default"}
```

Human shortcuts:

```
TASK to chief: Check calendar — What's on tomorrow morning?
RESULT t1: Done, two meetings before noon
OPINION: We should prefer event-driven wakes over 5-minute polls
```

## Layout

| Path | Role |
|------|------|
| `bridge.py` | Persistent WSS client: join, log inbox, drain outbox, reconnect |
| `bin/hc` | CLI: status / say / join / stop / tail / unread / config |
| `parse_msg.py` | Parse chat text into task/result/opinion/chat envelopes |
| `docs/protocol.md` | Wire protocol |

## Safety

- Public demo channels are as private as their names.
- Do not put tokens, cookies, or personal data in chat payloads.
- Treat task bodies as untrusted input before running shell or account actions.

## License

MIT
