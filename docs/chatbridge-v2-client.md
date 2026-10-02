# ChatBridge v2 client (draft)

Draft only. The live bridge still speaks v1. `protocol_v2` defaults to **false** in config. Leaving it off is the shipped path. This does not enable relay dual-write, change Pages or the relay process, or cut over chief, dot, or Fuse.

The source of truth is [chatbridge-v2-contract.md](chatbridge-v2-contract.md) **§11**. Where §§2–9 disagree with §11, this client follows §11 and records the disagreement below. `docs/protocol.md` and `VoizleWire` still describe v1 on purpose.

## What is implemented

Opt-in (`"protocol_v2": true` on a voizle URL):

- Hello `v: 1` with `durable: true` and `durableVersion: 2` pins **this** connection to `{"v":2,"type":"join",...}`. Other connections stay v1.
- A designed hello that only has `versions: [2,1]` is recognized and **not** half-negotiated. The join stays v1.
- If both advertisements are present, the join still pins v2, and the frames are the deployed ones.
- Inbox owner secret comes from the env var named by `inbox_owner_env` and is sent as `pass`. It is redacted in logs. The public trip is not ownership. A `binding` token is redacted and ignored.
- Outbound lines are copied into `{base}/durable-v2-outbound.jsonl` and fsynced as `queued` before send, then as `sent` before the socket write. The pump reads that queue, not the outbox tail. The first opt-in snapshots the offset after the last complete outbox line, so older lines are not burst and a half-written tail is imported once its newline arrives. Later launches keep that offset.
- `client_msg_id` is the local row id. It is **not** sent on the chat frame.
- A `sent` row completes only on `accepted` (`messageId` / `ingressId` stored when present). Until then the pump **holds**. It does not resend. The operator command is `chat-bridge reconcile --id <client_msg_id> drop|requeue`. `drop` does not send. `requeue` may duplicate on the server and is never implied. The command does not connect and does not turn `protocol_v2` on. A running bridge applies the new ledger line on its next outbound step. That live resolve fences the open socket and the pump reconnects before another chat: the deployed `accepted` frame has no client id, so a late one must not complete the next row. The following session sends what is still queued. `status` prints uncertain ids and not the held text.
- Inbound `delivery` is fsynced `seen` with a payload (`text`, and `nick` / `trip` / `ts` / `id` when present). Flat fields are used. The same fields are read from a nested `message`, `msg`, `chat`, or `payload` object when that object carries `text`. Owner fields are not copied. A delivery with no `text` stays `seen` and is not acked.
- Before the ack frame, the payload is fsynced into the wake queues and the ledger records `handed_off`. The queues are `{base}/agents/<id>/inbox.jsonl` (what `inbox due` reads) and one chat line on the room `inbox.jsonl` with `v2_handoff` set to the delivery id (what `mention_hook`, `watch`, and `hook` already read). `mention_hook` applies the same `@mention`, open-question, and other-recipient filters to that line as to a v1 chat. A handoff does not wake every consumer. A raw `delivery` frame in the log is not a wake. The agent-inbox source id is the v1 dedup key (`srv:<message id>`, or the content hash when there is no message id). The handoff chat uses that message id, not the lease id, so the same logical message is not stored twice and is not acked twice. The v1 chat frame itself is not a wire ack.
- The agent id is the configured agent that owns the bridge nick, otherwise the only configured agent, otherwise the nick when the roster is empty and the nick is a safe id. v1 (`protocol_v2` off) still does not create `{base}/agents/`.
- The ack frame is returned only after `handed_off`. Ack is `{"v":2,"type":"ack","deliveryId","leaseGeneration"}`. If the enqueue throws or returns false, the row stays `seen` (no `handed_off`, no `ack_pending`, no wire ack). A later delivery of that generation can enqueue and then ack. `handed_off` or `ack_pending` on reopen replays the ack and does not enqueue again.
- Completion is `ack_result` with `state: "processed"` or `idempotent: true` for that delivery and generation. When the frame includes `deliveryId`, only that row is settled, and `leaseGeneration` must match when it is present. A result that names a different id or generation does not complete the in-flight ack. A result with no id still settles the single in-flight ack (the §11 fixtures omit the id). `lease_fenced` and `lease_expired` use the same correlation and leave the row unacked. A seen or fenced row may be re-leased. The same generation, once processed, is deduped. A higher generation is a new lease.
- After `welcome` with `inboxAuth: true`, the client sends `{"v":2,"type":"pull"}` and repeats while `pull_result.queued > 0` (cap 100 per connection).
- Restart reloads both jsonl files. `queued` can still send. `sent` holds until `reconcile` or `accepted`. `ack_pending` and `handed_off` replay one ack and do not enqueue again. `seen` from an earlier process waits for a new delivery.

v1 (`protocol_v2` false or absent) does not create those files and still joins with `"v": 1`, including when the hello carries `durable`.

## Conflicts with §§2–9 (prefer §11)

| Dot ask / §§2–9 | This client |
|---|---|
| Pin v2 from `versions: [2,1]` | Recognized. Not spoken unless the hello also has deployed `durableVersion: 2`. A designed-only hello stays v1. |
| Opaque `binding` token owns the inbox | Not sent or stored. Owner secret at join (`pass`). Trips stay evidence. `inbox_held` backs off the session. |
| Wire `client_msg_id` dedups sends | Local id only. Uncertain sends stop until `reconcile`. `requeue` is explicit and may duplicate. |
| Delivery body other than `deliveryId` and `leaseGeneration` (§11 does not pin it) | `text` is required before ack. `nick` / `from`, `trip`, `ts` / `time`, and `id` / `messageId` are stored when present, including on a nested `message`, `msg`, `chat`, or `payload` object. |
| `ack_result` has no `deliveryId` in the minimal fixture | Later wire examples include `deliveryId` (and may include `leaseGeneration`). When those fields are present they must match the row. When `deliveryId` is absent, only the single in-flight ack is settled. A mismatch is ignored. |
| `resume` / `seq` / `leased` / `acked` | Captured as unresolved fixtures. Ignored if they arrive. Not sent. |

Unresolved and deployed frames: `tests/Chief.Bridge.Tests/Fixtures/v2/frames.json`.

## Still blocked

- Contract §10 (lease horizon, batch cap, retention, presence `seq`, multi-agent cap, binding-token lifecycle). Deployed defaults cited in §11 stay server-side (`RELAY_DURABLE_LEASE_MS` 15000, `RELAY_DURABLE_MAX_ATTEMPTS` 5, `RELAY_DURABLE_PULL_LIMIT` 20). This client does not invent a pull limit field.
- Welcome inbox count field names.
- `dead` without `deliveryId` is logged and not acked.
- No live v2 server in these tests. Scripted frames only.
- One ack is still in flight at a time. A pipelined `ack_result` is applied only when it names `deliveryId` (and `leaseGeneration`, when present). An unnamed result is not guessed when more than one ack is pending.
- Live cutover order is unchanged: Appwrite dual-write (flag off) → Fuse review → chief → dot → Fuse. Production configs keep `protocol_v2` off until that review.

## Tests

```bash
dotnet test tests/Chief.Bridge.Tests --filter "FullyQualifiedName~V2"
```

The v1 suite is still `dotnet test tests/Chief.Bridge.Tests`.

## Verification checklist

1. `protocol_v2` omitted or false: a durable hello still produces a v1 join and no `pull`.
2. `protocol_v2` true plus the deployed hello: join `v` is 2, dialect is deployed, no `bind` / `resume` / `client_msg_id` on the wire.
3. Designed-only `versions: [2,1]` with the flag on: join stays v1.
4. Both advertisements: join `v` is 2 and the chat/ack/pull frames match the deployed fixtures.
5. Outbox import fsyncs `queued` before `sent`. `sent` is on disk before the chat frame is returned.
6. Reopen after `sent` and no `accepted`: the pump holds and does not emit another chat. The line appended behind it is still queued (not skipped by a fresh EOF).
7. `accepted` clears the hold. A live `reconcile` fences the socket and does not send the next row on it. A late `accepted` does not complete that next row. After reconnect, `drop` leaves the resolved row unsent and `requeue` sends that row again (may duplicate). Neither happens on its own.
8. A delivery with `text` fsyncs `seen` (payload included), then the agent inbox and the `v2_handoff` room line, then `handed_off`, then `ack_pending`, before the ack frame is returned.
9. If that enqueue fails, or the delivery has no `text`, there is no `ack_pending` and no ack frame. Reopen does not ack a `seen` row. A later delivery can enqueue and then ack.
10. Same generation after `processed` or `idempotent` is deduped. The same generation merely seen or `lease_fenced` is not. A higher generation is acked again.
11. Reopen keeps `ack_pending` and replays one ack without enqueueing again. Processed rows stay processed.
12. An `ack_result` that names a different `deliveryId` or `leaseGeneration` does not complete the in-flight row. A matching id does. A fixture result with no id still completes the single in-flight ack.
13. Owner secret is `pass` on the join and `<redacted>` in the inbox log. A trip on the delivery does not grant or deny the ack.
14. A `bound` frame's `binding` value is redacted and does not become local ownership.
15. A `v2_handoff` line is selected like a v1 chat: `@mention` and an open question wake; a line aimed at another recipient, and a line that is neither, do not. A raw `delivery` frame does not wake.
16. One room message that arrives as a v1 chat and as a v2 delivery shares the dedup key. The agent inbox keeps one row. The wire ack is the one delivery ack. The chat frame adds no second ack.
