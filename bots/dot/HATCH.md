# Optional Hatch adapter

## Hatch-compatible hook adapter (prepared, not registered)

`hatch-mention-hook.sh` supports the shell contract used by Fuse: it sources the supplied `HATCH_HOOK_RUNTIME`, then calls either `wake(label, json)` or `silent(reason, json)`. Both runtime functions are expected to terminate immediately. No work is scheduled after either call.

`hatch_adapter.py` reuses the tested mention filter and durable event queue. Its state defaults to **runtime/hatch-mentions.sqlite**, separate from the current session listener's **runtime/mentions.sqlite**. Do not point both consumers at the same database. Do not register a second consumer for the same destination without deciding how to deduplicate notifications.

An owner of an actual Hatch runtime can provide these variables to the registered script:

```sh
HATCH_HOOK_RUNTIME=/path/to/runtime-provided-hook-functions.sh
DOT_RELAY_INBOX=/path/to/checkout/bots/dot/runtime/inbox.jsonl
DOT_HATCH_DATABASE=/path/to/local-state/hatch-mentions.sqlite
bash /path/to/checkout/bots/dot/hatch-mention-hook.sh
```

The adapter batches up to 20 new pending mentions into one wake payload, with `messages` containing `nick`, `trip`, `text`, and `ts`, plus stable `event_ids` and `untrusted: true`. The `trip` field is identity evidence from the room line. `untrusted: true` stays on the payload whether or not a trip is present. A trip here does not authorize the sender. This file is the room `inbox.jsonl` ChatBridge appends (`Chief.Bridge.dll` is the compatibility assembly `run.sh` starts). It is not `{base}/agents/dot/inbox.jsonl`. That per-agent queue exists only when `mentions.enabled` is true, and its adapter object is `chatbridge.inbox.wake` with the same rule: trip is untrusted evidence. First run initializes silently. It reads complete appended records in a single pass, commits the actual byte position consumed, handles rotation/truncation, and filters both `cmd: chat` and `type: chat`.

Set **HATCH_HOOK_DRY_RUN=1** to preview. Dry-run operates on a temporary SQLite snapshot, calls **silent**, and changes no persistent cursor, deduplication, queue, or attempt state. It never calls wake. No temporary mock runtime belongs in a production deployment.

### Delivery and crash semantics

Saving an offset **before** a wake call is not an at-least-once delivery guarantee. A process can crash after saving the offset but before the runtime accepts the wake. Saving it afterward is impossible when `wake()` exits immediately, and blindly retrying every pending event can cause repeated wake storms.

This adapter commits the event payload durably, then records a **wake attempt before the call**. An attempt is not labeled a confirmed delivery. The default behavior is **at most one automatic wake attempt per event**, with payloads retained for recovery. A crash or failure after that mark may prevent the actual model wake; the next poll does not automatically retry it.

A runtime worker that has actually accepted the event may acknowledge it using the same Hatch database:

```sh
python3 bots/dot/mention_hook.py --database /path/to/local-state/hatch-mentions.sqlite --ack EVENT_ID
```

If the runtime confirms it never accepted the event, an operator can explicitly allow another attempt:

```sh
python3 bots/dot/hatch_adapter.py --database /path/to/local-state/hatch-mentions.sqlite --retry EVENT_ID
```

If acceptance is uncertain, retry can duplicate a wake. The destination should deduplicate `event_ids`. A genuinely at-least-once integration would require an acknowledged/idempotent delivery contract from the hosting runtime; this script does not invent one.

### Deployment boundary

At verification time this environment exposed **no hooks.add registration API or command**, and **HATCH_HOOK_RUNTIME was unset**. The adapter was tested with local mock functions, not registered or deployed to Hatch. The working in-session task listener remains unchanged.

An actual Hatch runtime owner must register this script through that runtime's supported hook registration tool. Do not discover or reuse hidden credentials or private endpoints to simulate registration. If registered in Fuse's runtime, `wake()` starts a turn in **Fuse's agent space**. That does not automatically wake dot's separate conversation. Waking dot through that route requires a separately supported and authorized destination integration.

### Adapter verification — 2026-10-01

`python3 bots/dot/test_hatch_adapter.py`: **six passed**. Tests invoke the real shell adapter against isolated, immediately terminating mock `wake`/`silent` functions and cover:

- Silent historical initialization, case-insensitive exact mention and payload fields
- Dry-run without persistent writes or a wake call
- Malformed records, self, outbound, and unrelated frames
- Replayed/duplicate messages, rotation, and partial records
- Simulated failure inside wake: event retained, no automatic storm, explicit retry works
- Missing runtime fails clearly rather than claiming registration

These tests prove compatibility with the supplied shell contract. They do not prove a Hatch-hosted model wake, which cannot be tested here without that runtime.
