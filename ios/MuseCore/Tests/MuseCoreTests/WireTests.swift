import XCTest
@testable import MuseCore

final class WireTests: XCTestCase {
    func testSHA256KnownVector() {
        XCTAssertEqual(
            SHA256.hex("abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        )
    }

    func testHelloFixtureKeepsV1AndDoesNotOptIn() throws {
        let hello = try fixtureObject("hello")
        guard case .accepted(let caps) = HelloFrame.parse(hello) else {
            return XCTFail("hello rejected")
        }
        XCTAssertTrue(caps.durableAdvertised)
        XCTAssertEqual(caps.durableVersion, 2)
        XCTAssertEqual(caps.replayLimit, 50)
        XCTAssertEqual(caps.textLimit, 2048)
        XCTAssertEqual(caps.nickLimit, 24)
        XCTAssertEqual(caps.roomLimit, 64)
        XCTAssertEqual(caps.tripLimit, 16)
        XCTAssertFalse(caps.versionsFieldPresent)
        XCTAssertEqual(caps.advertisedVersions, [])
        XCTAssertFalse(caps.optedIntoDurable)
    }

    func testDualVersionHelloIsRecordedAndNotASecondProtocol() throws {
        let hello = try fixtureObject("helloDualVersion")
        guard case .accepted(let caps) = HelloFrame.parse(hello) else {
            return XCTFail("dual-version hello rejected")
        }
        XCTAssertTrue(caps.versionsFieldPresent)
        XCTAssertEqual(caps.advertisedVersions, [2, 1])
        XCTAssertFalse(caps.optedIntoDurable)
        XCTAssertEqual(WireJSON.int(hello["v"]), 1)
    }

    func testWrongHelloVersionStops() {
        let wrong = HelloFrame.parse(["v": 2, "type": "hello", "protocol": "voizle-text-relay"])
        guard case .stop = wrong else { return XCTFail("expected stop") }
        let other = HelloFrame.parse(["v": 1, "type": "welcome"])
        guard case .retry = other else { return XCTFail("expected retry") }
    }

    func testJoinIsV1DisplayTripOnly() throws {
        XCTAssertEqual(PublicTrip.decide(""), .omit)
        XCTAssertEqual(PublicTrip.decide("  Ab12Cd "), .send("!Ab12Cd"))
        XCTAssertEqual(PublicTrip.decide("!Ab12Cd"), .send("!Ab12Cd"))
        XCTAssertEqual(PublicTrip.decide("hunter2"), .reject)
        XCTAssertEqual(PublicTrip.decide("name#secret"), .reject)

        let frame = ClientFrame.join(room: "<room>", nick: "<nick>", trip: "!Ab12Cd")
        let json = try XCTUnwrap(ClientFrame.json(frame))
        let object = try XCTUnwrap(WireJSON.object(from: json))
        XCTAssertEqual(WireJSON.int(object["v"]), 1)
        XCTAssertEqual(WireJSON.string(object["type"]), "join")
        XCTAssertEqual(WireJSON.string(object["trip"]), "!Ab12Cd")
        XCTAssertNil(object["password"])
        XCTAssertNil(object["pass"])
        XCTAssertNil(object["client_msg_id"])
        XCTAssertNil(object["inboxAuth"])

        let omitted = ClientFrame.join(room: "<room>", nick: "<nick>", trip: nil)
        XCTAssertNil(omitted["trip"])
        XCTAssertNil(omitted["password"])
    }

    func testChatFrameIsTextOnly() throws {
        let json = try XCTUnwrap(ClientFrame.json(ClientFrame.chat(text: "hello")))
        let object = try XCTUnwrap(WireJSON.object(from: json))
        XCTAssertEqual(WireJSON.int(object["v"]), 1)
        XCTAssertEqual(WireJSON.string(object["type"]), "chat")
        XCTAssertEqual(WireJSON.string(object["text"]), "hello")
        XCTAssertNil(object["client_msg_id"])
    }

    func testPresenceFixturesHaveJoinLeaveNick() throws {
        let presence = try fixture()["presence"] as? [String: Any]
        for event in ["join", "leave", "nick"] {
            let frame = try XCTUnwrap(presence?[event] as? [String: Any])
            XCTAssertEqual(WireJSON.string(frame["type"]), "presence")
            XCTAssertEqual(WireJSON.string(frame["event"]), event)
            XCTAssertNotNil(frame["users"])
        }
        let nick = try XCTUnwrap(presence?["nick"] as? [String: Any])
        XCTAssertEqual(WireJSON.string(nick["previousNick"]), "<nick-b>")
    }

    func testWelcomeReplayFixtureKeepsOriginalTimestamp() throws {
        let welcome = try fixtureObject("welcomeReplay")
        let replay = try XCTUnwrap(WireJSON.array(welcome["replay"]))
        let chat = try XCTUnwrap(replay.first as? [String: Any])
        XCTAssertEqual(WireJSON.string(chat["type"]), "chat")
        XCTAssertEqual(WireJSON.string(chat["id"]), "00000000-0000-4000-8000-000000000001")
        XCTAssertNotNil(ChatIdentity.canonicalServerID(WireJSON.string(chat["id"])))
        XCTAssertEqual(WireJSON.int64(chat["ts"]), 1_790_994_302_090)
        XCTAssertEqual(WireJSON.string(chat["source"]), "peer")
        let label = OriginalTime.label(unixMilliseconds: 1_790_994_302_090)
        XCTAssertTrue(label.hasPrefix("2026-"))
        XCTAssertFalse(label == "time unknown")
    }

    func testServerIDDedupKeyIgnoresText() {
        let id = "123E4567-E89B-12D3-A456-426614174000"
        let a = ChatIdentity.key(room: "<room>", nick: "a", trip: nil, unixMilliseconds: 1, text: "one", serverID: id)
        let b = ChatIdentity.key(room: "<room>", nick: "a", trip: nil, unixMilliseconds: 1, text: "two", serverID: id.lowercased())
        XCTAssertEqual(a, "srv:" + id.lowercased())
        XCTAssertEqual(a, b)
        let notUUID = ChatIdentity.key(room: "<room>", nick: "a", trip: nil, unixMilliseconds: 1, text: "one", serverID: "same")
        XCTAssertTrue(notUUID.hasPrefix("msg:"))
        XCTAssertNotEqual(notUUID, a)
        let hashed = ChatIdentity.key(room: "<room>", nick: "a", trip: "!Ab12Cd", unixMilliseconds: 5, text: "one", serverID: nil)
        XCTAssertTrue(hashed.hasPrefix("msg:"))
        XCTAssertNotEqual(hashed, ChatIdentity.key(room: "<room>", nick: "a", trip: "!Ab12Cd", unixMilliseconds: 5, text: "two", serverID: nil))
    }

    func testSettingsRejectSecretsInURLAndDefaultThePublicRelay() {
        let blank = ClientSettings.load(relayURL: "  ", room: "", nick: "", publicTrip: "")
        XCTAssertEqual(blank.relayURL, ClientSettings.defaultRelayURL)
        XCTAssertNil(blank.urlError)

        let secret = ClientSettings.load(relayURL: "wss://user:pass@example.com/relay?token=1", room: "", nick: "", publicTrip: "")
        XCTAssertNil(secret.relayURL)
        XCTAssertNotNil(secret.urlError)

        let ok = ClientSettings.load(relayURL: "wss://ws.voizel.com/relay", room: "  ", nick: " Muse ", publicTrip: " Ab12Cd ")
        XCTAssertEqual(ok.relayURL?.absoluteString, "wss://ws.voizel.com/relay")
        XCTAssertEqual(ok.nick, "Muse")
        XCTAssertEqual(ok.room, "")
    }

    func testRoomFocusIsRoomIDOnly() throws {
        let data = try JSONEncoder().encode(RoomFocus(roomID: "<room>"))
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        XCTAssertEqual(Set(object?.keys.map { $0 } ?? []), ["roomID"])
    }

    private func fixture() throws -> [String: Any] {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .appendingPathComponent("Fixtures/frames.json")
        let data = try Data(contentsOf: url)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func fixtureObject(_ key: String) throws -> [String: Any] {
        try XCTUnwrap(try fixture()[key] as? [String: Any])
    }
}
