# SwiftUI v1 — Cursor Wire-up Spin-up List
**For:** @dot (Architect) → Cursor
**From:** Fuse (project lead)
**Date:** 2026-10-02
**Status:** Draft — dot's final boundary list incorporated; pending dot sign-off, then Cursor go

## v1 Scope (Alex's directive)
Native SwiftUI iOS app replicating the relay chatroom with multiple agents.

### In scope for v1
- Connect to owned Voizle relay WSS
- Join room with nick + public trip (trip is display metadata — not auth, not verified owner authority)
- Send/receive chat messages
- Presence (join/leave/nick lists) — native `type:presence` frames
- Apple Intelligence: session-only summaries (private-by-default, bounded local context), Writing Tools — behind OS/runtime/language availability gates, with ordinary-chat fallback when unavailable
- App Intents (room-ID only)
- Reconnect/rejoin with replay chat-ID dedup (original timestamps, `recentReplay` labeling with coverage + connection-gap labels)
- No silent truncation, no automatic resend; uncertain sends mark a gap, never auto-resend
- Delivery receipts are display-only — receipts are not agent-task completion

### Out of scope for v1
- Durable join/pull/ack (deployed server-side; iOS v1 is chat-only and does not opt in)
- Owner secret / password entry
- Appwrite mirroring and history backfill
- Workspace folders/filtered transcripts (deferred — needs server-side `workspace_id`)
- Perpetual background socket (iOS backgrounding rules apply; reconnect on foreground)
- App Store submission
- Push notifications
- Voice/video

## Protocol
- **Wire:** voizle-text-relay v1 (`hello` v:1 + capability fields)
- **Capabilities are opt-in:** the client explicitly opts into what it uses; advertised server capabilities do not opt the client in
- **Durable delivery:** support IS deployed server-side (protocol_v2=true); iOS v1 does not use the durable join/pull/ack path
- **v2 wire protocol:** contract drafted (docs/chatbridge-v2-contract.md); iOS v1 uses the v1 wire

### Key frames
- `hello` (with capability fields) → `join` → `welcome` + `welcome.replay`
- `chat` send/receive (with chat-ID dedup on reconnect)
- Presence: native `type:presence` frames for `join`/`leave`/`nick`

## Tasks for Cursor
1. **Project scaffold** — SwiftUI app, WSS client (URLSessionWebSocketTask)
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

## Gates
- No App Store without Alex
- No DO changes without Alex
- No Pages deploy without Alex
- chief (CEO) has final say on protocol
