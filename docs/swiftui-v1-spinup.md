# SwiftUI v1 — Cursor Wire-up Spin-up List
**For:** @dot (Architect) → Cursor
**From:** Fuse (project lead)
**Date:** 2026-10-02
**Status:** Locked contract — ready to build

## v1 Scope (Alex's directive)
Native SwiftUI iOS app replicating the relay chatroom with multiple agents.

### In scope for v1
- Connect to owned Voizle relay WSS
- Join room with nick + trip
- Send/receive chat messages
- Presence (join/leave/nick lists)
- Workspace folder view (per Alex: "looks like a folder, unrelated chats filtered out")
- Apple HIG compliance (Cupertino is the HIG agent)

### Out of scope for v1
- App Store submission
- Push notifications
- Voice/video

## Protocol (locked)
- **Wire:** voizle-text-relay v1 (hello v:1)
- **Durable delivery:** ON (protocol_v2=true on server)
- **Mirror:** ON (HIVEMIND_MESSAGE_MIRROR=1)
- **v2 wire protocol:** Contract drafted (docs/chatbridge-v2-contract.md), not yet deployed — v1 wire for now

### Key frames
- `hello` → `join` → `welcome` (no replay in v2, but v1 has welcome.replay)
- `chat` send/receive
- Presence: `join`/`leave`/`nick`

## Tasks for Cursor
1. **Project scaffold** — SwiftUI app, WSS client (URLSessionWebSocketTask)
2. **Connection** — WSS handshake, hello parsing, join with nick/trip
3. **Chat UI** — Message list, composer, timestamps
4. **Presence** — Online sidebar, join/leave updates
5. **Workspace folders** — Folder list sidebar, per-workspace filtered transcript, back-to-room
6. **HIG polish** — Cupertino (HIG agent) reviews

## Dependencies
- Voizle relay WSS endpoint (from chief)
- Tripcode auth (public trip, not secret)

## Gates
- No App Store without Alex
- No DO changes without Alex
- chief (CEO) has final say on protocol
