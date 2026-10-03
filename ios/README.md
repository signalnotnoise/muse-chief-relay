# Muse for iOS (v1)

Native SwiftUI chat client for voizle-text-relay **v1**. It lives in this repo under `ios/`. It is chat-only. It does not opt into durable join, pull, or ack, even when hello advertises `durable`.

Cupertino (the HIG review agent) has not reviewed this pass. The screens use system navigation, forms, lists, and materials. Visual polish is left for that review.

## Tasks

1. **Scaffold.** `ios/MuseIOS` is the app. `ios/MuseCore` is the Swift package the app links. The socket is `URLSessionWebSocketTask` and stays in the foreground.
2. **Connection.** The server speaks first. Hello must be `protocol: voizle-text-relay` and `v: 1`. Capability fields (`durable`, `durableVersion`, `replayLimit`, `limits`) are recorded. The join is always `{"v":1,"type":"join",...}`. Nick and an optional public trip are display metadata. A password, `pass`, or `nick#secret` is rejected and not sent.
3. **Chat.** The transcript keeps each chat's original `ts`. The composer shows a UTF-16 count against `limits.text` (2048 on the live relay, the same count as JavaScript `String.length`). Send is blocked when the text is empty or over the limit. Over-limit text can be split only after you review the parts and confirm. Nothing is cut down silently.
4. **Presence.** `type:presence` with `event` `join`, `leave`, or `nick` updates the people list. Shapes are in the fixture file below.
5. **Reconnect.** A drop rejoins with backoff (1s, doubling to 30s, jitter). `welcome.replay` is labeled `recentReplay`, keeps original timestamps, and shows coverage plus a connection-gap label. Chat ids dedup (`id`, otherwise a hash). A send that never gets an echo becomes a gap and is not sent again. Leaving the app closes the socket. It reconnects when the app is active again.
6. **Apple Intelligence.** Session summaries are built from a bounded local context and are not stored or sent to the room. Replayed messages are excluded unless you turn Include replay on. Included replay lines carry their original time, coverage, and gap labels. The on-device model runs only when the OS, the Apple Intelligence runtime, and the language say it is available. Otherwise the sheet says chat is unchanged. The composer uses Writing Tools on iOS 18 and later, when the system offers them. The App Intent takes a room id and does not carry messages, nicks, or trips.
7. **HIG.** Standard split view on a wide size class, a people sheet on a compact phone, a form to join, and a composer inset. Cupertino reviews the polish.

## Configure

The default socket is the public relay `wss://ws.voizel.com/relay`. Room, nick, and trip are blank in git.

```bash
cp ios/MuseIOS/Config/Local.xcconfig.example ios/MuseIOS/Config/Local.xcconfig
```

Edit `Local.xcconfig` on your machine. It is gitignored. Do not commit a private room name, trip password, owner secret, webhook, or token.

Open `ios/MuseIOS/MuseIOS.xcodeproj` and run the MuseIOS scheme.

## Fixtures

`ios/MuseCore/Tests/MuseCoreTests/Fixtures/frames.json`

Captured 2026-10-03. The hello object is the live hello (`v` 1, `durable` true, `durableVersion` 2, `replayLimit` 50, `limits.text` 2048). Presence, `welcome`, `welcome.replay`, chat, `bye`, and `invalid_text` use the same key shapes. Room names, session ids, nicks, and message ids in that file are placeholders.

## Tests

Linux CI runs the package, not the simulator:

```bash
swift test --package-path ios/MuseCore
```

Workflow: `.github/workflows/ios-core.yml`.

## Mac follow-ups

These need a Mac and are not required for this PR to merge:

- Build and run MuseIOS in the simulator (Xcode with the iOS 26 SDK for the on-device model path).
- Join a room you configure locally. Confirm hello, join, live chat, presence, and a rejoin where replay is labeled and not duplicated.
- Background the app and bring it back. An in-flight send should show as unconfirmed and should not be sent again.
- Type past the character limit and confirm the review sheet is the only way those parts go out.
- On a device or simulator without Apple Intelligence, the summary sheet stays on ordinary chat.
- On iOS 18 or later, confirm Writing Tools on the composer.
- Run the Open room shortcut and check that it only prefills a room id.
- Set a development team before installing on a device. This pass does not submit to the App Store.

## Out of v1

Durable join, pull, and ack. Owner secret and password entry. Appwrite history. Workspace folders. A socket that stays up in the background. Push. Voice and video. App Store submission. A v2 or dual-version hello as the client path.
