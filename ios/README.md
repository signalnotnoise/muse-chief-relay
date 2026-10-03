# Muse for Apple (v1) — iOS, macOS, and visionOS

Native SwiftUI chat client for voizle-text-relay **v1**. It lives in this repo under `ios/`. One shared `MuseCore` package and one app target, `MuseIOS`, with destinations iOS, macOS, and visionOS. The product name is Muse. It is chat-only. It does not opt into durable join, pull, or ack, even when hello advertises `durable`.

Dot's arch sign-off at fa91786 covers the iOS chat-only wire. It does not cover this macOS and visionOS expansion. Destinations are already universal. macOS and visionOS shells beyond the shared window stay held until Dot and Fuse re-sign. Cupertino (the HIG review agent) has not reviewed this pass. The screens use system navigation, forms, lists, and materials. Visual polish is left for that review.

## Tasks

1. **Scaffold.** `ios/MuseIOS` is the single app target (`URLSessionWebSocketTask`). `ios/MuseCore` is the Swift package it links, declared for iOS 17, macOS 14, and visionOS 1. Supported platforms are iPhone, iPad, Mac, and visionOS. It is not Mac Catalyst and not a Designed-for-iPhone build. iOS and visionOS close the socket when the scene backgrounds and reconnect when it is active. macOS keeps the socket when you switch apps. The Mac window defaults to 960×680. The visionOS window defaults to 980×720. There is no ornament, volume, or immersive space.
2. **Connection.** The server speaks first. Hello must be `protocol: voizle-text-relay` and `v: 1`. Capability fields (`durable`, `durableVersion`, `replayLimit`, `limits`, and a `versions` list when one is present) are recorded. Advertised durable delivery does not opt the client in. A `versions` list does not change the join: it is always `{"v":1,"type":"join",...}`. A hello that is not v1 stops. The client does not send a hello of its own. Nick, room, and trip are checked against the hello limits before join. Over-limit values are not shortened and are not sent. Nick and an optional public trip are display metadata. A password, `pass`, or `nick#secret` is rejected and not sent.
3. **Chat.** The transcript keeps each chat's original `ts`. The composer shows a UTF-16 count against `limits.text` (2048 on the live relay, the same count as JavaScript `String.length`). Send is blocked when the text is empty or over the limit. Over-limit text can be split only after you review the parts and confirm. Nothing is cut down silently. A send is accepted only when a chat echo carries a UUID `id`. Transport success, `accepted`, and `delivery` are not acceptance. An echo without that id stays a gap and is not sent again. `@mentions` are plain chat text. There is no mention frame.
4. **Presence.** `type:presence` with `event` `join`, `leave`, or `nick` updates the people list. Shapes are in the fixture file below.
5. **Reconnect.** A drop before welcome rejoins with backoff (1s, doubling to 30s, jitter 0.8–1.2). Welcome resets the delay. The next step is hello, then a v1 join. `welcome.replay` is labeled `recentReplay`, keeps original timestamps, and shows coverage plus a connection-gap label. Dedup uses the chat UUID. A welcome with no replay list, or replay rows that are not chat, is labeled uncertain and those rows are not shown as messages. A send that never gets a UUID echo becomes a gap and is not sent again. A UUID replay of that text is labeled seen-in-replay and is not sent again. On iOS and visionOS, leaving the app closes the socket and cancels the backoff. It reconnects when the scene is active again. macOS does not close the socket on app switch.
6. **Apple Intelligence.** Session summaries are built from a bounded local context and are not stored or sent to the room. Replayed messages are excluded unless you turn Include replay on. Included replay lines carry their original time, coverage, and gap labels. The on-device model runs only when that OS (iOS 26, macOS 26, or visionOS 26), the Apple Intelligence runtime, and the language say it is available. Otherwise the sheet says chat is unchanged. The composer uses Writing Tools when the system offers them (iOS 18, macOS 15, visionOS 2). The App Intent takes a room id and does not carry messages, nicks, or trips.
7. **HIG.** The shared window is a split view: people on the side, transcript in the detail. A compact iPhone uses a people sheet. macOS and visionOS always use the split. Join is a form. The composer is inset. Cupertino reviews the polish. No separate Mac or visionOS shell is in this pass.

## Configure

The default socket is the public relay `wss://ws.voizel.com/relay`. Room, nick, and trip are blank in git.

```bash
cp ios/MuseIOS/Config/Local.xcconfig.example ios/MuseIOS/Config/Local.xcconfig
```

Edit `Local.xcconfig` on your machine. It is gitignored. Do not commit a private room name, trip password, owner secret, webhook, or token.

Open `ios/MuseIOS/MuseIOS.xcodeproj` and run the MuseIOS scheme. Pick an iOS, macOS, or visionOS destination. The built product is `Muse.app`.

## Fixtures

`ios/MuseCore/Tests/MuseCoreTests/Fixtures/frames.json`

Captured 2026-10-03. The hello object is the live hello (`v` 1, `durable` true, `durableVersion` 2, `replayLimit` 50, limits nick 24, room 64, trip 16, text 2048). Presence, `welcome`, `welcome.replay`, chat, `bye`, and `invalid_text` use the same key shapes. `helloDualVersion` is the designed versions list, not the live hello. `welcomeMissingReplay` and `welcomeUncertainReplay` cover a replay window that must be labeled instead of invented. Room names, session ids, and nicks are placeholders. Chat ids in the file are placeholder UUIDs.

## Tests

Linux CI runs the package, not an Apple simulator:

```bash
swift test --package-path ios/MuseCore
```

Workflow: `.github/workflows/ios-core.yml`.

## Apple follow-ups

These need a Mac with Xcode and are not required for this PR to merge. This environment did not compile the app target.

- Build and run the MuseIOS scheme for iOS, macOS, and visionOS. The on-device summary path needs the OS 26 SDK for that destination.
- Join a room you configure locally. Confirm hello, join, live chat, presence, and a rejoin where replay is labeled and not duplicated.
- On iOS and visionOS, background the scene and bring it back. An in-flight send should show as unconfirmed and should not be sent again. On macOS, switching apps should leave the socket up.
- Type past the character limit and confirm the review sheet is the only way those parts go out.
- On a destination without Apple Intelligence, the summary sheet stays on ordinary chat.
- Confirm Writing Tools on the composer where that OS offers them.
- Run the Open room shortcut and check that it only prefills a room id.
- Set a development team before installing on a device. This pass does not submit to the App Store.

## Out of v1

Durable join, pull, and ack. Owner secret and password entry. Appwrite or hivemind history. Workspace folders, cards, or a board. Pages or DigitalOcean. A socket that stays up while iOS or visionOS is in the background. Push. Voice and video. App Store submission. A v2 client path. macOS or visionOS UI shells beyond the shared window.
