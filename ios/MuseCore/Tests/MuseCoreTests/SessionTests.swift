import XCTest
@testable import MuseCore

final class SessionTests: XCTestCase {
    private let start = Date(timeIntervalSince1970: 1_790_994_300)

    func testDurableHelloStillJoinsV1() throws {
        var session = makeSession(trip: "Ab12Cd")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        let effects = session.receive(try frame("hello"), now: start)
        XCTAssertEqual(session.capabilities?.optedIntoDurable, false)
        XCTAssertTrue(session.capabilities?.durableAdvertised == true)
        let join = try sentObject(effects)
        XCTAssertEqual(WireJSON.int(join["v"]), 1)
        XCTAssertEqual(WireJSON.string(join["type"]), "join")
        XCTAssertEqual(WireJSON.string(join["trip"]), "!Ab12Cd")
        XCTAssertNil(join["pass"])
        XCTAssertNil(join["password"])

        _ = session.receive(try frame("welcomeReplay"), now: start)
        XCTAssertEqual(session.phase, .joined)
        XCTAssertEqual(session.users.map(\.nick), ["<nick-a>", "<nick>"])
        let replay = session.items.filter { $0.kind == .recentReplay }
        XCTAssertEqual(replay.count, 1)
        XCTAssertEqual(replay[0].text, "<text>")
        XCTAssertEqual(replay[0].originalUnixMilliseconds, 1_790_994_302_090)
        XCTAssertEqual(session.banner?.label, "recentReplay")
        XCTAssertEqual(session.banner?.connectionGapLabel, "connection gap: none in this session")
        XCTAssertTrue(session.banner?.coverageLabel.contains("1 of up to 50") == true)

        let again = session.receive(try frame("welcomeReplay"), now: start.addingTimeInterval(1))
        XCTAssertTrue(again.isEmpty)
        XCTAssertEqual(session.items.filter { $0.kind == .recentReplay }.count, 1)
        XCTAssertEqual(session.banner?.alreadyVisible, 1)
    }

    func testPresenceJoinLeaveNick() throws {
        var session = try joinedSession()
        _ = session.receive(try presence("join"), now: start)
        XCTAssertEqual(session.users.map(\.nick), ["<nick-a>", "<nick-b>"])
        XCTAssertTrue(session.items.contains { $0.kind == .presence && $0.text == "<nick-b> joined" })

        _ = session.receive(try presence("nick"), now: start)
        XCTAssertEqual(session.users.map(\.nick), ["<nick-a>", "<nick-c>"])
        XCTAssertTrue(session.items.contains { $0.text == "<nick-b> is now <nick-c>" })

        _ = session.receive(try presence("leave"), now: start)
        XCTAssertEqual(session.users.map(\.nick), ["<nick-a>"])
        XCTAssertTrue(session.items.contains { $0.text == "<nick-b> left" })
    }

    func testEchoIsDisplayReceiptAndDropDoesNotResend() throws {
        var session = try joinedSession()
        guard case .sent(let effects) = session.submit("hello room", now: start) else {
            return XCTFail("send blocked")
        }
        let outbound = try sentObject(effects)
        XCTAssertEqual(WireJSON.string(outbound["text"]), "hello room")
        XCTAssertNil(outbound["client_msg_id"])

        var echo = try fixtureObject("chat")
        echo["text"] = "hello room"
        echo["nick"] = "Muse"
        echo["id"] = "123E4567-E89B-12D3-A456-426614174000"
        let echoJSON = try XCTUnwrap(WireJSON.stringify(echo))
        _ = session.receive(echoJSON, now: start.addingTimeInterval(1))
        let mine = try XCTUnwrap(session.items.last { $0.mine && $0.text == "hello room" })
        XCTAssertEqual(mine.delivery, .relayAccepted)
        XCTAssertEqual(mine.receipt?.displayOnly, true)
        XCTAssertEqual(mine.receipt?.isTaskCompletion, false)
        XCTAssertEqual(mine.serverID, "123e4567-e89b-12d3-a456-426614174000")
        XCTAssertEqual(mine.originalUnixMilliseconds, 1_790_994_302_090)
        XCTAssertEqual(session.items.filter { $0.text == "hello room" }.count, 1)

        guard case .sent = session.submit("second", now: start.addingTimeInterval(2)) else {
            return XCTFail("second send blocked")
        }
        let closed = session.socketClosed(now: start.addingTimeInterval(5))
        XCTAssertTrue(closed.contains { if case .scheduleReconnect = $0 { return true }; return false })
        let gap = try XCTUnwrap(session.items.last { $0.text == "second" })
        XCTAssertEqual(gap.kind, .unconfirmedGap)
        XCTAssertEqual(gap.delivery, .unconfirmed)
        XCTAssertFalse(gap.receipt?.isTaskCompletion ?? true)

        guard case .openSocket = session.reconnectDue(now: start.addingTimeInterval(6)).last else {
            return XCTFail("expected reopen")
        }
        _ = session.socketOpened(now: start.addingTimeInterval(6))
        let rejoin = session.receive(try frame("hello"), now: start.addingTimeInterval(6))
        let join = try sentObject(rejoin)
        XCTAssertEqual(WireJSON.string(join["type"]), "join")
        XCTAssertEqual(WireJSON.int(join["v"]), 1)
        XCTAssertFalse(rejoin.contains { effect in
            guard case .send(let body, _) = effect else { return false }
            return body.contains("\"chat\"") || body.contains("\"pull\"") || body.contains("\"ack\"") || body.contains("\"v\":2")
        })
    }

    func testEchoWithoutUUIDStaysUnconfirmedAndIsNotResent() throws {
        var session = try joinedSession()
        _ = session.submit("hello room", now: start)
        var echo = try fixtureObject("chat")
        echo["text"] = "hello room"
        echo["nick"] = "Muse"
        echo["id"] = "<echo>"
        _ = session.receive(try XCTUnwrap(WireJSON.stringify(echo)), now: start.addingTimeInterval(1))
        let mine = try XCTUnwrap(session.items.last { $0.text == "hello room" })
        XCTAssertEqual(mine.kind, .unconfirmedGap)
        XCTAssertEqual(mine.delivery, .unconfirmed)
        XCTAssertNil(mine.serverID)
        XCTAssertEqual(session.items.filter { $0.text == "hello room" }.count, 1)

        _ = session.socketClosed(now: start.addingTimeInterval(2))
        _ = session.reconnectDue(now: start.addingTimeInterval(3))
        _ = session.socketOpened(now: start.addingTimeInterval(3))
        let rejoin = session.receive(try frame("hello"), now: start.addingTimeInterval(3))
        XCTAssertEqual(WireJSON.string(try sentObject(rejoin)["type"]), "join")
        XCTAssertFalse(rejoin.contains { effect in
            guard case .send(let body, _) = effect else { return false }
            return body.contains("\"chat\"")
        })
    }

    func testDurableAcceptDoesNotCompleteASend() throws {
        var session = try joinedSession()
        _ = session.submit("hello room", now: start)
        let accepted = session.receive(#"{"v":2,"type":"accepted","messageId":"123e4567-e89b-12d3-a456-426614174000"}"#, now: start)
        XCTAssertTrue(accepted.isEmpty)
        XCTAssertEqual(session.items.last { $0.text == "hello room" }?.delivery, .sending)
        _ = session.receive(#"{"v":2,"type":"delivery","deliveryId":"<delivery>","text":"hello room"}"#, now: start)
        XCTAssertEqual(session.items.last { $0.text == "hello room" }?.delivery, .sending)
        XCTAssertFalse(session.items.contains { $0.text == "hello room" && $0.kind == .liveChat && $0.delivery == .relayAccepted })
    }

    func testReplayBindsUnconfirmedSendWithoutResending() throws {
        var session = try joinedSession()
        _ = session.submit("from replay", now: start)
        _ = session.socketClosed(now: start.addingTimeInterval(4))
        _ = session.reconnectDue(now: start.addingTimeInterval(4))
        _ = session.socketOpened(now: start.addingTimeInterval(4))
        _ = session.receive(try frame("hello"), now: start.addingTimeInterval(4))

        var welcome = try fixtureObject("welcome")
        welcome["replay"] = [[
            "v": 1,
            "type": "chat",
            "id": "123E4567-E89B-12D3-A456-426614174000",
            "room": "<room>",
            "nick": "Muse",
            "trip": nil,
            "text": "from replay",
            "ts": 1_790_994_302_090,
            "source": "peer"
        ]]
        let raw = try XCTUnwrap(WireJSON.stringify(welcome))
        _ = session.receive(raw, now: start.addingTimeInterval(9))
        let rows = session.items.filter { $0.text == "from replay" }
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(rows[0].kind, .recentReplay)
        XCTAssertEqual(rows[0].serverID, "123e4567-e89b-12d3-a456-426614174000")
        XCTAssertEqual(rows[0].receipt?.caption, "Seen in replay. Not sent again.")
        XCTAssertEqual(rows[0].originalUnixMilliseconds, 1_790_994_302_090)
        XCTAssertEqual(session.banner?.connectionGapLabel, "connection gap: 5s")
        XCTAssertEqual(session.items.filter { $0.kind == .liveChat && $0.text == "from replay" }.count, 0)
    }

    func testCappedReplayCoverage() throws {
        var session = try joinedSession()
        _ = session.socketClosed(now: start)
        _ = session.reconnectDue(now: start)
        _ = session.socketOpened(now: start)
        _ = session.receive(try frame("hello"), now: start)
        var chats: [[String: Any]] = []
        for index in 0..<50 {
            chats.append([
                "v": 1,
                "type": "chat",
                "id": "id-\(index)",
                "room": "<room>",
                "nick": "other",
                "text": "line \(index)",
                "ts": 1_790_994_302_090,
                "source": "peer"
            ])
        }
        var welcome = try fixtureObject("welcome")
        welcome["replay"] = chats
        _ = session.receive(try XCTUnwrap(WireJSON.stringify(welcome)), now: start.addingTimeInterval(2))
        XCTAssertEqual(session.items.filter { $0.kind == .recentReplay }.count, 50)
        XCTAssertTrue(session.banner?.coverageLabel.contains("capped at 50") == true)
        XCTAssertEqual(session.items.filter { $0.kind == .recentReplay }.first?.originalUnixMilliseconds, 1_790_994_302_090)
    }

    func testBackgroundDoesNotKeepTheSocketOrResend() throws {
        var session = try joinedSession()
        _ = session.submit("while active", now: start)
        let effects = session.enterBackground(now: start.addingTimeInterval(1))
        XCTAssertTrue(effects.contains(.closeSocket))
        XCTAssertTrue(effects.contains(.cancelReconnect))
        XCTAssertFalse(effects.contains { if case .scheduleReconnect = $0 { return true }; return false })
        XCTAssertEqual(session.items.last { $0.text == "while active" }?.kind, .unconfirmedGap)
        XCTAssertTrue(session.enterForeground(now: start.addingTimeInterval(2)).contains { if case .openSocket = $0 { return true }; return false })
        _ = session.socketOpened(now: start.addingTimeInterval(2))
        let join = try sentObject(session.receive(try frame("hello"), now: start.addingTimeInterval(2)))
        XCTAssertEqual(WireJSON.string(join["type"]), "join")
        XCTAssertFalse(session.items.contains { $0.delivery == .sending })
    }

    func testDurableFramesAreNotAcked() throws {
        var session = try joinedSession()
        let before = session.items.count
        let effects = session.receive(#"{"v":2,"type":"delivery","deliveryId":"<delivery>","text":"hidden"}"#, now: start)
        XCTAssertTrue(effects.isEmpty)
        XCTAssertEqual(session.ignoredDurableFrames, 1)
        XCTAssertEqual(session.items.count, before + 1)
        XCTAssertFalse(session.items.contains { $0.text == "hidden" })
        let again = session.receive(#"{"type":"accepted","messageId":"<id>"}"#, now: start)
        XCTAssertTrue(again.isEmpty)
        XCTAssertEqual(session.ignoredDurableFrames, 2)
    }

    func testReplayWithoutUUIDLeavesTheGap() throws {
        var session = try joinedSession()
        _ = session.submit("from replay", now: start)
        _ = session.socketClosed(now: start.addingTimeInterval(4))
        _ = session.reconnectDue(now: start.addingTimeInterval(4))
        _ = session.socketOpened(now: start.addingTimeInterval(4))
        _ = session.receive(try frame("hello"), now: start.addingTimeInterval(4))

        var welcome = try fixtureObject("welcome")
        welcome["replay"] = [[
            "v": 1,
            "type": "chat",
            "id": "<replayed>",
            "room": "<room>",
            "nick": "Muse",
            "trip": nil,
            "text": "from replay",
            "ts": 1_790_994_302_090,
            "source": "peer"
        ]]
        _ = session.receive(try XCTUnwrap(WireJSON.stringify(welcome)), now: start.addingTimeInterval(9))
        let rows = session.items.filter { $0.text == "from replay" }
        XCTAssertEqual(rows.count, 2)
        XCTAssertEqual(rows.filter { $0.kind == .unconfirmedGap }.count, 1)
        XCTAssertEqual(rows.filter { $0.kind == .recentReplay }.count, 1)
        XCTAssertFalse(rows.contains { $0.delivery == .relayAccepted })
    }

    func testMissingReplayIsLabeledAndInventsNothing() throws {
        var session = makeSession(trip: "")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        _ = session.receive(try frame("hello"), now: start)
        _ = session.receive(try frame("welcomeMissingReplay"), now: start)
        XCTAssertEqual(session.phase, .joined)
        XCTAssertEqual(session.banner?.replayUncertain, true)
        XCTAssertEqual(session.banner?.coverageLabel, "coverage uncertain")
        XCTAssertTrue(session.banner?.uncertainLabel?.contains("no replay list") == true)
        XCTAssertEqual(session.items.filter { $0.kind == .recentReplay }.count, 0)
        XCTAssertTrue(session.items.contains { $0.kind == .system && $0.text.contains("no replay list") })
    }

    func testNonChatReplayRowsAreLabeledNotShown() throws {
        var session = makeSession(trip: "")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        _ = session.receive(try frame("hello"), now: start)
        _ = session.receive(try frame("welcomeUncertainReplay"), now: start)
        XCTAssertEqual(session.items.filter { $0.kind == .recentReplay }.count, 1)
        XCTAssertEqual(session.banner?.replayUncertain, true)
        XCTAssertEqual(session.banner?.uncertainLabel, "2 replay entries were not chat and not shown")
        XCTAssertTrue(session.banner?.coverageLabel.contains("1 of up to 50") == true)
        XCTAssertFalse(session.items.contains { $0.text == "<nick-b>" })
        XCTAssertFalse(session.items.contains { $0.text == "not-a-frame" })
    }

    func testDualVersionHelloStillJoinsV1() throws {
        var session = makeSession(trip: "Ab12Cd")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        let effects = session.receive(try frame("helloDualVersion"), now: start)
        let join = try sentObject(effects)
        XCTAssertEqual(WireJSON.int(join["v"]), 1)
        XCTAssertEqual(WireJSON.string(join["type"]), "join")
        XCTAssertEqual(effects.filter { if case .send = $0 { return true }; return false }.count, 1)
        XCTAssertFalse(effects.contains { effect in
            guard case .send(let body, _) = effect else { return false }
            return body.contains("\"v\":2") || body.contains("pull") || body.contains("ack") || body.contains("password")
        })
        XCTAssertEqual(session.capabilities?.optedIntoDurable, false)
        XCTAssertEqual(session.capabilities?.advertisedVersions, [2, 1])
        XCTAssertTrue(session.items.contains { $0.text.contains("dual-version") })
    }

    func testNonV1HelloStopsWithoutRetry() throws {
        var session = makeSession(trip: "")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        let effects = session.receive(#"{"v":2,"type":"hello","protocol":"voizle-text-relay","durable":true}"#, now: start)
        XCTAssertEqual(session.phase, .stopped)
        XCTAssertEqual(session.wantConnected, false)
        XCTAssertTrue(effects.contains(.closeSocket))
        XCTAssertTrue(effects.contains(.cancelReconnect))
        XCTAssertFalse(effects.contains { if case .scheduleReconnect = $0 { return true }; return false })
        XCTAssertFalse(effects.contains { if case .send = $0 { return true }; return false })
    }

    func testHelloLimitsRefuseJoinWithoutTruncating() throws {
        var session = makeSession(trip: "Ab12Cd")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        var hello = try fixtureObject("hello")
        var limits = try XCTUnwrap(hello["limits"] as? [String: Any])
        limits["nick"] = 3
        limits["trip"] = 6
        hello["limits"] = limits
        let effects = session.receive(try XCTUnwrap(WireJSON.stringify(hello)), now: start)
        XCTAssertEqual(session.phase, .stopped)
        XCTAssertFalse(effects.contains { if case .send = $0 { return true }; return false })
        XCTAssertFalse(effects.contains { if case .scheduleReconnect = $0 { return true }; return false })
        let notes = session.joinIssues.joined(separator: " ")
        XCTAssertTrue(notes.contains("not shortened"))
        XCTAssertFalse(notes.contains("Ab12Cd"))
        XCTAssertFalse(notes.contains("Muse"))
    }

    func testNickPasswordNeverConnectsOrEchoesTheSecret() {
        var session = makeSession(trip: "", nick: "name#secret")
        let effects = session.connect(now: start)
        XCTAssertEqual(session.phase, .stopped)
        XCTAssertFalse(effects.contains { if case .openSocket = $0 { return true }; return false })
        let notes = session.joinIssues.joined(separator: " ")
        XCTAssertTrue(notes.contains("password"))
        XCTAssertFalse(notes.contains("secret"))
        XCTAssertFalse(notes.contains("#"))
    }

    func testPinnedLimitBoundaries() {
        var overNick = makeSession(trip: "", nick: String(repeating: "n", count: 25))
        _ = overNick.connect(now: start)
        XCTAssertEqual(overNick.phase, .stopped)

        var overRoom = makeSession(trip: "", room: String(repeating: "r", count: 65))
        _ = overRoom.connect(now: start)
        XCTAssertEqual(overRoom.phase, .stopped)

        var exact = makeSession(trip: "Ab12Cd", nick: String(repeating: "n", count: 24), room: String(repeating: "r", count: 64))
        let effects = exact.connect(now: start)
        XCTAssertTrue(effects.contains { if case .openSocket = $0 { return true }; return false })
    }

    func testBackoffDoublesUntilWelcomeThenResets() throws {
        var session = try joinedSession()
        XCTAssertEqual(reconnectDelay(session.socketClosed(now: start)), 800)
        _ = session.reconnectDue(now: start.addingTimeInterval(1))
        _ = session.socketOpened(now: start.addingTimeInterval(1))
        XCTAssertEqual(reconnectDelay(session.socketClosed(now: start.addingTimeInterval(1))), 1600)
        _ = session.reconnectDue(now: start.addingTimeInterval(2))
        _ = session.socketOpened(now: start.addingTimeInterval(2))
        _ = session.receive(try frame("hello"), now: start.addingTimeInterval(2))
        _ = session.receive(try frame("welcome"), now: start.addingTimeInterval(2))
        XCTAssertEqual(session.phase, .joined)
        XCTAssertEqual(reconnectDelay(session.socketClosed(now: start.addingTimeInterval(3))), 800)
    }

    func testJoinSendFailureRetriesHelloWithoutChat() throws {
        var session = try joinedAwaitingWelcome(hasJoined: false)
        let effects = session.transportFailed(localID: "", now: start)
        XCTAssertTrue(effects.contains(.closeSocket))
        XCTAssertEqual(reconnectDelay(effects), 800)
        XCTAssertFalse(effects.contains { effect in
            guard case .send(let body, _) = effect else { return false }
            return body.contains("\"chat\"")
        })
        _ = session.reconnectDue(now: start.addingTimeInterval(1))
        _ = session.socketOpened(now: start.addingTimeInterval(1))
        let rejoin = session.receive(try frame("hello"), now: start.addingTimeInterval(1))
        XCTAssertEqual(WireJSON.string(try sentObject(rejoin)["type"]), "join")
        XCTAssertEqual(reconnectDelay(session.socketClosed(now: start.addingTimeInterval(2))), 1600)
    }

    func testSameChatUUIDIsOneRow() throws {
        var session = try joinedSession()
        let raw = try frame("chat")
        _ = session.receive(raw, now: start)
        _ = session.receive(raw, now: start.addingTimeInterval(1))
        XCTAssertEqual(session.items.filter { $0.kind == .liveChat && $0.serverID == "00000000-0000-4000-8000-000000000002" }.count, 1)
    }

    func testPasswordTripNeverConnects() {
        var session = makeSession(trip: "name#secret")
        let effects = session.connect(now: start)
        XCTAssertEqual(session.phase, .stopped)
        XCTAssertFalse(effects.contains { if case .openSocket = $0 { return true }; return false })
        XCTAssertTrue(session.joinIssues.contains { $0.contains("Passwords are not sent") })
    }

    func testInvalidNickStopsInsteadOfLooping() throws {
        var session = try joinedAwaitingWelcome(hasJoined: false)
        let effects = session.receive(#"{"v":1,"type":"error","code":"invalid_nick","text":"bad nick"}"#, now: start)
        XCTAssertEqual(session.phase, .stopped)
        XCTAssertEqual(session.wantConnected, false)
        XCTAssertTrue(effects.contains(.closeSocket))
        XCTAssertFalse(effects.contains { if case .scheduleReconnect = $0 { return true }; return false })
    }

    private func makeSession(trip: String, nick: String = "Muse", room: String = "<room>") -> ChatSession {
        ChatSession(
            identity: JoinIdentity(
                relayURL: ClientSettings.defaultRelayURL,
                room: room,
                nick: nick,
                publicTrip: trip
            ),
            jitterUnit: 0
        )
    }

    private func reconnectDelay(_ effects: [SessionEffect], file: StaticString = #filePath, line: UInt = #line) -> Int {
        for effect in effects {
            if case .scheduleReconnect(let milliseconds) = effect { return milliseconds }
        }
        XCTFail("no reconnect scheduled", file: file, line: line)
        return -1
    }

    private func joinedSession() throws -> ChatSession {
        var session = makeSession(trip: "")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        _ = session.receive(try frame("hello"), now: start)
        _ = session.receive(try frame("welcome"), now: start)
        return session
    }

    private func joinedAwaitingWelcome(hasJoined: Bool) throws -> ChatSession {
        var session = makeSession(trip: "")
        _ = session.connect(now: start)
        _ = session.socketOpened(now: start)
        _ = session.receive(try frame("hello"), now: start)
        if hasJoined {
            _ = session.receive(try frame("welcome"), now: start)
        }
        return session
    }

    private func frame(_ key: String) throws -> String {
        let object = try fixtureObject(key)
        return try XCTUnwrap(WireJSON.stringify(object))
    }

    private func presence(_ event: String) throws -> String {
        let presence = try XCTUnwrap(try fixture()["presence"] as? [String: Any])
        let object = try XCTUnwrap(presence[event] as? [String: Any])
        return try XCTUnwrap(WireJSON.stringify(object))
    }

    private func sentObject(_ effects: [SessionEffect]) throws -> [String: Any] {
        for effect in effects {
            if case .send(let body, _) = effect {
                return try XCTUnwrap(WireJSON.object(from: body))
            }
        }
        XCTFail("no send effect")
        return [:]
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

final class ComposerAndSummaryTests: XCTestCase {
    func testUTF16LimitBlocksSendAndSplitIsExplicit() {
        let emoji = String(repeating: "😀", count: 1024)
        XCTAssertEqual(Composer.utf16Count(emoji), 2048)
        XCTAssertTrue(Composer.state(text: emoji, limit: 2048).canSend)
        XCTAssertFalse(Composer.state(text: emoji + "😀", limit: 2048).canSend)

        let over = String(repeating: "a", count: 2049)
        let state = Composer.state(text: over, limit: 2048)
        XCTAssertFalse(state.canSend)
        XCTAssertTrue(state.offersReviewSplit)
        XCTAssertEqual(state.count, 2049)
        let chunks = Composer.split(text: over, limit: 2048)
        XCTAssertEqual(chunks.count, 2)
        XCTAssertEqual(chunks.joined(), over)
        XCTAssertTrue(chunks.allSatisfy { Composer.utf16Count($0) <= 2048 })

        var session = ChatSession(
            identity: JoinIdentity(
                relayURL: ClientSettings.defaultRelayURL,
                room: "<room>",
                nick: "Muse",
                publicTrip: ""
            )
        )
        if case .sent = session.submit(over, now: Date()) {
            XCTFail("over-limit text must not send")
        } else if case .needsReview(let review) = session.submit(over, now: Date()) {
            XCTAssertEqual(review.joined(), over)
        } else {
            XCTFail("expected review")
        }
        XCTAssertTrue(session.items.filter { $0.kind == .liveChat }.isEmpty)
    }

    func testConfirmedSplitSendsEachPartAndDropDoesNotResend() throws {
        var session = ChatSession(
            identity: JoinIdentity(
                relayURL: ClientSettings.defaultRelayURL,
                room: "<room>",
                nick: "Muse",
                publicTrip: ""
            ),
            jitterUnit: 0
        )
        let now = Date(timeIntervalSince1970: 1_790_994_300)
        _ = session.connect(now: now)
        _ = session.socketOpened(now: now)
        let hello = try fixtureString("hello")
        _ = session.receive(hello, now: now)
        _ = session.receive(try fixtureString("welcome"), now: now)

        let text = String(repeating: "b", count: 3000)
        guard case .sent(let effects) = session.confirmReviewedSplit(Composer.split(text: text, limit: 2048), now: now) else {
            return XCTFail("split was not confirmed")
        }
        let bodies = effects.compactMap { effect -> String? in
            if case .send(let body, _) = effect { return body }
            return nil
        }
        XCTAssertEqual(bodies.count, 2)
        XCTAssertEqual(session.items.filter { $0.delivery == .sending }.count, 2)
        _ = session.socketClosed(now: now.addingTimeInterval(2))
        XCTAssertEqual(session.items.filter { $0.kind == .unconfirmedGap }.count, 2)
        _ = session.reconnectDue(now: now.addingTimeInterval(3))
        _ = session.socketOpened(now: now.addingTimeInterval(3))
        let rejoin = session.receive(hello, now: now.addingTimeInterval(3))
        XCTAssertFalse(rejoin.contains { effect in
            guard case .send(let body, _) = effect else { return false }
            return body.contains("\"chat\"")
        })
    }

    func testSummarySkipsReplayUnlessAskedAndStaysBounded() throws {
        var session = ChatSession(
            identity: JoinIdentity(
                relayURL: ClientSettings.defaultRelayURL,
                room: "<room>",
                nick: "Muse",
                publicTrip: ""
            )
        )
        let now = Date(timeIntervalSince1970: 1_790_994_300)
        _ = session.connect(now: now)
        _ = session.socketOpened(now: now)
        _ = session.receive(try fixtureString("hello"), now: now)
        _ = session.receive(try fixtureString("welcomeReplay"), now: now)
        _ = session.submit("live line", now: now)
        let echo = try XCTUnwrap(WireJSON.stringify([
            "v": 1,
            "type": "chat",
            "id": "123e4567-e89b-12d3-a456-426614174000",
            "room": "<room>",
            "nick": "Muse",
            "text": "live line",
            "ts": 1_790_994_302_090
        ]))
        _ = session.receive(echo, now: now)
        _ = session.submit("unconfirmed line", now: now)
        _ = session.socketClosed(now: now.addingTimeInterval(1))
        let hidden = SummaryBuilder.make(items: session.items, includeReplay: false, maxMessages: 30, maxCharacters: 4000)
        XCTAssertEqual(hidden.lines.map(\.text), ["live line"])
        XCTAssertEqual(hidden.replayExcluded, 1)
        XCTAssertTrue(hidden.sessionOnly)
        XCTAssertTrue(hidden.prompt.contains("not saved"))
        XCTAssertFalse(hidden.prompt.contains("<text>"))
        XCTAssertFalse(hidden.prompt.contains("unconfirmed"))

        let included = SummaryBuilder.make(items: session.items, includeReplay: true)
        XCTAssertEqual(included.lines.map(\.origin), ["recentReplay", "live"])
        XCTAssertEqual(included.lines[0].originalTimeLabel, OriginalTime.label(unixMilliseconds: 1_790_994_302_090))
        XCTAssertNotNil(included.lines[0].coverageLabel)
        XCTAssertNotNil(included.lines[0].connectionGapLabel)
        XCTAssertTrue(included.prompt.contains("recentReplay"))

        let bounded = SummaryBuilder.make(items: session.items, includeReplay: false, maxMessages: 1, maxCharacters: 4)
        XCTAssertTrue(bounded.lines.isEmpty)
        XCTAssertEqual(bounded.omittedByBound, 1)
    }

    func testIntelligenceGateFallsBackToOrdinaryChat() {
        let ready = IntelligenceGate.decide(osSupportsOnDeviceModel: true, runtimeAvailable: true, languageSupported: true)
        XCTAssertFalse(ready.usesOrdinaryChat)
        let oldOS = IntelligenceGate.decide(osSupportsOnDeviceModel: false, runtimeAvailable: false, languageSupported: false)
        XCTAssertTrue(oldOS.usesOrdinaryChat)
        XCTAssertNotNil(oldOS.unavailableReason)
        let language = IntelligenceGate.decide(osSupportsOnDeviceModel: true, runtimeAvailable: true, languageSupported: false)
        XCTAssertTrue(language.usesOrdinaryChat)
    }

    private func fixtureString(_ key: String) throws -> String {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .appendingPathComponent("Fixtures/frames.json")
        let data = try Data(contentsOf: url)
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let object = try XCTUnwrap(root[key] as? [String: Any])
        return try XCTUnwrap(WireJSON.stringify(object))
    }
}
