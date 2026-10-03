# SwiftUI v1 — Cursor Wire-up Spin-up List
**For:** @dot (Architect) → Cursor
**From:** Fuse (project lead)
**Date:** 2026-10-02
**Status:** Draft — reconciling dot's feedback, not yet locked

## v1 Scope (Alex's directive)
Native SwiftUI iOS app replicating the relay chatroom with multiple agents.

### In scope for v1
- Connect to owned Voizle relay WSS
- Join room with nick + public trip (trip is identity evidence, not auth)
- Send/receive chat messages
- Presence (join/leave/nick lists) — native `type:presence` frames
- Apple Intelligence: session-only summaries, Writing Tools
- App Intents (room-ID only)
- Reconnect/rejoin with replay chat-ID dedup (original timestamps, `recentReplay` labeling)
- No silent truncation, no automatic resend

### Out of scope for v1
- Workspace folders/filtered transcripts (deferred — needs server-side `workspace_id`)
- App Store submission
- Push notifications
- Voice/video

## Protocol
- **Wire:** voizle-text-relay v1 (hello v:1 + capability fields)
- **Durable delivery:** ON (protocol_v2=true on server)
- **Mirror:** ON (HIVEMIND_MESSAGE_MIRROR=1)
- **v2 wire protocol:** Contract drafted (docs/chatbridge-v2-contract.md), not yet deployed — v1 wire for now

### Key frames
- `hello` (with capability fields) → `join` → `welcome` + `welcome.replay`
- `chat` send/receive (with chat-ID dedup on reconnect)
- Presence: native `type:presence` frames for `join`/`leave`/`nick`

## Tasks for Cursor
1. **Project scaffold** — SwiftUI app, WSS client (URLSessionWebSocketTask)
2. **Connection** — WSS handshake, hello parsing (capabilities), join with nick/trip
3. **Chat UI** — Message list, composer, timestamps (original, not rewritten)
4. **Presence** — Online sidebar via native presence frames
5. **Reconnect** — Rejoin with replay dedup, `recentReplay` labeling
6. **Apple Intelligence** — Session summaries, Writing Tools integration
7. **HIG polish** — Cupertino (HIG agent) reviews

## Dependencies
- Voizle relay WSS endpoint (from chief)
- Public trip for identity (not auth)

## Gates
- No App Store without Alex
- No DO changes without Alex
- chief (CEO) has final say on protocol
