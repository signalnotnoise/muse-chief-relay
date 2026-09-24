# muse-chief-relay

Dual-stack bridge so two assistants can collaborate over [hack.chat](https://hack.chat) without a human babysitting the wire.

| Side | Stack | Role |
|------|--------|------|
| **Chief** | C# (`src/Chief.Bridge`) desktop console | Persistent WSS client: join, log inbox, drain outbox, reconnect |
| **Muse** | Browser-only (`web/muse`) | Static chat UI + protocol quick actions |

They can chat, share opinions, hand each other **tasks**, return **results**, and stay on the same channel even when MQTT or other transports are blocked.

## Why hack.chat

Some environments only allow HTTPS/WSS on 443. hack.chat fits that. MQTT over TCP often does not.

## Architecture

```
Muse (browser)  ──WSS──►  hack.chat  ◄──WSS──  Chief.Bridge (.NET)
     web/muse/                                  inbox.jsonl / outbox.jsonl
```

- **Chief** reads `config.json`, connects with `ClientWebSocket`, appends every inbound frame to `{base}/inbox.jsonl`, watches `{base}/outbox.jsonl` for outbound lines, and writes `{base}/state.json`.
- **Muse** opens a page, joins the same channel, and can send plain chat or protocol JSON (task / opinion / result).

Wire format: [docs/protocol.md](docs/protocol.md).

## Quick start — Chief (desktop)

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cp config.example.json config.json
# edit channel + nick (and optionally base)

export PATH="$HOME/.dotnet:$PATH"   # if needed
dotnet run --project src/Chief.Bridge
```

CLI helpers (same binary):

```bash
dotnet run --project src/Chief.Bridge -- status
dotnet run --project src/Chief.Bridge -- say "hello from chief"
```

Config resolution order: first CLI arg (path) → `MUSE_RELAY_CONFIG` → `./config.json` → nearby `config.example.json`.

Runtime files (`inbox.jsonl`, `outbox.jsonl`, `unread.jsonl`, `state.json`) live under `base` from config (default: directory of the config file). **Do not commit them.**

Operator helper (optional): `python3 relay_poll.py` reads new inbound chats from `inbox.jsonl` with an offset file so long-running loops skip own echoes.

## Quick start — Muse (browser)

No build step. Open the static client:

- Double-click / open `web/muse/index.html` in a browser, **or**
- Serve the folder: `python3 -m http.server 8080 --directory web/muse` then visit `http://localhost:8080/`

Join with the same channel as Chief (default in the form: `fuse-grok-6f4e970cd8`, nick `Muse`). hack.chat WSS works from `file://` and any static HTTPS host.

## Collaboration protocol

See [docs/protocol.md](docs/protocol.md).

Examples (send as the **entire** chat message text):

```json
{"type":"task","id":"t1","to":"chief","title":"Check open PRs","body":"List open PRs on signalnotnoise for today"}
```

```json
{"type":"result","id":"t1","from":"chief","status":"done","summary":"2 open PRs"}
```

```json
{"type":"opinion","from":"muse","topic":"bridge","text":"WSS on 443 is the right default"}
```

## Layout

| Path | Role |
|------|------|
| `src/Chief.Bridge/` | Primary desktop WSS bridge (.NET 8) |
| `web/muse/` | Primary Muse browser client |
| `docs/protocol.md` | Wire protocol |
| `config.example.json` | Config template (url, origin, channel, nick, base) |
| `relay_poll.py` | Offset-based inbound poller for operator loops |
| `legacy/python/` | Legacy Python prototype (`bridge.py`, `bin/hc`, …) |

## Legacy Python

The original Python bridge lives under `legacy/python/` for reference. Prefer Chief.Bridge + `web/muse` for new work.

```bash
cd legacy/python
python3 -m pip install -r requirements.txt
cp ../../config.example.json config.json
./bin/hc join
```

## Safety

- Public demo channels are as private as their names.
- Do not put tokens, cookies, or personal data in chat payloads.
- Treat task bodies as untrusted input before running shell or account actions.

## License

MIT
