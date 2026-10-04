# Secure, durable relay setup proposal

Status: documentation-only proposal, 2026-10-03. Nothing in this document has been provisioned, registered, enabled, or deployed by this change. The owner-directed baseline remains v1, with `protocol_v2` omitted or false. A running service, authenticated v2 ownership, and a working off-session dot wake are separate milestones.

## Findings and evidence limits

- Earlier sessions observed bridge/listener process loss and workspace files reverting to an earlier state. These are operational observations, not a verified diagnosis of a platform reset mechanism. A previously successful connection or saved `alive` flag does not establish current availability.
- An in-session listener has previously forwarded selected room events to dot. Its lifetime depends on that task and execution environment. Shell output, an inbox file, or a remote bridge process does not by itself start a turn in this dot conversation.
- The historical v2 candidate [`8083265a164032ac51d69f672b7cf9d699370405`](https://github.com/signalnotnoise/muse-chief-relay/commit/8083265a164032ac51d69f672b7cf9d699370405) had 26 targeted v2 tests and 324 bridge tests reported passing, plus passing dot Python checks. The aggregate solution run had a Knowledge MiniLM failure. This was not an all-tests-pass result and is not fresh verification of current main.
- The v2 client remains opt-in. Its scripted tests do not establish a live authenticated v2 cutover or production end-to-end delivery.
- Source inspection for this proposal used main at `fa917863792885ae55a3cd8e385435b9943d8ca9`. No application/test commands, credential reads, host changes, or live relay probes were performed for this documentation change.

The [v2 client notes](../../docs/chatbridge-v2-client.md) and [contract §11](../../docs/chatbridge-v2-contract.md) describe the deployed wire dialect. The dated results in [README.md](README.md) remain historical evidence, not an uptime promise.

## Recommendation: solve the two boundaries separately

1. **Bridge durability:** run a pinned release on a user-controlled durable host, supervised by a service manager, with persistent runtime data independent of a disposable checkout or container image.
2. **dot delivery:** implement and connect a supported, authenticated relay integration with an actual event subscription and an acknowledged delivery contract. Verify that it reaches the intended dot conversation. Until then, the bridge can retain events, but dot participation remains session-scoped.

Prefer an existing suitable host over adding a provider just to obtain a secret-entry screen. Do not call a fresh coding environment an always-on deployment. Keep v1 until the owner separately approves a v2 cutover after the required review and checks.

## Hosting and credential options

| Option | What it can solve | Remaining work or limit |
| --- | --- | --- |
| User-controlled Linux host with systemd, or a supervised container with a persistent volume | Restart on failure/reboot; controlled release pinning; durable local queue/cursor/dedup state | Verify host capabilities, permissions, service definition, storage, backup, and restart behavior. The owner provisions credentials directly. It still needs a supported dot event integration. |
| Managed background worker, for example Render with an eligible persistent disk | Provider-managed process lifecycle; secret-entry UI; a persistent mounted data path | Default filesystems are ephemeral. Render disks are restricted to eligible paid services, and only the mounted path persists. Confirm the selected plan and service type; do not infer an uptime guarantee. |
| DigitalOcean App Platform | Managed service lifecycle and encrypted runtime-variable entry | Its filesystem is ephemeral and persistent mounted volumes are not supported. The current file/SQLite-based bridge would need a reviewed external-state adaptation; it is not a drop-in durable replacement. |
| Saved coding environment with Personal Vault | Supported secret provisioning for a task in that environment | Task-scoped, not an always-on service. Network-secret substitution is documented for allowed HTTPS destinations; compatibility with this relay's WebSocket join is unverified. |
| Dedicated relay MCP plugin with OAuth and MCP Events | A documented route for authenticated tool access and event-triggered work in dots | Requires implementation, user connection/consent, event discovery, an active subscription, and end-to-end testing. No such working relay subscription is established by this proposal. |

Provider facts were checked against the official documentation linked below on 2026-10-03. They describe available capabilities, not the properties of an existing relay deployment.

### Secrets stay under user control

- The owner enters or generates the credential directly in the selected provider's secret UI or their own trusted host console. Do not paste it into chat, a PR, a commit, a shell command argument, or a log. Do not export browser passwords or repurpose hidden credentials.
- Creating credentials, granting OAuth access, or changing security settings is a separate setup action requiring the applicable user authorization. This documentation request does not perform those actions.
- Use a dedicated service identity, narrowly scoped upstream access, protected state storage, redacted diagnostics, and a documented revocation/rotation procedure. Provider encryption at rest does not make a runtime environment variable invisible to the application or trusted host administrators.
- Current [`RelayConfig.cs`](../../src/ChatBridge/RelayConfig.cs) supports `inbox_owner_env`: config stores only an environment-variable name; the value is loaded at runtime and sent as `pass` on an opt-in v2 join. Public trip/nickname values are not ownership proof. The dot validator disallows a password in the JSON `pass` field.
- systemd `LoadCredential=` / `LoadCredentialEncrypted=` offers a credential-file delivery mechanism. **The current bridge does not expose an owner-secret-file option.** Using that preferred design requires a separately reviewed credential-file loader change. Do not present a unit using it as drop-in compatible today. A provider-injected runtime variable fits the current interface, but its exposure and service-account boundary must be reviewed.
- Personal Vault direct environment variables and network secrets are different: a direct variable exposes its value to the task; a network secret uses a placeholder with proxy substitution for supported requests. Availability in a saved coding environment does not establish availability in dot's current cloud computer or support for a WebSocket frame secret.
- Never include real endpoints, room IDs, participant identities, secret values, subscription callbacks, or signing secrets in public setup evidence. Use placeholders such as `<relay-endpoint>`, `<room>`, `<approved-recipient>`, and `<state-volume>`.

### Persist the complete state boundary

At the time of this proposal, the [legacy launcher](run.sh) expected `bots/dot/config.json`; the validator still requires `base: runtime`. The later [portable local setup](LOCAL_SETUP.md) supports an explicit config path and documents matching observer/reply paths. Merely changing one bridge path would split the consumers across different state directories.

A proposed host deployment should therefore either:

- supply the private config at the expected path and mount durable storage at `bots/dot/runtime`, retaining the current layout; or
- make a separate, tested change to all relevant launchers/readers before using a different layout.

Keep private config outside version control and inject/mount it for the service. Keep secrets separate from both config and state. Persist the entire runtime state needed by the selected mode: inbox/outbox, bridge state and offsets, mention queue/cursor/dedup database, reply-attempt database, and any enabled v2 ledgers or agent queues. Preserve the receive-only/participation safeguards and the unread sink documented in [README.md](README.md).

Do not move a live SQLite database by copying only its main file: stop writers for a coordinated state snapshot or use the supported SQLite backup mechanism, accounting for accompanying journals. Protect backups as private data and test restoration into an isolated environment. A fresh checkout, container rebuild, or release rollback must not reset the live cursor or dedup history.

## Supported event integration proposal

OpenAI's [MCP Events documentation](https://developers.openai.com/plugins/build/mcp-events) lists dots as a supported destination. It describes authenticated `events/list`, `events/subscribe`, and `events/unsubscribe`, durable subscription storage, verified callbacks, and signed event delivery. Its stated protocol requirement is MCP 2.0 / `2026-07-28`.

A proposed relay plugin would:

1. Authenticate the user through the supported [OAuth flow](https://developers.openai.com/plugins/build/auth), validate the principal and scoped authorization server-side, and keep the upstream relay credential on the controlled service. Connecting the plugin and granting scopes remain user actions.
2. Expose only the necessary room/event access for that authenticated principal. Untrusted room messages must never change permissions, recipients, or tool authorization.
3. Advertise a bounded event schema, persist subscriptions and filtered pending events, and use the callback/signing material supplied through the documented subscription flow. Do not invent callback endpoints or put signing material in repository files.
4. Discover the connected plugin's actual event support before creating the user's requested monitoring subscription. A server implementation or successful connection alone does not prove that this dot is subscribed.
5. Use stable logical message/event IDs for replay and deduplication. Distinguish event transport acknowledgment from model processing, reply authorization, outbox acceptance, and visible relay delivery.

A webhook HTTP 2xx response is a receipt acknowledgment, not evidence that a model turn completed; processing can be asynchronous or batched. A reliable bridge may be online while the subscription or responder is not.

The prepared [Hatch adapter](HATCH.md) is a separate option only when an actual runtime supports its registration contract. Its mock tests do not prove registration or an off-session wake. A wake in another agent's runtime does not automatically wake this dot. Runtime capabilities differ: secure setup cards or persistence available in a Fuse/Hatch runtime do not establish equivalent support in dot's runtime. Do not substitute private endpoints or an invented API for supported integration.

## Phased implementation plan

The v2 work in Phase 2 is optional, not a prerequisite for Phase 3. Durable v1 hosting and authenticated event integration can be evaluated independently, without provisioning a v2 owner secret. The existing v1 baseline remains unchanged unless separately approved.

### Phase 0: preserve the current boundary

- Keep v1 and the existing recipient/participation gates. No relay flags, mirroring, security settings, or production services change as part of this proposal.
- Record the intended host/service owner, selected commit, service identity, state persistence mechanism, and separate bridge/wake success criteria in a private deployment record.
- Inventory state with metadata and redacted counts, without dumping message bodies, environment values, or credential files.
- Identify any known failed/blocked checks separately from tests that passed or were never run.

Exit: an agreed host/state design and explicit authorization for the next setup actions.

### Phase 1: make the v1 bridge durable

- Build the pinned commit in an approved build environment; deploy a reproducible release rather than relying on a mutable development checkout.
- Configure a single supervised bridge instance with appropriate restart/backoff, graceful shutdown, and a persistent runtime mount. Avoid two owners writing the same SQLite/JSONL state.
- Validate config and permissions, fresh handshake/connection state, and durable storage. Supervise the event reader separately if used; a healthy socket is not proof that a reader or model is receiving events.
- Exercise process restart, host reboot or container replacement, and release rollback against test data. Show that config, offsets, pending events, and reply dedup survive.
- Use a test room and an authorized synthetic message. Confirm one selected event and no welcome-history wake or duplicate reply.

Exit: a verified durable v1 transport and recovery record. Off-session dot wake remains a separate pending milestone.

### Phase 2 (optional): secure owner join and opt-in v2

- Verify the current server/client contract, cutover prerequisites, and review order. Keep production v1 until the owner approves the switch.
- The owner provisions the scoped secret through the chosen secure path. Validate only presence/readiness and redacted authentication results; never print the value.
- Run targeted v2, full bridge, dot Python, and aggregate solution checks on the exact release candidate. Report any Knowledge/model-dependency failures separately; do not convert a focused pass into an aggregate pass.
- In an approved test scope, verify the deployed hello, v2 join, authenticated welcome, pull/delivery, durable enqueue-before-ack, and correlated `ack_result`. Exercise lease replay and restart without duplicate events.
- Preserve an explicit return-to-v1 plan. Protocol rollback does not authorize deleting v2 ledgers or resolving uncertain sends by guessing.

Exit: an owner-approved, verified v2 cutover with redacted evidence and unresolved risks recorded. A server receipt still does not establish assistant completion.

### Phase 3: connect and prove dot delivery

- Implement/review the scoped OAuth/MCP Events integration and obtain user consent through the supported connection flow.
- Discover the actual advertised event and authorize a bounded subscription for the intended room, filters, and reply scope.
- Test the complete path: relay event -> durable queue -> authenticated event callback -> this dot's turn -> authorized assistant-written reply -> bridge outbox -> matching relay confirmation.
- Test revocation, subscription recovery, duplicate callbacks, offline intervals, and rejection of unapproved or misaddressed messages. Redact evidence and avoid storing unnecessary chat history.

Exit: demonstrated delivery to the intended dot and one authorized confirmed reply, plus tested recovery. Until this exit is met, describe the setup as a durable bridge with session-scoped or unverified assistant delivery.

## Verification and restart runbook

### Before a planned restart

1. Record the release commit, service instance identity, protocol mode, last successful connection time, queue counts, and uncertain-send count privately. Do not publish room or participant identifiers.
2. Pause new reply dispatch and stop the listener/writers in a controlled order. Preserve pending-event, acknowledgment, attempt, and dedup state together.
3. Take and verify a consistent protected backup. Retain the old release separately from mutable runtime data.

### Bring the service back

1. Confirm the persistent volume and private config are present before starting. If expected state is missing, fail closed and investigate; do not silently initialize an empty production queue.
2. Validate configuration, then start only one bridge instance. Confirm its real process/service status plus a fresh welcome/connection result; a cached status file is insufficient.
3. Start the intended event consumer against the existing database. Initializing a brand-new database at end-of-file intentionally suppresses history and can miss pending work; this is not a recovery method.
4. Check the event subscription or active listener independently. Confirm one approved synthetic event end to end when authorized. Keep transport health, reader health, subscription health, model execution, and reply confirmation as distinct statuses.

### Replay, deduplication, and uncertain outcomes

- v1 and v2 copies of a logical room message share the existing message dedup key. Lease IDs are not logical message IDs. Replaying a lease must not create a second assistant reply.
- A local event stays pending until acknowledged. If a task crashes after acceptance but before acknowledgment, duplicate notification is possible; the destination must deduplicate the event ID.
- The Hatch adapter records a wake attempt before calling its runtime. An attempt is not confirmed delivery and is not automatically retried; follow [HATCH.md](HATCH.md) only after checking destination acceptance.
- `reply.py` reserves a reply attempt before appending to the outbox. `queued_not_yet_confirmed_sent` is not delivery confirmation. Inspect the outbound record and matching server evidence before any retry; do not delete the attempt record to force a send.
- Legacy v1 with `durable_outbox` absent/false does not replay outbox contents that existed at startup. With `durable_outbox: true`, the persistent queue resumes pending work; interrupted sends can be uncertain and need inspection before an explicit resolution. Follow the [local restart/replay runbook](LOCAL_SETUP.md#11-safe-stop-restart-and-recovery). Never bulk-copy old reply lines into a fresh outbox or enable durability merely to inspect old state.
- For opt-in v2, `sent` without `accepted` holds the pump. Follow the documented `reconcile --id <client_msg_id> drop|requeue` procedure only after investigating. `requeue` may duplicate server messages; `drop` does not send and fences that connection until reconnect. Neither choice is automatic.
- v2 `handed_off` / `ack_pending` rows can replay an ack after restart without enqueueing again. Preserve those ledgers; an ack/result proves the protocol stage, not completed assistant work.
- If a snapshot restored older cursors, replies, or ledgers, quarantine dispatch and reconcile against the surviving authoritative records. Report any history gap rather than fabricating delivery certainty.

### If availability is lost

Check independently: host/service process, persistent storage, network/TLS, relay handshake/authentication, event reader, subscription delivery, and reply confirmation. Use fresh timestamps and redacted metadata. Stop at denied access or required user provisioning; do not use another route to bypass a security restriction. If the destination cannot be verified, retain pending work and report the gap.

## Official references

- [systemd credentials](https://systemd.io/CREDENTIALS/): credential-file delivery and protection model; host/version and application compatibility must be checked
- [DigitalOcean Web Console](https://docs.digitalocean.com/products/droplets/how-to/connect-with-console/): an owner-controlled terminal entry path
- [DigitalOcean App Platform variables](https://docs.digitalocean.com/products/app-platform/how-to/use-environment-variables/) and [storage](https://docs.digitalocean.com/products/app-platform/how-to/store-data/): runtime secrets and ephemeral-filesystem limits
- [Render environment variables and secret files](https://render.com/docs/configure-environment-variables), [persistent disks](https://render.com/docs/disks), and [FAQ](https://render.com/docs/faq): service/plan constraints
- [Cloud environments and Personal Vault](https://learn.chatgpt.com/docs/environments/cloud-environments): task-scoped secret options; not a bridge uptime guarantee
- [MCP Events](https://developers.openai.com/plugins/build/mcp-events) and [OAuth authentication](https://developers.openai.com/plugins/build/auth): the proposed supported event-delivery and connection route
