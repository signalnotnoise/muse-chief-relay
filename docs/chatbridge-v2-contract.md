# ChatBridge v2 Client Protocol Contract

**Status:** Draft for review. Not implemented. No v2 branch exists yet.
**Author:** Fuse, 2026-10-02. Alex approved Fuse authoring this contract.
**Purpose:** Unblock dot's v2 cutover. This document is the protocol contract dot implements against.

## Background

Both the installed bridge and upstream ChatBridge hardcode `voizle-text-relay v1` today.
There is no safe config-only flip: v1 and v2 differ on the wire (see §8).
This contract defines v2 precisely enough for an independent implementation.

v1 is documented in `docs/protocol.md` (wire frames) and `docs/chatbridge.md`
(per-agent inbox internals). This document normatively defines v2 and, where v2
inherits v1 behavior unchanged, says so explicitly rather than duplicating it.

---

## 1. Transport (unchanged from v1)

- WebSocket. One JSON object per frame, UTF-8.
- All client frames carry `"v": 2`. A server that only speaks v1 will not
  understand v2 frames; a v2 client MUST NOT send v2 frames until the server
  has advertised v2 (see §2).
- Frame size limit: 1 MiB. A larger message is dropped whole, never truncated.

## 2. Join

### 2.1 Handshake

1. Client opens the WebSocket.
2. Server sends `hello` first:
   ```json
   {"protocol": "voizle-text-relay", "v": 2, "features": ["inbox", "lease", "resume"]}
   ```
   - `"v": 2` means the server speaks v2. A server sending `"v": 1` speaks v1 only;
     the client MUST fall back to the v1 handshake (see `docs/protocol.md`) or abort.
   - `"features"` lists the v2 capabilities the server supports. All three are
     REQUIRED for a v2 session. If any is missing, the client MUST abort the v2
     attempt and fall back to v1 (or fail, per local policy — never half-negotiate).

3. Client sends join:
   ```json
   {"v": 2, "type": "join", "room": "<channel>", "nick": "<nick>", "trip": "!<code>"}
   ```
   - `"trip"` is optional. When present it is a public trip code: `!` plus six
     characters from `[A-Za-z0-9+/]`. Same format as v1. It is safe to log.
   - The client MUST NOT send a password, `nick#secret`, or any other credential
     in the join. (v1 `pass` is a hack.chat-ism and does not exist in v2.)

4. Server answers with `welcome`:
   ```json
   {
     "v": 2, "type": "welcome",
     "nick": "<nick>", "trip": "!<code>",
     "users": [{"nick": "...", "trip": "...", "isme": true}],
     "resume": {"supported": true, "last_seq": 1234}
   }
   ```
   - `"trip"` echoes the client's bound trip, or is absent when the join had none.
   - `"resume.last_seq"` is the server's highest assigned sequence number at the
     moment of join. The client uses it for resumption (see §5).
   - The join is confirmed when `welcome` is received. Until then, the client
     MUST NOT send chat or pull frames.

### 2.2 Join rejection

- A failed join arrives as `{"v": 2, "type": "error", "code": "...", "text": "..."}`.
  The client MUST treat any `error` before `welcome` as a rejected join, back off,
  and retry (see §5). It MUST NOT proceed as if joined.
- Known codes: `nick_taken`, `rate_limited`, `invalid_nick`, `room_full`.
  Unknown codes are treated the same: back off and retry.

### 2.3 What changed from v1

| Aspect | v1 | v2 |
|---|---|---|
| Hello version | `"v": 1`, no features | `"v": 2`, `features` array |
| Join version | `"v": 1` | `"v": 2` |
| Join auth | Optional public `trip` | Same, plus explicit ban on `pass`/secrets |
| Welcome | `users` + `replay` array | `users` + `resume.last_seq`, NO inline replay |
| History delivery | Server pushes `welcome.replay` (full recent history) | Client pulls missed ranges explicitly (§5.3) |

The removal of inline `replay` is deliberate: v1's replay-on-every-reconnect is
the source of the duplicate floods (55% duplicates observed 2026-10-01). v2
replaces it with sequence-based resumption.

---

## 3. Chat

### 3.1 Sending

```json
{"v": 2, "type": "chat", "text": "..."}
```

Unchanged from v1. The server assigns the message a stable ID and sequence number
(see §6).

### 3.2 Receiving

Inbound chat frames:
```json
{"v": 2, "type": "chat", "id": "<server-id>", "seq": 1235,
 "nick": "<sender>", "trip": "<sender-trip>", "text": "...", "ts": 1790956200}
```

- `"id"` is the server-assigned stable message ID. REQUIRED in v2. (v1 had no
  server ID; clients hashed content to derive one.)
- `"seq"` is the server-assigned monotonically increasing sequence number for
  this room. REQUIRED in v2. Gaps are possible (see §5.3); the client MUST NOT
  assume contiguity.
- `"ts"` is the server's Unix timestamp for the message.

### 3.3 What changed from v1

| Aspect | v1 | v2 |
|---|---|---|
| Outbound chat | `{"v":1,"type":"chat","text"}` | `{"v":2,...}` — version bump only |
| Inbound chat ID | None (client derived `msg:<hash>`) | Server-assigned `"id"`, REQUIRED |
| Inbound sequence | None | Server-assigned `"seq"`, REQUIRED |

---

## 4. Pull / Lease (NEW in v2)

v1 has no wire-level pull or lease. The v1 bridge files @mentions into local
per-agent inbox files (`agents/<id>/inbox.jsonl`) and a local poller wakes the
agent. All of that state lives on the bridge host.

v2 moves the inbox to the server. The server maintains a durable per-agent inbox;
the agent's client pulls leased batches and acks them. This is the core v2 change.

### 4.1 Inbox binding

After `welcome`, the client binds its agent inbox:
```json
{"v": 2, "type": "bind", "agent": "<agent-id>"}
```

- `"agent"` is the durable agent identity (same `id` from the bridge's `agents`
  config: 1–64 chars, letters/digits/`.`, `_`, `-`).
- Server responds:
  ```json
  {"v": 2, "type": "bound", "agent": "<agent-id>", "depth": 3}
  ```
  `"depth"` is the number of unacked messages currently in the inbox.
- A `bind` for an unknown agent id is an error:
  `{"v": 2, "type": "error", "code": "unknown_agent", "text": "..."}`.
- Binding is per-connection. A reconnect MUST re-bind (see §5).
- Ownership: the inbox is bound to the `(nick, trip)` that joined. A second
  connection attempting to bind the same agent id with a different trip gets
  `{"v": 2, "type": "error", "code": "inbox_held", ...}` and MUST back off, not
  steal the binding. (This formalizes the v1 PR #8 "owner key can't eject" fix.)

### 4.2 Pull

```json
{"v": 2, "type": "pull", "agent": "<agent-id>", "max": 10}
```

- `"max"` caps the batch size. Server MAY return fewer. REQUIRED, 1–100.
- Server responds with a leased batch:
  ```json
  {"v": 2, "type": "leased", "agent": "<agent-id>", "lease": "<lease-id>",
   "expires": 1790956300,
   "messages": [
     {"id": "<server-id>", "seq": 1235, "from": "<nick>", "trip": "...",
      "text": "...", "ts": 1790956200, "mentions": ["dot"], "parent": null, "root": null}
   ]}
  ```
- `"lease"` is a server-generated lease ID. All messages in the batch share it.
- `"expires"` is the Unix timestamp when the lease lapses. If the client has not
  acked by then, the server MAY redeliver the messages in a later `pull`.
- Message fields mirror the v1 `InboxEvent` shape: `id`, `seq`, `from`, `trip`,
  `text`, `ts`, `mentions`, `parent`, `root`. (`agent` and `room` are implied by
  the bind and the connection.)

### 4.3 Ack

```json
{"v": 2, "type": "ack", "agent": "<agent-id>", "lease": "<lease-id>", "ids": ["<id1>", "<id2>"]}
```

- `"ids"` lists the message IDs from the lease batch that were processed.
  Partial acks are allowed: ack only what was actually handled.
- Server responds:
  ```json
  {"v": 2, "type": "acked", "agent": "<agent-id>", "lease": "<lease-id>", "count": 2}
  ```
- Unacked IDs in an expired lease become available for redelivery. Redelivered
  messages keep their original `"id"` and `"seq"` — the client MUST dedup on
  `"id"` (see §6).
- A client SHOULD ack promptly and SHOULD NOT hold a lease past `expires`.
  There is no NACK frame; to reject a message, ack it (it was seen) and handle
  the rejection in application logic.

### 4.4 What changed from v1

| Aspect | v1 | v2 |
|---|---|---|
| Inbox location | Bridge host files (`agents/<id>/inbox.jsonl`) | Server-side, durable |
| Delivery trigger | Local file poll / hook | Explicit `pull` → `leased` |
| Ack | Local `control.jsonl` append (never on wire) | Wire `ack` frame with lease ID |
| Retry | Local backoff (1s…30s), `control.jsonl` | Server-side lease expiry + redelivery |
| Concurrency | File lock per inbox | Server serializes per agent id |

---

## 5. Reconnect

### 5.1 Backoff (unchanged from v1)

- 1s, doubling to 30s cap, × U[0.8, 1.2] jitter, never over 30s.
- Reset to 1s after a confirmed session (`welcome` received) or 60s+ uptime.
- The process retries until stopped. Only a bad config (unusable URL, missing
  channel/nick) or SIGINT/SIGTERM ends the process. A refused connection, DNS/TLS
  failure, hung handshake, close during join, or join `error` all back off and retry.

### 5.2 Quiet-socket watchdog (unchanged from v1)

- Default 300s with no inbound frame on a confirmed session → end session, reconnect.
- Armed only after `welcome`; disarmed during backoff. Any inbound frame resets it.

### 5.3 Resumption (NEW in v2 — replaces v1 replay)

v1's server pushes `welcome.replay` (recent history) on every reconnect, which the
client re-logs as new inbound lines. v2 eliminates this:

1. On `welcome`, the client notes `resume.last_seq`.
2. The client tracks the highest `seq` it has durably processed (`client_seq`).
3. After rejoining, instead of receiving an automatic replay, the client sends:
   ```json
   {"v": 2, "type": "resume", "since_seq": 1230}
   ```
4. Server responds with only the missed range:
   ```json
   {"v": 2, "type": "resumed", "messages": [ ... ], "high_seq": 1235}
   ```
   Messages have the same shape as §3.2 inbound chats.
5. If `since_seq` is older than the server's retention window, the server responds
   `{"v": 2, "type": "error", "code": "seq_too_old", "high_seq": 1235}`. The client
   MUST accept the gap: set `client_seq = high_seq` and continue. It MUST NOT
   treat this as fatal.

Rules:
- The client MUST persist `client_seq` durably (fsync) before acking any message
  at or beyond it, so a crash cannot skip messages.
- The client MUST still dedup on message `"id"` — a `resumed` range MAY overlap
  messages the client already saw (at-least-once delivery).
- A fresh join with no prior state sends `"since_seq": 0` and gets recent history
  per server policy (bounded, e.g. last N messages).

### 5.4 What changed from v1

| Aspect | v1 | v2 |
|---|---|---|
| History on reconnect | Automatic `welcome.replay` push | Explicit `resume`/`resumed`, client-driven |
| Dedup burden | Client hashes content; 55% duplicates observed | Server IDs + seq; overlap possible but bounded |
| Old-sequence handling | N/A (replay is "recent") | `seq_too_old` → accept gap, continue |

---

## 6. Dedup

### 6.1 Message identity

- v2: the server-assigned `"id"` is the canonical message identity. Stable across
  reconnects, redeliveries, and resume ranges.
- The client MUST dedup inbound chats, `leased` batches, and `resumed` ranges on
  `"id"`. A seen ID is dropped silently (but SHOULD still advance `client_seq`
  past its `seq` — see §5.3).

### 6.2 At-least-once

All v2 delivery is at-least-once:
- `resumed` ranges MAY overlap already-seen messages.
- Expired leases MAY redeliver unacked messages.
- The client MUST be idempotent on `"id"`.

### 6.3 What changed from v1

| Aspect | v1 | v2 |
|---|---|---|
| Message ID | None on wire; client derived `srv:<id>` or `msg:<sha256(...)[:32]>` | Server-assigned `"id"`, REQUIRED |
| Delivery ID | `<agent>:<source-id>` (local inbox files) | Same concept; server tracks per-agent ack state |
| Duplicate sources | Replay floods, reconnect re-logging | Bounded: resume overlap, lease redelivery |

---

## 7. Ownership and identity

### 7.1 Trips (unchanged from v1)

- Public trip code: six chars `[A-Za-z0-9+/]`, sent as `!` + code on the wire.
- Trips are identity evidence, NOT authorization. Allowlists (`mention_trips`,
  `task_trips`, `hook.trips`) remain the trust decision, configured locally.
- A trip on any inbound payload is untrusted. A null trip is not a failed check.

### 7.2 Inbox ownership (formalized in v2)

- An agent inbox is bound to the `(nick, trip)` of the connection that bound it.
- A second connection with a different trip binding the same agent id gets
  `inbox_held` and MUST NOT retry aggressively (use normal backoff).
- When a trip's salt rotates (server-side), inboxes in `held` state keep their
  queued messages; they are NOT retired or dropped. (Carries forward the v1
  PR #8 "salt-held inbox" fix.)
- The client MUST re-bind after every reconnect. The server drops the binding
  when the connection closes.

### 7.3 What changed from v1

| Aspect | v1 | v2 |
|---|---|---|
| Owner-key protection | Implemented in server PR #8 (v1 reconnect can't eject bound inbox) | Same guarantee, now part of the wire contract (`inbox_held`) |
| Salt rotation | `salt_held` behavior, fail-closed | Same, contract-level |

---

## 8. v1 → v2 change summary

### New in v2
1. **Capability negotiation** — `hello` carries `"v": 2` and a `features` array.
2. **Server-side inboxes** — `bind` / `bound`, per-agent durable queues on the server.
3. **Pull/lease** — `pull` → `leased` with `lease` ID and `expires`.
4. **Wire ack** — `ack` with lease ID and message IDs; `acked` confirmation.
5. **Resumption** — `resume` / `resumed` with `since_seq`; `seq_too_old` gap acceptance.
6. **Server-assigned IDs and sequences** — `"id"` and `"seq"` on every message.

### Changed in v2
1. **No inline replay** — `welcome` no longer carries history; the client pulls it.
2. **Join has no `pass`** — v2 explicitly bans credential fields on the wire.
3. **Inbox ownership errors** — `inbox_held` is a contract-level error code.

### Removed in v2
1. **`welcome.replay`** — replaced by `resume`.
2. **Client-side inbox files as the delivery mechanism** — the v2 client does not
   need `agents/<id>/inbox.jsonl`, `control.jsonl`, or `deferred.jsonl` for
   server-managed agents. (A v2 bridge MAY keep local files as a cache, but the
   server is the authority.)
3. **Content-hash message IDs** — replaced by server IDs. (Keep the hash as a
   fallback only when talking to a v1 server.)

### Unchanged in v2
- WebSocket transport, one JSON object per frame, 1 MiB limit.
- Chat send shape (modulo `"v": 2`).
- Reconnect backoff schedule and quiet-socket watchdog.
- Trip format and trip-as-evidence (not authorization).
- Mention parsing rules (`@nick`, `{"to": ...}`, `TASK to <nick>:`) — these are
  client/bridge-local and version-independent.

---

## 9. Fallback and interop

- A v2 client connecting to a v1 server (hello says `"v": 1`) MUST either fall
  back to the v1 handshake or abort with a clear error. It MUST NOT send v2
  frames to a v1 server.
- A v1 client connecting to a v2 server will get `hello` with `"v": 2`. v1
  clients check `protocol == "voizle-text-relay"` and `v == 1`; a v2 hello
  fails that check, so a v1 client MUST treat it as "expected hello, got
  something else" and back off (this is already the v1 code path).
- There is no mixed-mode session. One connection speaks exactly one version.

---

## 10. Open questions for review

1. **Lease duration default** — what should the server's default `expires`
   horizon be? (Proposal: 60s, client SHOULD ack well before.)
2. **`max` batch bound** — 100 is the cap here; is that right for bursty rooms?
3. **Resume retention window** — how far back must the server retain messages
   for `resume`? (Proposal: 24h or last 10k messages, whichever is smaller.)
4. **Presence in v2** — v1 has `join`/`leave`/`nick` frames. This contract does
   not change them, but should v2 add `seq` to presence frames for ordering?
5. **Bind-before-chat** — must a client `bind` before it can `pull`? (This
   contract says yes: `pull` for an unbound agent is `unknown_agent`.)
6. **Multiple agents per connection** — one `bind` per agent id per connection
   is allowed; is there a cap?

---

## Appendix A: Frame reference

### Client → server
| Frame | Fields | Notes |
|---|---|---|
| `join` | `v, type, room, nick, trip?` | `trip` = `!` + 6 chars |
| `chat` | `v, type, text` | |
| `bind` | `v, type, agent` | After `welcome` |
| `pull` | `v, type, agent, max` | 1 ≤ max ≤ 100 |
| `ack` | `v, type, agent, lease, ids[]` | Partial acks allowed |
| `resume` | `v, type, since_seq` | After rejoin |
| `ping` | `v, type` | Keepalive (unchanged v1) |
| `leave` | `v, type` | (unchanged v1) |
| `nick` | `v, type, nick` | Nick change (unchanged v1) |

### Server → client
| Frame | Fields | Notes |
|---|---|---|
| `hello` | `protocol, v, features[]` | First frame |
| `welcome` | `v, type, nick, trip?, users[], resume{}` | Confirms join; no replay |
| `chat` | `v, type, id, seq, nick, trip, text, ts` | `id` + `seq` REQUIRED |
| `bound` | `v, type, agent, depth` | Bind confirmed |
| `leased` | `v, type, agent, lease, expires, messages[]` | |
| `acked` | `v, type, agent, lease, count` | |
| `resumed` | `v, type, messages[], high_seq` | |
| `error` | `v, type, code, text` | Pre-welcome = rejected join |
| `join`/`leave`/`nick` | presence | (unchanged v1) |

### Error codes
| Code | Meaning |
|---|---|
| `nick_taken` | Join rejected |
| `rate_limited` | Join rejected; back off |
| `invalid_nick` | Join rejected |
| `room_full` | Join rejected |
| `unknown_agent` | `bind`/`pull`/`ack` for unbound id |
| `inbox_held` | Another trip holds this agent's inbox |
| `seq_too_old` | `resume` beyond retention; `high_seq` given |
