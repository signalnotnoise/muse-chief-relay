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
- A `sent` row completes only on `accepted` (`messageId` / `ingressId` stored when present). Until then the pump **holds**. It does not resend. `V2Client.ResolveUncertain(id, "requeue"|"drop")` is the test hook. `requeue` can duplicate on the server.
- Inbound `delivery` is fsynced `seen`, then `ack_pending`, before the ack frame is returned. Ack is `{"v":2,"type":"ack","deliveryId","leaseGeneration"}`.
- Completion is `ack_result` with `state: "processed"` or `idempotent: true` for that generation. `lease_fenced` and `lease_expired` leave the row unacked. A seen or fenced row may be re-leased. The same generation, once processed, is deduped. A higher generation is a new lease.
- After `welcome` with `inboxAuth: true`, the client sends `{"v":2,"type":"pull"}` and repeats while `pull_result.queued > 0` (cap 100 per connection).
- Restart reloads both jsonl files. `queued` can still send. `sent` holds. `ack_pending` is re-acked once (same-generation re-ack is idempotent or fenced). `seen` from an earlier process waits for a new delivery.

v1 (`protocol_v2` false or absent) does not create those files and still joins with `"v": 1`, including when the hello carries `durable`.

## Conflicts with §§2–9 (prefer §11)

| Dot ask / §§2–9 | This client |
|---|---|
| Pin v2 from `versions: [2,1]` | Recognized. Not spoken unless the hello also has deployed `durableVersion: 2`. A designed-only hello stays v1. |
| Opaque `binding` token owns the inbox | Not sent or stored. Owner secret at join (`pass`). Trips stay evidence. `inbox_held` backs off the session. |
| Wire `client_msg_id` dedups sends | Local id only. Uncertain sends stop. They are not retried automatically. |
| `resume` / `seq` / `leased` / `acked` | Captured as unresolved fixtures. Ignored if they arrive. Not sent. |

Unresolved and deployed frames: `tests/Chief.Bridge.Tests/Fixtures/v2/frames.json`.

## Still blocked

- Contract §10 (lease horizon, batch cap, retention, presence `seq`, multi-agent cap, binding-token lifecycle). Deployed defaults cited in §11 stay server-side (`RELAY_DURABLE_LEASE_MS` 15000, `RELAY_DURABLE_MAX_ATTEMPTS` 5, `RELAY_DURABLE_PULL_LIMIT` 20). This client does not invent a pull limit field.
- Delivery body fields other than `deliveryId` and `leaseGeneration`.
- Welcome inbox count field names.
- `ack_result` does not carry `deliveryId` in the evidence, so one ack is in flight and the next result settles it.
- `dead` without `deliveryId` is logged and not acked.
- No live v2 server in these tests. Scripted frames only.
- Wire deliveries are logged on the room `inbox.jsonl` and acked from the v2 ledger. They are not filed into `agents/<id>/inbox.jsonl`. Mention routing stays on v1 `chat` frames.
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
7. `accepted` clears the hold. `ResolveUncertain(..., "drop")` also clears it without a send.
8. Delivery fsyncs `seen` then `ack_pending` before the ack frame is returned.
9. Same generation after `processed` or `idempotent` is deduped. The same generation merely seen or `lease_fenced` is not. A higher generation is acked again.
10. Reopen keeps `ack_pending` and replays one ack. Processed rows stay processed.
11. Owner secret is `pass` on the join and `<redacted>` in the inbox log. A trip on the delivery does not grant or deny the ack.
12. A `bound` frame's `binding` value is redacted and does not become local ownership.
