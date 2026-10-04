# Optional remote MCP Events backend

`mcp_events.py` is the remote integration behind `./go`. It requires the configured
single authenticated principal and HTTPS/OAuth gateway. It is separate from the
preferred local bridge launcher and does not implement independent local Dot wake.
These guarantees are covered by offline tests, not a deployed end-to-end callback
or model-turn verification.

## Locking and durable delivery

The process gate protects the shared SQLite connection and state transitions.
Neither callback-address DNS validation nor challenge/event HTTP runs under that
gate or inside a database transaction. A callback can synchronously call
`dot_complete` on another request thread without waiting for its own HTTP response.
Status, cancellation, and other requests can also proceed while HTTP is in flight.
Local inbox polling, configuration reads, and reply-file publication still use
short serialized state operations; this is not a fully asynchronous storage API.

Before event HTTP starts, the backend commits `delivering`, increments the attempt
count, stores a unique claim token, and reserves the retry time. Concurrent ticks
cannot claim that same delivery. Finalization checks both the claim token and
attempt number, and only changes a still-`delivering` row. Completion,
cancellation, expiry, or authorization revocation during HTTP wins over a late
callback result. A request already sent cannot be recalled by unsubscribe.

A `2xx` callback response means `accepted`, not model completion. Only an authorized
`dot_complete` records completion. `410` rejects the delivery and deactivates its
subscription; `413` rejects only the delivery. Other unsuccessful results retry
with delays of 1, 2, 4, 8, 16, 32, and 64 seconds, with at most eight total attempts.
A late `410` from a pre-refresh attempt cannot deactivate a newer verified
subscription. Rejected, exhausted, cancelled, expired, revoked, and completed rows
are not automatically redelivered when a subscription is renewed.

The command-line server holds `runtime/mcp-events.lock` for its lifetime. Run one
backend owner per runtime; the Python `Events` class is not a multiprocess lock.
For an authorized update, stop that owner before starting the new code against
its preserved state. Schema changes add claim/generation columns and a pending
verification table without replacing existing subscriptions or delivery data.
On restart, unfinished `delivering` rows return to `pending` only if the attempt
budget remains. The consumed attempt count and reserved retry time survive;
the eighth interrupted attempt becomes `exhausted`. There is no timer-based
reclaim while another callback may still be running. An invalid configuration
reload during a callback releases the claim for a bounded retry after the
configuration is repaired, without authorizing further network work while invalid.

An interrupted callback may have been received even though its response was not
recorded. Retried callbacks keep the same `webhook-id` (`evt_` plus event ID) and
payload; consumers must deduplicate. This is not exactly-once delivery, and the
backend cannot guarantee completion after an accepted callback if the consumer
never submits `dot_complete`.

## Subscription verification races

A subscribe request reserves a durable verification token before external I/O.
It activates or refreshes the subscription only after the echoed challenge passes
and its token, current authorization scope, and granted expiry still match.
For concurrent verification of the same subscription, the newest started request
wins. An older response fails with `subscription_superseded`, even if the newer
request later fails. Failed verification does not replace a previously verified
subscription. Unsubscribe also invalidates a pending verification, including one
for a subscription that does not yet exist. A challenge that outlives its granted
TTL fails with `subscription_expired`.

A restart discards unfinished verification tokens rather than activating an
unverified subscription. The caller must subscribe again. Renewal retains the
original event start time only while the prior subscription is still active and
unexpired; otherwise the newly verified lifetime starts at verification finish.
Signing-key rotation retains the previous key for a five-minute dual-signing
window. Callback address checks require public HTTPS addresses, pin the validated
connection address with the original TLS hostname, repeat DNS checks per HTTP
connection, and never follow redirects.

## Completion and reply export

Completion rechecks the configured principal, room, approved recipient, and active
unexpired subscription. It changes only authorized delivered/completed rows, never
a cancelled, expired, or pending delivery for another subscription. Repeating the
same completion is idempotent; a different reply for an already completed event
is rejected, including across subscriptions. Completing with no reply is also
final and cannot later be changed into a reply.

Completed replies are exported through `durable_reply.publish` as immutable
requests keyed by event ID. Publication can be retried after a crash between
file creation and the SQLite `exported` flag without creating another request.
Export rechecks the current principal, room, and approved recipient. Expiry or
unsubscribe does not retract a completion that was already durably recorded or a
reply that was already published. Durable export is still only queueing for the
bridge; it does not prove room delivery, owner identity, or exactly-once sending.

## Offline regression checks

```sh
python3 -m unittest discover -s bots/dot -p 'test_*.py'
python3 -m unittest discover -s tools -p 'test_status.py'
bash -n bots/dot/env.sh bots/dot/launch-bridge.sh bots/dot/run.sh
```

`test_mcp_events.py` uses temporary synthetic configuration/state, mocked DNS,
mocked callback HTTP, and coordinated threads. It covers synchronous completion,
claim durability, concurrent ticks, verification races, unsubscribe/expiry and
scope changes, retry/crash limits, signing rotation, callback-address checks,
additive schema migration, and idempotent reply export. It does not contact a real
room, use deployment credentials, invoke a model, or validate the remote gateway.
