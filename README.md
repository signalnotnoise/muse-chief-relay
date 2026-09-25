# muse-chief-relay

**Built because I was bored.**

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
- **Status view** (optional): `tools/status.py` turns an inbox log into `docs/status.json`, and `docs/status/` renders it. Publishing fails closed (see below).

Wire format: [docs/protocol.md](docs/protocol.md).

## Quick start — Chief (desktop)

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cp config.example.json config.json
# edit channel + nick (and optionally base and pass)

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

## Configuration (`config.json`)

Copy `config.example.json` to `config.json`. `config.json` is gitignored. Keep it that way, because it can hold your pass.

| Field | Used by | Meaning |
|---|---|---|
| `url` | bridge | hack.chat WebSocket endpoint, normally `wss://hack.chat/chat-ws` |
| `origin` | bridge | Origin header sent on connect (`https://hack.chat`) |
| `channel` | bridge | Channel to join. Anyone who knows the name can read it. |
| `nick` | bridge | Nick for the bridge, e.g. `chief` |
| `pass` | bridge (.NET and legacy Python) | Optional hack.chat password. It gives the nick a **tripcode**. It is sent only in the join frame and is never written to logs. |
| `base` | bridge | Directory for runtime files. Default: the config file's directory. |
| `publish_repos` | `tools/status.py` | Allowlist of `owner/name` repos whose tasks can appear in the status view. Default: this repo. Compared case-insensitively. |
| `publish_trips` | `tools/status.py` | Tripcodes allowed to publish. Every task, ack and result must carry one, **including the bridge's own**. An empty or missing list publishes nothing. |

### Tripcodes and the pass

hack.chat derives a short tripcode (e.g. `Ab12Cd`) from the password you join with. The same password always gives the same trip, and nobody else can produce it without the password. Nicks are first come, first served, so anyone can join as `chief` or `Muse`. **Trust the trip, not the nick.**

- Give the bridge a trip by setting `pass`. Treat the pass like a password: keep it out of chat, logs, commits and screenshots.
- Put the bridge's own trip in `publish_trips`, or its acks and results won't count.
- The Muse web client does not send a password yet, so its messages carry no trip. They won't count for publishing, and operators who gate commands on trips will treat them as chat.

## Status view and fail-closed publishing

```bash
python3 tools/status.py --config config.json --inbox inbox.jsonl --out docs/status.json
python3 tools/test_status.py        # unit tests
```

- Only tasks with a `repo` on the `publish_repos` list are published. Untagged tasks, and tasks for any other repo, are left out. A private-repo task stays private simply by leaving `repo` off.
- Every counted message must carry a trip in `publish_trips`. An empty list means nothing is published, so a fresh fork shows nothing until it's configured.
- Shortcut tasks (`TASK to chief: ...`) have no id or repo, so they never show up.
- The output includes `coverage`: the windows the bridge was actually connected. Anything said outside them is missing.
- `docs/status/` renders the file. `docs/status/?demo` shows a bundled fixture (`docs/status/sample.json`, built by `status.py` from a synthetic log).
- A real `status.json` is a public artifact. Even with zero tasks it reveals when the relay was online. **Don't commit one without the repo owner's OK.** This repo currently ships the fixture only.

## Quick start — Muse (browser)

No build step. Open the static client:

- Double-click / open `web/muse/index.html` in a browser, **or**
- Serve the folder: `python3 -m http.server 8080 --directory web/muse` then visit `http://localhost:8080/`

Join with the same channel as Chief (default in the form: `fuse-grok-6f4e970cd8`, nick `Muse`). hack.chat WSS works from `file://` and any static HTTPS host. The same client is published at `docs/muse/`. Keep `web/muse/` and `docs/muse/` identical.

<!-- MUSE-SIDE SECTION: written by Fuse (usage, reconnect behavior, rejected joins). -->
Reconnect in brief: if the socket drops, the client retries with exponential backoff (1 s doubling to a 30 s cap, ±20% jitter). It retries immediately when the tab becomes visible again or the browser comes back online. If hack.chat rejects the join, the client never sits "connected" outside the channel. A nick-taken or rate-limit rejection is retried: indefinitely after a successful join, since the taken nick is usually your own stale session, but at most 3 times on the very first join. Any other rejection shows "join rejected" and stops.

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
| `docs/security.md` | Trust model: trips, pass handling, what needs a human |
| `docs/index.html` | Landing page (GitHub Pages root) |
| `docs/muse/` | Published copy of the Muse client |
| `docs/status/` | Status panel (renders `docs/status.json`; `?demo` for the fixture) |
| `tools/status.py` | Fail-closed status generator (+ `test_status.py`) |
| `config.example.json` | Config template (see Configuration) |
| `CHANGELOG.md` | What changed, by PR |
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
- Trust tripcodes, not nicks. Keep a human in the loop for merges, deploys, posts and spending.

Full trust model: [docs/security.md](docs/security.md).

## License

MIT
