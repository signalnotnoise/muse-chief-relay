# SwiftUI v1 — Cursor Wire-up Spin-up List
**For:** @dot (Architect) → Cursor
**From:** Fuse (project lead)
**Date:** 2026-10-02 (updated 2026-10-03)
**Status:** dot sign-off confirmed 2026-10-03 — scoped to the iOS chat-only wire @fa91786 (does not cover the macOS/visionOS expansion or implementation/runtime verification). Alex locked universal (iOS+macOS+visionOS) 2026-10-03. The destination list in this file and in the Xcode target is already universal. Shared SwiftUI only until Dot signs macOS and visionOS shells. Do not treat this file as sign-off for platform-specific UI.

## Destinations
One SwiftUI app, one shared core. Xcode destinations are iOS, macOS, and visionOS. The wire does not change per platform.

- **iOS and visionOS:** a scene that backgrounds closes the socket. It reconnects when the scene is active. No background mode.
- **macOS:** the process stays in the foreground. Switching apps does not drop the socket. The window has a default size. No separate Mac shell.
- **visionOS:** the same chat window, with a default size. No ornament, volume, or immersive shell.
- Apple Intelligence stays an on-device product surface. It is not a Voizle wire change. Availability is per OS, runtime, and language.

## v1 Scope (Alex's directive)
Native SwiftUI universal app (iOS + macOS + visionOS) replicating the relay chatroom with multiple agents.

### In scope for v1
- Connect to owned Voizle relay WSS
- Join room with nick + public trip (trip is display metadata — not auth, not verified owner authority)
- Send/receive chat messages
- Presence (join/leave/nick lists) — native `type:presence` frames
- Mentions stay plain chat text. There is no mention frame.
- Apple Intelligence: session-only summaries (private-by-default, bounded local context), Writing Tools — behind OS/runtime/language availability gates, with ordinary-chat fallback when unavailable
- App Intents (room-ID only)
- Reconnect/rejoin with replay chat-ID dedup (original timestamps, `recentReplay` labeling with coverage + connection-gap labels)
- No silent truncation, no automatic resend; uncertain sends mark a gap, never auto-resend
- Delivery receipts are display-only — receipts are not agent-task completion

### Out of scope for v1
- Durable join/pull/ack (deployed server-side; this client is chat-only and does not opt in)
- Owner secret / password entry
- Appwrite mirroring and history backfill
- Workspace folders/filtered transcripts (deferred — needs server-side `workspace_id`)
- Workspace cards or board UI
- Perpetual background socket (iOS and visionOS close on background and reconnect on foreground; macOS does not add a background mode)
- macOS or visionOS UI shells beyond the shared window (held for Dot; destinations are already universal)
- Pages or DigitalOcean changes
- App Store submission
- Push notifications
- Voice/video

## Protocol
- **Wire:** voizle-text-relay v1 (`hello` v:1 + capability fields)
- **Capabilities are opt-in:** the client explicitly opts into what it uses; advertised server capabilities do not opt the client in
- **Durable delivery:** support IS deployed server-side (protocol_v2=true); this client does not use the durable join/pull/ack path
- **v2 wire protocol:** contract drafted (docs/chatbridge-v2-contract.md); this client uses the v1 wire
- **Mentions:** plain chat text. There is no mention frame.

### Key frames
- `hello` (with capability fields) → `join` → `welcome` + `welcome.replay`
- `chat` send/receive (with chat-ID dedup on reconnect)
- Presence: native `type:presence` frames for `join`/`leave`/`nick`

## Tasks for Cursor
1. **Project scaffold** — One SwiftUI app for iOS, macOS, and visionOS; WSS client (URLSessionWebSocketTask)
2. **Connection** — WSS handshake, hello parsing (capabilities), join with nick/trip (display metadata)
3. **Chat UI** — Message list, composer, timestamps (original, not rewritten)
4. **Presence** — Online sidebar via native presence frames; capture exact presence payloads as fixtures
5. **Reconnect** — Rejoin with replay dedup, `recentReplay` labeling (timestamps + coverage + gap labels)
6. **Apple Intelligence** — Session summaries (private-by-default, bounded context; replayed messages excluded unless explicitly labeled in), availability gates + ordinary-chat fallback, Writing Tools
7. **HIG polish** — Cupertino (HIG agent) reviews

## Replay / summary boundary (dot's definition, retained)
- Replayed messages are excluded from session summaries by default (catch-up, not live session context)
- Any inclusion later must carry original timestamps, coverage labels, and connection-gap labels

## Dependencies
- Voizle relay WSS endpoint (from chief)
- Public trip for display (not auth, not owner authority)

## Task identity (latest records, 2026-10-03)
- **Authoritative Cursor agent:** bc-c3749813 — wire agent up against brief @fa91786, writing `ios/` (chief, 2026-10-03)
- **Duplicate agent:** bc-ff08c5c8 — stopped/archived per chief's 02:26:55 UTC report; not authoritative
- **Arch sign-off:** @dot confirmed 2026-10-03 — scoped to the narrow iOS chat-only brief @fa91786; does not automatically cover the macOS/visionOS expansion or implementation/runtime verification

## Gates
- No App Store without Alex
- No DO changes without Alex
- No Pages deploy without Alex
- chief (CEO) has final say on protocol
- Dot's fa91786 sign-off does not cover macOS or visionOS shells. Fuse and Dot re-sign that expansion. The destination list itself is already universal.
