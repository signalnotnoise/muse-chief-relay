# ChatBridge v2 client (draft)

Draft only. The live bridge still speaks v1. `protocol_v2` defaults to **false** in config. Leaving it off is the shipped path. This does not enable relay dual-write, change Pages or the relay process, or cut over chief, dot, or Fuse.

The source of truth is [chatbridge-v2-contract.md](chatbridge-v2-contract.md) **§11**. Where §§2–9 disagree with §11, this client follows §11 and records the disagreement below. `docs/protocol.md` and `VoizleWire` still describe v1 on purpose.

## What is implemented

Opt-in (`"protocol_v2": true` on a voizle URL):

- Hello `v: 1` with `durable: true` and `durableVersion: 2` pins **this** connection to `{"v":2,"type":"join",...}`. Other connections stay v1.
- A designed hello that only has `versions: [2,1]` is recognized and **not** half-negotiated. The join stays v1.
- If both advertisements are present, the join still pins v2, and the frames are the deployed ones.
- Inbox owner secret comes from the env var named by `inbox_owner_env` and is sent as `pass`. It is redacted in logs. The public trip is not ownership. A `binding` token is redacted and ignored.
- Outbound lines are copied into `{base}/durable-v2-outbound.jsonl` and fsynced as `queued` before send, then as `sent` before SendAsync. That `sent` record is the intent to write, not a proven socket write. The pump reads that queue, not the outbox tail. The first opt-in snapshots the offset after the last complete outbox line, so older lines are not burst and a half-written tail is imported once its newline arrives. Later launches keep that offset.
- `client_msg_id` is the local row id. It is **not** sent on the chat frame unless `v2_send_dedup` is true. That flag defaults to **false**. Turning it on reuses the same id on a rate-limit retry. It does **not** make delivery exactly-once. §11 still has no server idempotency key, so a retry after a missed `accepted` can store a second message.
- Outbound states are not interchangeable:
  - `queued` — durable, not yet handed to the socket.
  - `sent` — fsynced before SendAsync. It records the intent to write, not a proven socket write. A crash between that record and the send leaves the row `sent` without proof the bytes left. This is not server acceptance. With no correlated receipt the pump **holds** and does not resend.
  - `accepted` — a correlated accept receipt only (`messageId` / `ingressId` stored when present). A frame that names `client_msg_id` completes that row only. A frame with no id completes the single in-flight `sent` row, or a single `uncertain` row when nothing is `sent`. Two in-flight sends and no id are not guessed.
  - `echo_observed` — own nick and exact text were seen. This is **never** written as `accepted`. With `v2_echo_compat` left at its default **false**, the echo is recorded and the row stays `sent` (the pump still holds). Nick plus text does not clear the head.
  - `rate_limited` — the server rejected a chat this connection actually submitted, and that row is still `sent`. The row is kept. It is retried after the server delay (`retryAfterMs`, `retry_after_ms`, or `retryAfter` in seconds), clamped to at most 30 seconds before it becomes a wait. The same `client_msg_id` is reused. The default cap is 5 send attempts. This does not mark the row `sent` forever and does not drop it. A `rate_limited` JOIN, or any other frame that does not match that current-session send, is a control error and does not move a persisted `sent` row.
  - `uncertain` — the rate-limit budget is exhausted, or the receipt was lost. The row is not dropped and not requeued. The pump holds. A later correlated `accepted` can still complete it. A `sent` row that never receives a receipt is the same kind of hold: it is not resent on reconnect or after a crash.
- `v2_echo_compat` (default **false**) is the only echo release. It is session-tied and allows one outstanding attempt: the chat nick equals the bridge nick, the text equals that one in-flight row, no other open row has the same text, and the chat is not a welcome replay. The row becomes `echo_observed` (not `accepted`) and the socket is fenced so an unnamed `accepted`, or one that names any other id, cannot complete the next chat. A later receipt that names that `echo_observed` row can still complete it; the fence stays up until reconnect. The next row is sent only after reconnect. A replay, identical text, a nick mismatch, or a row this session did not send does not release the head. The ambiguity limit is that limit: outside it, the client would rather hold than attach the echo to a row. An in-flight `sent`, `rate_limited`, or `uncertain` row stays ahead of an earlier row that was requeued.
- The operator command is `chat-bridge reconcile --id <client_msg_id> drop|requeue` for a `sent`, `uncertain`, `rate_limited`, or `echo_observed` row. `drop` does not send, and it fences that connection: the deployed `accepted` frame has no client id, so a late `accepted` is ignored and does not complete a later chat. The same fence ignores a ledger `accepted` line on load and catch-up. The next chat is sent only after the socket reconnects. `requeue` may duplicate on the server and is never implied. The command does not connect and does not turn `protocol_v2` on. A running bridge applies the new ledger line on its next outbound step. `status` prints uncertain, rate-limited, and echo-observed ids and not the held text.
- Inbound `delivery` is fsynced `seen` with a payload (`text`, and `nick` / `trip` / `ts` / `id` when present). Flat fields are used. The same fields are read from a nested `message`, `msg`, `chat`, or `payload` object when that object carries `text`. Owner fields are not copied. A delivery with no `text` stays `seen` and is not acked.
- Before the ack frame, the payload is fsynced into the wake queues and the ledger records `handed_off`. The queues are `{base}/agents/<id>/inbox.jsonl` (what `inbox due` reads) and one chat line on the room `inbox.jsonl` with `v2_handoff` set to the delivery id (what `mention_hook`, `watch`, and `hook` already read). `mention_hook` applies the same `@mention`, open-question, and other-recipient filters to that line as to a v1 chat. A handoff does not wake every consumer. A raw `delivery` frame in the log is not a wake. The agent-inbox source id is the v1 dedup key (`srv:<message id>`, or the content hash when there is no message id). The handoff chat uses that message id, not the lease id, so the same logical message is not stored twice and is not acked twice. The v1 chat frame itself is not a wire ack.
- The agent id is the configured agent that owns the bridge nick, otherwise the only configured agent, otherwise the nick when the roster is empty and the nick is a safe id. v1 (`protocol_v2` off) still does not create `{base}/agents/`.
- The ack frame is returned only after `handed_off`. Ack is `{"v":2,"type":"ack","deliveryId","leaseGeneration"}`. If the enqueue throws or returns false, the row stays `seen` (no `handed_off`, no `ack_pending`, no wire ack). A later delivery of that generation can enqueue and then ack. `handed_off` or `ack_pending` on reopen replays the ack and does not enqueue again.
- Completion is `ack_result` with `state: "processed"` or `idempotent: true` for that delivery and generation. When the frame includes `deliveryId`, only that row is settled, and `leaseGeneration` must match when it is present. A result that names a different id or generation does not complete the in-flight ack. A result with no id still settles the single in-flight ack (the §11 fixtures omit the id). `lease_fenced` and `lease_expired` use the same correlation and leave the row unacked. A seen or fenced row may be re-leased. The same generation, once processed, is deduped. A higher generation is a new lease.
- After `welcome` with `inboxAuth: true`, the client sends `{"v":2,"type":"pull"}` and repeats while `pull_result.queued > 0` (cap 100 per connection).
- Restart reloads both jsonl files. `queued` can still send. `sent` and `uncertain` hold until `reconcile` or a correlated `accepted`. A due `rate_limited` row retries with the same id. `echo_observed` does not block and is not `accepted`. `ack_pending` and `handed_off` replay one ack and do not enqueue again. `seen` from an earlier process waits for a new delivery.

v1 (`protocol_v2` false or absent) does not create those files and still joins with `"v": 1`, including when the hello carries `durable`.

## Conflicts with §§2–9 (prefer §11)

| Dot ask / §§2–9 | This client |
|---|---|
| Pin v2 from `versions: [2,1]` | Recognized. Not spoken unless the hello also has deployed `durableVersion: 2`. A designed-only hello stays v1. |
| Opaque `binding` token owns the inbox | Not sent or stored. Owner secret at join (`pass`). Trips stay evidence. `inbox_held` backs off the session. |
| Wire `client_msg_id` dedups sends | Local id always. The chat frame omits it unless `v2_send_dedup` is on, and that flag does not make delivery exactly-once. A missed `accepted` is not resent. An explicit `rate_limited` rejection is retried with the same id. `requeue` is explicit and may duplicate. |
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
2. `protocol_v2` true plus the deployed hello: join `v` is 2, dialect is deployed, no `bind` / `resume` on the wire. `client_msg_id` stays off the chat frame unless `v2_send_dedup` is true.
3. Designed-only `versions: [2,1]` with the flag on: join stays v1.
4. Both advertisements: join `v` is 2 and the chat/ack/pull frames match the deployed fixtures.
5. Outbox import fsyncs `queued` before `sent`. `sent` is on disk before the chat frame is returned. That record is possibly-written intent, not proof the socket write happened.
6. Reopen after `sent` and no `accepted`: the pump holds and does not emit another chat. The line appended behind it is still queued (not skipped by a fresh EOF). A crash reload does the same. An own-nick echo does not accept the row and, with `v2_echo_compat` false, does not release the hold.
7. `accepted` clears the hold. A receipt that names `client_msg_id` completes that row only, including when another row has the same text. An unnamed receipt does not choose between two in-flight sends. `reconcile --id <id> drop` also clears a held row without a send, and a late `accepted` does not mark a different chat accepted. A ledger `accepted` line replayed while that fence is up is ignored. The next chat waits for a new connection. `requeue` sends that row again and may duplicate. Neither happens on its own.
8. `rate_limited` for a chat this session submitted keeps the row, waits the capped server delay, and retries the same `client_msg_id`. A huge `retryAfterMs` or `retryAfter` does not throw; the wait is at most 30 seconds. It does not leave the row `sent` and it does not drop it. A `rate_limited` frame with no current-session send, including a rate-limited JOIN after reconnect, does not re-arm a persisted `sent` row. After the correlated `accepted`, the next queued row sends. When the attempt budget is exhausted the row is `uncertain`: not dropped, not requeued, and the rows behind it stay queued.
9. `v2_echo_compat` false: replay, out-of-order text, and identical text do not accept or release. `v2_echo_compat` true: one unambiguous echo of the send this session handed off parks that row as `echo_observed`, fences the socket, and the next row sends only after reconnect. An unnamed `accepted` and a receipt for a different id stay fenced. A receipt that names that `echo_observed` row completes it and still waits for reconnect before the next chat. Requeue of that earlier row does not pass a later send that is still in flight. A reloaded process does not treat a welcome-time or previous-session echo as that release.
10. A delivery with `text` fsyncs `seen` (payload included), then the agent inbox and the `v2_handoff` room line, then `handed_off`, then `ack_pending`, before the ack frame is returned.
11. If that enqueue fails, or the delivery has no `text`, there is no `ack_pending` and no ack frame. Reopen does not ack a `seen` row. A later delivery can enqueue and then ack.
12. Same generation after `processed` or `idempotent` is deduped. The same generation merely seen or `lease_fenced` is not. A higher generation is acked again.
13. Reopen keeps `ack_pending` and replays one ack without enqueueing again. Processed rows stay processed.
14. An `ack_result` that names a different `deliveryId` or `leaseGeneration` does not complete the in-flight row. A matching id does. A fixture result with no id still completes the single in-flight ack.
15. Owner secret is `pass` on the join and `<redacted>` in the inbox log. A trip on the delivery does not grant or deny the ack.
16. A `bound` frame's `binding` value is redacted and does not become local ownership.
17. A `v2_handoff` line is selected like a v1 chat: `@mention` and an open question wake; a line aimed at another recipient, and a line that is neither, do not. A raw `delivery` frame does not wake.
18. One room message that arrives as a v1 chat and as a v2 delivery shares the dedup key. The agent inbox keeps one row. The wire ack is the one delivery ack. The chat frame adds no second ack.
