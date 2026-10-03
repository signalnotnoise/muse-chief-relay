# Workspace routing v1: bounded review contract

Status: **DRAFT FOR GROUP REVIEW — not implemented, not deployed**  
Date: 2026-10-03  
Draft owner: dot; proposed implementation/readiness owner: Fuse, subject to acceptance

## Smallest implementation slice

- Stamp one explicit stable workspace key on each new send; carry its authoritative message UUID and room context end to end.
- Inherit replies only from a validated same-room parent; reject missing, cross-room, or conflicting parent links.
- Persist visible unassigned/rejected outcomes and reasons in the proposed reserved diagnostic sink, without guessing a workspace.
- Freeze the routing envelope before queued writes; verify canonical content and routing on duplicate UUIDs and restart/retry.
- Demonstrate the happy path and negative cases with fixtures, then close the schema/wire readiness gates before any separately authorized rollout; leave legacy records and permissions unchanged.

## 1. Goal and boundary

Give each new mirrored chat message a stable workspace tag, and let a reply inherit that tag from a validated parent message. A workspace is a tag/view in the existing API-key service model. It is **not an access boundary**, tenant-isolation guarantee, or substitute for authentication. This contract makes no claim about live credentials, collection permissions, database contents, or deployed behavior.

The small deliverable is explicit selection → accepted message → frozen routing → mirror row → workspace view, plus reply inheritance and deterministic failure/retry behavior. It does not authorize a production toggle, schema change, migration, deployment, backfill, permission change, or broader server/security review. Existing records stay untouched.

## 2. Evidence and facts still to verify

Public repository files were read through the GitHub connector on 2026-10-03; no repository source was executed and no live database was accessed. Observed main revision: `d9965fe5a1984b756bcf27ad4ddbebd4e918ca09`.

- [Mirror documentation](https://github.com/signalnotnoise/muse-chief-relay/blob/d9965fe5a1984b756bcf27ad4ddbebd4e918ca09/docs/hivemind.md) describes the opt-in asynchronous mirror, channel-hash workspace fallback, durable spill, existing message fields, and no raw channel/trip/password in mirror records.
- [Pure schema/planning helpers](https://github.com/signalnotnoise/muse-chief-relay/blob/d9965fe5a1984b756bcf27ad4ddbebd4e918ca09/web/muse/hivemind.js) declare `workspaces.key` and `messages.workspaceKey` as 64-character strings. Workspace fields are `key`, optional `name` (128), optional `description` (2048), and required integer `createdTs`. Message fields are `workspaceKey`, `threadKey` (64), `sender` (64), `text` (8192), and integer `ts`.
- That source declares `threadKey` **required** and defaults it to `room`; a peer schema report described it as optional. This is an unresolved source-versus-live-schema discrepancy, not proof of either deployed setting. v1 accepts an omitted input and persists `room`, satisfying either reported shape.
- [Current client](https://github.com/signalnotnoise/muse-chief-relay/blob/d9965fe5a1984b756bcf27ad4ddbebd4e918ca09/web/muse/hivemindClient.js) treats message/workspace create conflicts as first-write-wins success, including a 409 without comparing the raced message. v1 must strengthen message verification; the existing behavior does not already meet this contract.
- [Mirror interface](https://github.com/signalnotnoise/muse-chief-relay/blob/d9965fe5a1984b756bcf27ad4ddbebd4e918ca09/web/muse/messageMirror.js) plans before its in-memory retry loop. This is a useful seam, not evidence that v1 metadata survives the relay, bridge, spill file, or restart.

The comments in repository schema helpers say they match live collections. This draft treats that as a source assertion only. An authorized schema inventory is still needed before rollout.

## 3. Identifiers and provenance

### Workspace key and workspace document ID are different

- A selectable `workspaceKey` is an existing, stable registry key, never a room name, current-tab label, mutable display name, or default guessed by a receiver.
- Retain the public helper's key grammar: a lowercase 64-hex key or lowercase alphanumeric slug with single hyphen-separated parts, maximum 64 characters. Compare exact canonical keys; do not silently trim, case-fold, rename, or truncate keys.
- A syntactically valid value is not automatically a registered workspace. v1 resolves it against the registry and does not create an arbitrary workspace just because a message supplied a key.
- Retain the existing workspace document-ID mapping: `s1_` + first 32 lowercase hex characters of SHA-256 over UTF-8 `workspaces` + NUL + the complete key. It is 35 characters. Store the complete key in the `key` attribute and verify it after every lookup/create conflict. A different full key at the same derived ID is `workspace_id_collision`; never accept or overwrite it.
- The truncated digest has a nonzero collision risk; this check detects a conflicting key. Neither this mapping nor possession of a key authenticates a caller.

Appwrite's [document-create API](https://appwrite.io/docs/references/1.6.x/client-web/databases#createDocument) limits custom document IDs to 36 characters, beginning with a letter or digit, followed by letters, digits, periods, hyphens, or underscores. A full 64-hex SHA-256 with a prefix cannot be used as the document ID. An attribute value may be longer if its configured field size permits it; ID limits and field limits are separate.

### Message and parent IDs

`id` is the relay-stamped **individual chat message UUID**, represented as canonical lowercase `8-4-4-4-12` hexadecimal text (36 characters). It becomes the messages document `$id`; it is not a room ID. `parentMessageId`, when supplied, uses the same canonical representation and identifies exactly one earlier accepted chat. The existing phrase “room UUID” is ambiguous; implementation and new documentation must say “chat message UUID.”

Do not mint another message ID on retry or mirror restart. If the transport does not provide a stable, authoritative message UUID, v1 cannot be enabled for that transport.

### Proposed `roomProvenance`

Proposed encoding: `rp1_` + full lowercase SHA-256 hex of the UTF-8 compact JSON array `["relay-room-v1", relayAuthorityId, canonicalRoomId]` (68 characters total). The tuple must come from the trusted transport adapter's accepted-event context. `relayAuthorityId` is a fixed service namespace; `canonicalRoomId` is the authoritative stable room identity. They are not taken from user text, nickname, public trip, or a client-supplied provenance hash. If only a canonical room name exists, the adapter may hash it only after its exact authoritative normalization/lifetime semantics are documented.

Compare both parent and child using the same provenance version and service namespace. The raw room identity never enters the mirror row, spill record, URL, or diagnostic log. Hashing is data minimization, not encryption or proof of identity: low-entropy room names can be guessed, and hashes are not collision-free. A parent lookup must bind the UUID to authoritative accepted-event room metadata; a hash supplied by an arbitrary client is insufficient.

**Factual gate:** this design assumes the server/accepted-event path preserves explicit `workspaceKey`, `parentMessageId`, stable message UUID, and trustworthy room context through delivery and replay. That has not been verified. If the wire strips or lets clients forge that metadata, the missing adapter/server support must be implemented and demonstrated before claiming routing works. Do not infer provenance from a matching sender or text.

## 4. Decision contract

Resolve once for each accepted chat, before it enters the mirror write queue. A composer snapshots its selected key for that send; later tab changes do not affect it. A client stamps `routingVersion: 1`; v1 is distinct from any relay protocol version.

Process in this order:

1. Validate authoritative message ID, current-room provenance, sender, body, and timestamp. Invalid authority/identity input is rejected before a database write; persist a minimal local diagnostic and expose the failure. Do not generate a UUID or trust a client room claim to make it fit.
2. If `parentMessageId` is present, validate its UUID and resolve that exact parent before considering a destination. Verify authoritative provenance equality. Missing parent, cross-room parent, or unverifiable parent is a routing rejection even when an explicit workspace key is also supplied.
3. A routable parent must carry a validated v1 routing record. Legacy rows without that evidence cannot establish inheritance. If an explicit key accompanies a reply, it must equal the parent's frozen `workspaceKey`; a different key is a routing rejection. Do not prefer the explicit key over a linked parent.
4. For an unstamped reply to an assigned parent, use its frozen key and validate that key is still available at this decision point. For a reply to a parent whose outcome is unassigned/rejected, retain an unassigned outcome; do not infer a workspace from its room.
5. For a new message with no parent, validate the explicit key against the registry. A valid existing key is assigned. Missing, malformed/reserved, or unavailable/deleted keys get the explicit unassigned representation below. A lookup error is a held pre-decision state, not proof that a key is absent; show `workspace_lookup_pending` and retry the lookup without guessing.
6. Freeze the result and persist the queue envelope. After freezing, all delivery retries use it unchanged, without reading current UI selection, rehashing the channel into a destination, or rerunning registry/parent routing.

| Input / condition | Frozen outcome | Persisted reason | Destination |
|---|---|---|---|
| New message, explicit existing key | `assigned` | `explicit` | Selected key |
| Reply, valid same-room assigned parent, no key | `assigned` | `parent_inherited` | Parent's key |
| Reply, matching explicit key and valid parent | `assigned` | `parent_confirmed` | Parent's key |
| New message without a key | `unassigned` | `workspace_missing` | Reserved sink |
| Malformed key or direct attempt to select reserved sink | `unassigned` | `workspace_invalid` | Reserved sink |
| Selected/inherited key absent or deleted at decision time | `unassigned` | `workspace_unavailable` | Reserved sink |
| Reply with nonexistent parent | `rejected` | `parent_missing` | Reserved sink; no workspace attachment |
| Parent in another room | `rejected` | `parent_room_mismatch` | Reserved sink; no workspace attachment |
| Parent UUID invalid | `rejected` | `parent_id_invalid` | Reserved sink; omit malformed parent value |
| Parent lacks trustworthy routing/provenance | `rejected` | `parent_unverifiable` | Reserved sink; no workspace attachment |
| Explicit reply key differs from parent's key | `rejected` | `reply_workspace_mismatch` | Reserved sink; no workspace attachment |
| Unstamped reply to unassigned/rejected parent | `unassigned` | `parent_unassigned` | Reserved sink |

These are mirror-routing outcomes. They do not retroactively unsend an already accepted relay chat. “Rejected” means the requested workspace attachment is refused and visibly diagnosed.

A rejected-parent row in the reserved sink is only a diagnostic record of that refusal. It is not a valid routed reply, silent reassignment, or permission to attach to another parent. No ordinary workspace view includes it; only the explicitly labeled routing-issues view does. Retaining `parentMessageId` there records the rejected request without establishing an accepted reply relationship.

A reply with an invalid key still undergoes parent checks first. A key different from the parent remains `reply_workspace_mismatch`; removing that link automatically is forbidden. If the user deliberately clears the reply link and sends again, that is a new action and a new message UUID, evaluated as a new message. Do not mutate/relabel the original row to satisfy it.

A missing parent is a terminal rejection for that accepted message under this minimal v1. This avoids an unbounded late reroute. Transient lookup/network failure is not a missing parent: retain the unresolved envelope in a durable pre-decision hold and expose `parent_lookup_pending`. Resolution may continue there, but an assigned route may never enter the write queue before its parent check succeeds.

### Explicit proposed unassigned representation

Reserve the key **`routing-unassigned-v1`**. It matches the existing key grammar and fits the 64-character field. It is a diagnostic tag, not a guessed existing workspace, and must never appear as an ordinary selectable destination. This is proposed schema/registry content, not a claim that such a workspace exists today.

Before enablement, the readiness owner must verify the key is unused and arrange the separately authorized reserved-row creation, with an unmistakable display name such as “Unassigned routing.” A preexisting different use is a gate, not permission to take it over. A sink row preserves the required non-null `workspaceKey` while `routingOutcome` and `routingReason` distinguish unassigned and rejected messages.

Persist those outcome/reason fields alongside the message and show a visible “Unassigned” or “Routing rejected” state with the reason. An operational log alone is not enough. If the database write fails, retain the frozen envelope and its reason durably and show mirror-pending/failure status; do not report a persisted row until it is verified.

## 5. Frozen v1 envelope and schema delta

Keep existing fields and limits. Do not change workspace `name`, `description`, or first-created timestamp during message routing.

| Field | v1 contract | Proposed storage / compatibility |
|---|---|---|
| `id` | Authoritative chat UUID, frozen | Appwrite `$id`, 36 characters; not another user attribute |
| `workspaceKey` | Resolved key or reserved sink | Existing required string, maximum 64 |
| `threadKey` | Optional input; persist `room` if absent | Existing string, maximum 64; freeze once; never substitutes for parent UUID |
| `sender` | Accepted-event sender, frozen | Existing required string, maximum 64; nickname is not identity proof |
| `text` | Accepted text, unchanged | Existing required string, maximum 8192; reject oversize, never truncate |
| `ts` | Accepted-event timestamp normalized once to epoch seconds | Existing required integer; retain established supported range; never replace with retry time |
| `routingVersion` | Exactly integer `1` | New optional integer at collection level to retain old rows; required by v1 validator |
| `parentMessageId` | Canonical UUID when linked | New optional string, maximum 36; omit when absent/invalid |
| `roomProvenance` | Server-context-derived value above | New optional string, size at least 68; required by v1 validator |
| `routingOutcome` | `assigned`, `unassigned`, or `rejected` | New optional string, proposed size 16; required by v1 validator |
| `routingReason` | Stable code from decision table | New optional string, proposed size 64; required by v1 validator |

Collection-level optionality is for old records only; new v1 writes fail closed on missing required v1 metadata. No defaults may disguise legacy rows as v1. Omitted optional fields and returned `null` are equivalent only where the declared v1 canonical schema explicitly permits absence; empty strings do not stand in for a valid parent/provenance.

The durable pre-decision record preserves the accepted-event inputs needed to resume resolution. The durable write record contains the complete frozen envelope, not merely the old six helper fields. Both exclude raw room names, trips, credentials, and passwords. The writer/spill reader must distinguish pending resolution from ready-to-write; a restart cannot bypass this stage boundary.

If a workspace is deleted **after** the route is frozen, a retry retains its original key and outcome. It must not recreate the workspace, assign another one, or retroactively recategorize the message. A read-side missing-label indicator is acceptable. This is consistent with tag/view semantics; workspace deletion is not access revocation in this model. Key reuse is prohibited in v1 registry management.

## 6. Create-conflict and retry rule

Use create-or-verify, never update/upsert as duplicate recovery.

- On successful create, validate the returned message against the frozen canonical envelope.
- On 409 or existing-ID lookup, read that exact UUID. Success requires equality of **every immutable content and routing field**: `$id`, `workspaceKey`, `threadKey`, `sender`, `text`, `ts`, `routingVersion`, `parentMessageId`, `roomProvenance`, `routingOutcome`, and `routingReason`.
- Equality is typed field equality after the contract's one-time normalization and declared optional-absence handling. Ignore JSON property ordering and server-managed timestamps; do not compare raw JSON bytes. Do not coerce strings into numbers, trim text, or erase mismatched route fields.
- An existing v0 row is not equal to a v1 envelope simply because its UUID/content matches. A missing v1 field, changed payload, changed route, different parent, or different provenance is `message_id_conflict`. Preserve the existing row, persist/show the conflict, and stop retrying that write as success. Do not change permissions as part of verification.
- A read error after a 409 is unverified, not success. Keep the frozen job for bounded transport retry/recovery. A crash after create but before acknowledgment should become verified equality on replay, with exactly one row.
- Workspace collision checks compare the complete immutable `key`; existing `createdTs` remains first-write-wins and mutable display metadata is not rewritten by mirroring. A workspace identity conflict must stop the associated message write, not be treated as permission to continue.

The current message `dedupConflict` success shortcut must not be used for v1. Failures remain off the relay delivery critical path, but they must remain visible and durably recoverable rather than being silently reported as mirrored.

## 7. Legacy compatibility

Call the existing channel-hash routing interpretation **v0**. This is a read-compatibility label; do not add version fields to old records or relabel their workspace keys. A reader can show those records as legacy using the existing hash/tag interpretation.

For new v1 traffic, the channel hash is never a fallback destination. An old row without a version does not authorize the receiver to downgrade a new unstamped message to v0. Keep the old records untouched and do not replay old spill jobs through a v1 resolver as if they were new sends. Inventory unresolved old queue entries and handle them under a separately reviewed compatibility/recovery decision before enablement; this contract grants no legacy write replay or backfill authority.

Legacy backfill/reclassification remains deferred. There is no permission-model change in this design.

## 8. Minimum demonstration and acceptance cases

Use fixtures and a mocked registry/store first. Names/UUIDs below are synthetic. No live service credentials are needed.

Registry fixture: existing selectable keys `project-alpha` and `project-beta`, plus the separately prepared reserved sink. Define synthetic room proofs `P` and `Q` by applying the specified provenance function to two distinct fixture room identities.

1. **New message:** accepted message `11111111-1111-4111-8111-111111111111`, explicit `project-alpha`, no parent, room proof `P`. Persist exactly one row with key `project-alpha`, version 1, `assigned/explicit`.
2. **Reply inheritance:** accepted message `22222222-2222-4222-8222-222222222222`, parent equal to case 1's UUID, no workspace stamp, proof `P`. Persist `project-alpha`, `assigned/parent_inherited`, and the original parent UUID.
3. **Matching explicit reply:** the same parent and explicit `project-alpha` produce `assigned/parent_confirmed` for a fresh UUID.
4. **Missing parent:** a fresh reply referencing an absent fixture UUID produces `rejected/parent_missing`, visible in the reserved sink. No ordinary workspace receives it.
5. **Cross-room parent:** the parent from case 1 with child proof `Q` produces `rejected/parent_room_mismatch`, even if the explicit key matches.
6. **Conflicting reply key:** the parent from case 1 plus explicit `project-beta` produces `rejected/reply_workspace_mismatch`; it is not silently detached or rerouted.
7. **Unassigned cases:** separate new UUIDs cover missing key, malformed/reserved key, and unavailable/deleted key; each gets the correct persisted reason. Registry/network failure instead holds unresolved and makes no absence claim.
8. **Retry/restart:** freeze case 2, change current UI selection, and crash at both pre-write and post-create/pre-ack points. Restart from durable state; payload, key, parent, provenance, outcome/reason remain identical and there is one row.
9. **Collision/conflict:** a replay with JSON properties reordered verifies; a replay with the same UUID but different text, timestamp, parent, key, or provenance does not. A raced 409 with mismatched content and a 409 whose follow-up read fails are not success.
10. **Boundary checks:** UUID/document-ID length, 64-character workspace key, 68-character provenance, 8192-character text, optional-field absence, reserved-key collision, parent lacking v1 provenance, metadata stripped in transit, and existing v0 rows all have explicit assertions.

Acceptance evidence must include the persisted or mocked final rows and reasons, not only successful UI selection or a message on the relay. The final integration trace must cover composer → accepted relay event → bridge input → durable queue/spill → helper → verified store row → visible view state. A mocked test is not a live verification.

## 9. Bounded readiness gates and handoff

One named owner must hold the implementation/readiness checklist end to end; **Fuse is proposed, not presumed assigned**. dot owns this draft and consolidates group review. Review can settle this contract without enabling anything.

Before a separately authorized rollout, that owner supplies:

- Exact deployed Appwrite API/server/SDK version and a read-only schema inventory: collection IDs, field types/sizes/requiredness/defaults, index definitions/status, and the unresolved `threadKey` discrepancy. Do not paste credentials or private documents into the review.
- A specific additive schema plan preserving old records. New v1 fields stay optional for existing data but are mandatory in the v1 writer. Verify the proposed string sizes rather than assuming document-ID limits also describe field capacity. Appwrite's [attribute model](https://appwrite.io/docs/references/cloud/models/attributeString) exposes configured size, requiredness, and status.
- The exact query plan and only its necessary indexes: workspace-key lookup/uniqueness and workspace-filtered time ordering, plus any declared routing-issue view. Parent lookup is by document ID and does not need an invented parent index. Check index length limits against the actual deployed version; do not guess. Every required new attribute/index must be `available`, not merely accepted for creation; [index status](https://appwrite.io/docs/references/cloud/models/index) is asynchronous.
- Proof that the chosen reserved key is unused and can be prepared without repurposing an existing workspace; proof that workspace keys cannot be reused.
- The accepted-event metadata/provenance trace and a wire fixture establishing that metadata is retained and room claims cannot be substituted by the client at the trusted adapter boundary. Missing support is a small implementation blocker, not a reason to launch an unrelated server/security audit.
- Passing minimum cases above, explicit handling of existing pending v0 queues, bounded durable retry/hold behavior, and a disable/rollback plan that stops new v1 writes without deleting or rewriting rows.
- Review of the precise implementation diff and explicit production go-ahead through the approved workflow. The mirror gate stays off until then.

No code, tests, migrations, live database calls, secret access, or deployment were performed to prepare this document. This draft is the artifact for review; it does not claim the routing goal has shipped.
