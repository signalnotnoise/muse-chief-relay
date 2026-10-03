import Foundation

public struct JoinIdentity: Equatable, Sendable {
    public var relayURL: URL
    public var room: String
    public var nick: String
    public var publicTrip: String

    public init(relayURL: URL, room: String, nick: String, publicTrip: String) {
        self.relayURL = relayURL
        self.room = room
        self.nick = nick
        self.publicTrip = publicTrip
    }
}

public enum Phase: Equatable, Sendable {
    case idle
    case connecting
    case awaitingHello
    case awaitingWelcome
    case joined
    case waitingToReconnect
    case stopped
}

public enum DeliveryDisplay: Equatable, Sendable {
    case notMine
    case sending
    case relayAccepted
    case unconfirmed
    case rejected
}

public struct RelayReceipt: Equatable, Sendable {
    public var displayOnly: Bool
    public var isTaskCompletion: Bool
    public var caption: String

    public static let accepted = RelayReceipt(
        displayOnly: true,
        isTaskCompletion: false,
        caption: "Relay accepted. Not a completed task."
    )
}

public enum TranscriptKind: Equatable, Sendable {
    case liveChat
    case recentReplay
    case presence
    case system
    case unconfirmedGap
}

public struct TranscriptItem: Identifiable, Equatable, Sendable {
    public var id: String
    public var kind: TranscriptKind
    public var nick: String
    public var text: String
    public var trip: String?
    public var serverID: String?
    public var originalUnixMilliseconds: Int64?
    public var originalTimeLabel: String
    public var coverageLabel: String?
    public var connectionGapLabel: String?
    public var receipt: RelayReceipt?
    public var delivery: DeliveryDisplay
    public var mine: Bool
}

public struct PresenceUser: Identifiable, Equatable, Sendable {
    public var sessionID: String
    public var nick: String
    public var trip: String?
    public var id: String { sessionID.isEmpty ? nick : sessionID }
}

public struct ReplayBanner: Equatable, Sendable {
    public var label: String
    public var shown: Int
    public var received: Int
    public var alreadyVisible: Int
    public var coverageLabel: String
    public var connectionGapLabel: String
}

public enum SessionEffect: Equatable, Sendable {
    case openSocket(URL)
    case send(text: String, localID: String)
    case closeSocket
    case scheduleReconnect(milliseconds: Int)
    case cancelReconnect
}

public enum SubmitStatus: Equatable, Sendable {
    case sent([SessionEffect])
    case blocked(String)
    case needsReview([String])
}

public struct ChatSession: Equatable, Sendable {
    public private(set) var identity: JoinIdentity
    public private(set) var phase: Phase
    public private(set) var capabilities: HelloCapabilities?
    public private(set) var users: [PresenceUser]
    public private(set) var items: [TranscriptItem]
    public private(set) var banner: ReplayBanner?
    public private(set) var ignoredDurableFrames: Int
    public private(set) var textLimit: Int
    public private(set) var wantConnected: Bool
    public private(set) var statusText: String
    public private(set) var joinIssues: [String]
    public private(set) var hasJoinedOnce: Bool

    private var pending: [String]
    private var seenKeys: Set<String>
    private var backoffSeconds: Int
    private var firstJoinRejects: Int
    private var jitterUnit: Double
    private var connectedAt: Date?
    private var lastCloseAt: Date?
    private var inBackground: Bool
    private var notedDurableSkip: Bool
    private var notedDurableAdvertisement: Bool
    private var wireTrip: String?

    public init(identity: JoinIdentity, jitterUnit: Double = 0) {
        self.identity = JoinIdentity(
            relayURL: identity.relayURL,
            room: identity.room.trimmingCharacters(in: .whitespacesAndNewlines),
            nick: identity.nick.trimmingCharacters(in: .whitespacesAndNewlines),
            publicTrip: identity.publicTrip
        )
        phase = .idle
        capabilities = nil
        users = []
        items = []
        banner = nil
        ignoredDurableFrames = 0
        textLimit = HelloCapabilities.fallbackTextLimit
        wantConnected = false
        statusText = "Disconnected"
        joinIssues = []
        hasJoinedOnce = false
        pending = []
        seenKeys = []
        backoffSeconds = 1
        firstJoinRejects = 0
        self.jitterUnit = min(1, max(0, jitterUnit))
        connectedAt = nil
        lastCloseAt = nil
        inBackground = false
        notedDurableSkip = false
        notedDurableAdvertisement = false
        wireTrip = nil
    }

    public func composerState(for text: String) -> ComposerState {
        Composer.state(text: text, limit: textLimit)
    }

    public func reviewSplit(_ text: String) -> [String] {
        Composer.split(text: text, limit: textLimit)
    }

    public mutating func connect(now: Date) -> [SessionEffect] {
        joinIssues = validate()
        if !joinIssues.isEmpty {
            phase = .stopped
            wantConnected = false
            statusText = joinIssues.joined(separator: " ")
            return [.cancelReconnect, .closeSocket]
        }
        if case .send(let trip) = PublicTrip.decide(identity.publicTrip) {
            wireTrip = trip
        } else {
            wireTrip = nil
        }
        wantConnected = true
        inBackground = false
        firstJoinRejects = 0
        backoffSeconds = 1
        hasJoinedOnce = false
        items = []
        users = []
        pending = []
        seenKeys = []
        banner = nil
        ignoredDurableFrames = 0
        notedDurableSkip = false
        notedDurableAdvertisement = false
        return open(now: now, status: "Connecting…")
    }

    public mutating func socketOpened(now: Date) -> [SessionEffect] {
        guard wantConnected, phase == .connecting else { return [] }
        phase = .awaitingHello
        statusText = "Waiting for hello…"
        return []
    }

    public mutating func socketClosed(now: Date) -> [SessionEffect] {
        guard phase == .connecting || phase == .awaitingHello || phase == .awaitingWelcome || phase == .joined else {
            return []
        }
        let wasJoined = phase == .joined || hasJoinedOnce
        markPendingUnconfirmed(caption: "Not confirmed. This was not sent again.")
        lastCloseAt = now
        connectedAt = nil
        phase = .idle
        guard wantConnected, !inBackground else {
            statusText = inBackground ? "Paused. Reconnects when Muse is active." : "Disconnected"
            return []
        }
        let delay = nextDelay(confirmed: wasJoined, uptime: 0)
        phase = .waitingToReconnect
        statusText = "Reconnecting…"
        return [.scheduleReconnect(milliseconds: delay)]
    }

    public mutating func receive(_ json: String, now: Date) -> [SessionEffect] {
        guard let object = WireJSON.object(from: json) else {
            appendSystem("Unreadable frame.")
            return []
        }
        let type = WireJSON.string(object["type"]) ?? ""
        if phase == .awaitingHello {
            switch HelloFrame.parse(object) {
            case .accepted(let hello):
                capabilities = hello
                textLimit = max(1, hello.textLimit)
                if hello.durableAdvertised, !notedDurableAdvertisement {
                    notedDurableAdvertisement = true
                    appendSystem("Server advertised durable delivery. This client stays on v1 chat and does not opt in.")
                }
                phase = .awaitingWelcome
                statusText = "Joining…"
                if let frame = ClientFrame.json(ClientFrame.join(room: identity.room, nick: identity.nick, trip: wireTrip)) {
                    return [.send(text: frame, localID: "")]
                }
                return []
            case .rejected(let reason):
                appendSystem("Hello was not accepted (\(reason)).")
                phase = .awaitingHello
                return failAndRetry(now: now, confirmed: false)
            }
        }

        if durableTypes.contains(type) {
            ignoredDurableFrames += 1
            if !notedDurableSkip {
                notedDurableSkip = true
                appendSystem("Ignored a durable frame. v1 chat does not pull or ack.")
            }
            return []
        }

        switch type {
        case "hello", "pong", "ping":
            return []
        case "welcome":
            return applyWelcome(object, now: now)
        case "presence":
            applyPresence(object)
            return []
        case "chat":
            applyChat(object, origin: .live, now: now)
            return []
        case "error":
            return applyError(object, now: now)
        case "bye":
            let reason = WireJSON.string(object["reason"]) ?? "leave"
            appendSystem(reason == "replaced" ? "This connection was replaced." : "Left the room.")
            return []
        default:
            appendSystem("Ignored a \(type.isEmpty ? "frame" : type) frame.")
            return []
        }
    }

    public mutating func submit(_ text: String, now: Date) -> SubmitStatus {
        let state = composerState(for: text)
        if state.offersReviewSplit {
            return .needsReview(reviewSplit(text))
        }
        if !state.canSend {
            return .blocked(state.blockedReason ?? "Can't send")
        }
        return sendOne(text, now: now)
    }

    public mutating func confirmReviewedSplit(_ chunks: [String], now: Date) -> SubmitStatus {
        if chunks.isEmpty { return .blocked("Nothing to send") }
        if phase != .joined { return .blocked("Not connected. Nothing was sent.") }
        for chunk in chunks {
            let state = composerState(for: chunk)
            if !state.canSend {
                return .blocked(state.blockedReason ?? "A reviewed part is over the limit. Nothing was sent.")
            }
        }
        var effects: [SessionEffect] = []
        for chunk in chunks {
            if case .sent(let sent) = sendOne(chunk, now: now) {
                effects.append(contentsOf: sent)
            }
        }
        return .sent(effects)
    }

    public mutating func transportFailed(localID: String, now: Date) {
        markUnconfirmed(localID: localID, caption: "The relay did not take this. It was not sent again.")
    }

    public mutating func enterBackground(now: Date) -> [SessionEffect] {
        inBackground = true
        markPendingUnconfirmed(caption: "Not confirmed. This was not sent again.")
        lastCloseAt = now
        let shouldClose = phase == .connecting || phase == .awaitingHello || phase == .awaitingWelcome || phase == .joined
        phase = wantConnected ? .idle : phase
        connectedAt = nil
        statusText = wantConnected ? "Paused. Reconnects when Muse is active." : statusText
        var effects: [SessionEffect] = [.cancelReconnect]
        if shouldClose { effects.append(.closeSocket) }
        return effects
    }

    public mutating func enterForeground(now: Date) -> [SessionEffect] {
        inBackground = false
        guard wantConnected else { return [] }
        guard phase == .idle || phase == .waitingToReconnect || phase == .stopped else { return [] }
        if phase == .stopped { return [] }
        backoffSeconds = 1
        return open(now: now, status: "Reconnecting…")
    }

    public mutating func disconnect(now: Date) -> [SessionEffect] {
        wantConnected = false
        inBackground = false
        markPendingUnconfirmed(caption: "Not confirmed. This was not sent again.")
        phase = .stopped
        statusText = "Disconnected"
        connectedAt = nil
        return [.cancelReconnect, .closeSocket]
    }

    public mutating func reconnectDue(now: Date) -> [SessionEffect] {
        guard wantConnected, !inBackground, phase == .waitingToReconnect else { return [] }
        return open(now: now, status: "Reconnecting…")
    }

    private mutating func open(now: Date, status: String) -> [SessionEffect] {
        phase = .connecting
        statusText = status
        users = []
        return [.cancelReconnect, .openSocket(identity.relayURL)]
    }

    private func validate() -> [String] {
        var issues: [String] = []
        if identity.room.isEmpty {
            issues.append("Room is required.")
        } else if Composer.utf16Count(identity.room) > HelloCapabilities.fallbackRoomLimit {
            issues.append("Room is longer than \(HelloCapabilities.fallbackRoomLimit) characters. It was not shortened.")
        }
        if identity.nick.isEmpty {
            issues.append("Nick is required.")
        } else if identity.nick.contains("#") {
            issues.append("This client does not take a password. Use a nick on its own.")
        } else if Composer.utf16Count(identity.nick) > HelloCapabilities.fallbackNickLimit {
            issues.append("Nick is longer than \(HelloCapabilities.fallbackNickLimit) characters. It was not shortened.")
        }
        switch PublicTrip.decide(identity.publicTrip) {
        case .omit:
            break
        case .send:
            break
        case .reject:
            issues.append("Trip must be a public code such as Ab12Cd. Passwords are not sent.")
        }
        return issues
    }

    private mutating func applyWelcome(_ object: [String: Any], now: Date) -> [SessionEffect] {
        guard phase == .awaitingWelcome || phase == .joined else { return [] }
        phase = .joined
        hasJoinedOnce = true
        firstJoinRejects = 0
        backoffSeconds = 1
        connectedAt = now
        if let users = WireJSON.array(object["users"]) {
            self.users = users.compactMap(Self.user)
        }
        let echoedTrip = WireJSON.string(object["trip"])
        let replay = WireJSON.array(object["replay"]) ?? []
        let chats = replay.compactMap { $0 as? [String: Any] }.filter { WireJSON.string($0["type"]) == "chat" }
        let limit = capabilities?.replayLimit ?? HelloCapabilities.fallbackReplayLimit
        let coverage = coverageLabel(received: chats.count, limit: limit)
        let gap = GapLabel.make(from: lastCloseAt, to: now)
        var shown = 0
        var already = 0
        for chat in chats {
            if applyChat(chat, origin: .replay(coverage: coverage, gap: gap), now: now) {
                shown += 1
            } else {
                already += 1
            }
        }
        banner = ReplayBanner(
            label: "recentReplay",
            shown: shown,
            received: chats.count,
            alreadyVisible: already,
            coverageLabel: coverage,
            connectionGapLabel: gap
        )
        let who = identity.nick + (echoedTrip.map { " \($0)" } ?? " (no trip)")
        appendSystem("Joined as \(who). Trip is display only.")
        statusText = "Joined"
        return []
    }

    private enum ChatOrigin {
        case live
        case replay(coverage: String, gap: String)
    }

    @discardableResult
    private mutating func applyChat(_ object: [String: Any], origin: ChatOrigin, now: Date) -> Bool {
        let text = WireJSON.string(object["text"]) ?? ""
        let nick = WireJSON.string(object["nick"]) ?? ""
        let trip = WireJSON.string(object["trip"])
        let serverID = WireJSON.string(object["id"])
        let stamp = WireJSON.int64(object["ts"]) ?? WireJSON.int64(object["time"])
        let room = WireJSON.string(object["room"]) ?? identity.room
        let key = ChatIdentity.key(
            room: room,
            nick: nick,
            trip: trip,
            unixMilliseconds: stamp,
            text: text,
            serverID: serverID
        )
        if seenKeys.contains(key) { return false }

        if case .live = origin, nick == identity.nick, let localID = pending.first(where: { itemID in
            items.first(where: { $0.id == itemID })?.text == text
        }) {
            seenKeys.insert(key)
            pending.removeAll { $0 == localID }
            if let index = items.firstIndex(where: { $0.id == localID }) {
                items[index].serverID = serverID
                items[index].trip = trip
                items[index].originalUnixMilliseconds = stamp
                items[index].originalTimeLabel = OriginalTime.label(unixMilliseconds: stamp)
                items[index].kind = .liveChat
                items[index].delivery = .relayAccepted
                items[index].receipt = .accepted
            }
            return true
        }

        if case .replay = origin, nick == identity.nick, let localID = items.first(where: {
            $0.mine && $0.delivery == .unconfirmed && $0.serverID == nil && $0.text == text
        })?.id {
            seenKeys.insert(key)
            if let index = items.firstIndex(where: { $0.id == localID }) {
                let coverage = originCoverage(origin)
                items[index].serverID = serverID
                items[index].kind = .recentReplay
                items[index].delivery = .relayAccepted
                items[index].receipt = RelayReceipt(
                    displayOnly: true,
                    isTaskCompletion: false,
                    caption: "Seen in replay. Not sent again."
                )
                items[index].originalUnixMilliseconds = stamp
                items[index].originalTimeLabel = OriginalTime.label(unixMilliseconds: stamp)
                items[index].coverageLabel = coverage.coverage
                items[index].connectionGapLabel = coverage.gap
            }
            return false
        }

        seenKeys.insert(key)
        let replayMeta = originCoverage(origin)
        items.append(TranscriptItem(
            id: serverID ?? UUID().uuidString,
            kind: replayMeta.coverage == nil ? .liveChat : .recentReplay,
            nick: nick,
            text: text,
            trip: trip,
            serverID: serverID,
            originalUnixMilliseconds: stamp,
            originalTimeLabel: OriginalTime.label(unixMilliseconds: stamp),
            coverageLabel: replayMeta.coverage,
            connectionGapLabel: replayMeta.gap,
            receipt: nil,
            delivery: .notMine,
            mine: nick == identity.nick
        ))
        return true
    }

    private func originCoverage(_ origin: ChatOrigin) -> (coverage: String?, gap: String?) {
        switch origin {
        case .live:
            return (nil, nil)
        case .replay(let coverage, let gap):
            return (coverage, gap)
        }
    }

    private mutating func applyPresence(_ object: [String: Any]) {
        if let list = WireJSON.array(object["users"]) {
            users = list.compactMap(Self.user)
        }
        let event = WireJSON.string(object["event"]) ?? ""
        let nick = WireJSON.string(object["nick"]) ?? ""
        switch event {
        case "join" where !nick.isEmpty:
            appendPresence("\(nick) joined")
        case "leave" where !nick.isEmpty:
            appendPresence("\(nick) left")
        case "nick":
            let previous = WireJSON.string(object["previousNick"]) ?? "someone"
            appendPresence("\(previous) is now \(nick.isEmpty ? "?" : nick)")
        default:
            break
        }
    }

    private static func user(_ value: Any) -> PresenceUser? {
        guard let object = value as? [String: Any] else { return nil }
        let nick = WireJSON.string(object["nick"]) ?? ""
        if nick.isEmpty { return nil }
        let sessionID = WireJSON.string(object["sessionId"]) ?? nick
        let trip = WireJSON.string(object["trip"])
        return PresenceUser(sessionID: sessionID, nick: nick, trip: trip)
    }

    private mutating func applyError(_ object: [String: Any], now: Date) -> [SessionEffect] {
        let code = WireJSON.string(object["code"]) ?? ""
        let text = WireJSON.string(object["text"]) ?? code
        if phase == .awaitingWelcome {
            let decision = JoinRetry.decision(
                text: text,
                code: code,
                hasJoinedOnce: hasJoinedOnce,
                firstJoinRejects: firstJoinRejects
            )
            if decision == .retry, !hasJoinedOnce {
                firstJoinRejects += 1
            }
            appendSystem(text.isEmpty ? "Join rejected." : text)
            if decision == .stop {
                wantConnected = false
                phase = .stopped
                statusText = "Join rejected"
                return [.closeSocket, .cancelReconnect]
            }
            return failAndRetry(now: now, confirmed: hasJoinedOnce)
        }
        appendSystem(text.isEmpty ? "Error" : text)
        if code == "invalid_text", let localID = pending.first {
            markUnconfirmed(localID: localID, caption: "Rejected by the relay. Not sent again.")
            if let index = items.firstIndex(where: { $0.id == localID }) {
                items[index].delivery = .rejected
            }
        }
        return []
    }

    private mutating func sendOne(_ text: String, now: Date) -> SubmitStatus {
        guard phase == .joined else {
            return .blocked("Not connected. Nothing was sent.")
        }
        guard let frame = ClientFrame.json(ClientFrame.chat(text: text)) else {
            return .blocked("Could not encode the message. Nothing was sent.")
        }
        let localID = UUID().uuidString
        items.append(TranscriptItem(
            id: localID,
            kind: .liveChat,
            nick: identity.nick,
            text: text,
            trip: wireTrip,
            serverID: nil,
            originalUnixMilliseconds: nil,
            originalTimeLabel: "time unknown",
            coverageLabel: nil,
            connectionGapLabel: nil,
            receipt: nil,
            delivery: .sending,
            mine: true
        ))
        pending.append(localID)
        return .sent([.send(text: frame, localID: localID)])
    }

    private mutating func markPendingUnconfirmed(caption: String) {
        let ids = pending
        pending = []
        for id in ids {
            markUnconfirmed(localID: id, caption: caption)
        }
    }

    private mutating func markUnconfirmed(localID: String, caption: String) {
        pending.removeAll { $0 == localID }
        guard let index = items.firstIndex(where: { $0.id == localID }) else { return }
        guard items[index].delivery == .sending else { return }
        items[index].kind = .unconfirmedGap
        items[index].delivery = .unconfirmed
        items[index].receipt = RelayReceipt(displayOnly: true, isTaskCompletion: false, caption: caption)
    }

    private mutating func failAndRetry(now: Date, confirmed: Bool) -> [SessionEffect] {
        phase = .waitingToReconnect
        statusText = "Reconnecting…"
        let delay = nextDelay(confirmed: confirmed, uptime: 0)
        return [.closeSocket, .scheduleReconnect(milliseconds: delay)]
    }

    private mutating func nextDelay(confirmed: Bool, uptime: TimeInterval) -> Int {
        if confirmed || uptime >= 60 {
            backoffSeconds = 1
        }
        let base = backoffSeconds
        backoffSeconds = min(backoffSeconds * 2, 30)
        let seconds = min(30.0, Double(base) * (0.8 + jitterUnit * 0.4))
        return Int((seconds * 1000).rounded())
    }

    private func coverageLabel(received: Int, limit: Int) -> String {
        if limit > 0, received >= limit {
            return "coverage capped at \(limit) (older messages are outside this replay window)"
        }
        return "coverage \(received) of up to \(limit)"
    }

    private mutating func appendSystem(_ text: String) {
        items.append(TranscriptItem(
            id: UUID().uuidString,
            kind: .system,
            nick: "",
            text: text,
            trip: nil,
            serverID: nil,
            originalUnixMilliseconds: nil,
            originalTimeLabel: "",
            coverageLabel: nil,
            connectionGapLabel: nil,
            receipt: nil,
            delivery: .notMine,
            mine: false
        ))
    }

    private mutating func appendPresence(_ text: String) {
        items.append(TranscriptItem(
            id: UUID().uuidString,
            kind: .presence,
            nick: "",
            text: text,
            trip: nil,
            serverID: nil,
            originalUnixMilliseconds: nil,
            originalTimeLabel: "",
            coverageLabel: nil,
            connectionGapLabel: nil,
            receipt: nil,
            delivery: .notMine,
            mine: false
        ))
    }

    private var durableTypes: Set<String> {
        [
            "delivery", "pull", "pull_result", "ack", "ack_result", "accepted", "dead",
            "bind", "bound", "resume", "resumed", "leased", "acked"
        ]
    }

}

public enum JoinRetryDecision: Equatable, Sendable {
    case retry
    case stop
}

public enum JoinRetry {
    private static let retryable = try! NSRegularExpression(pattern: "taken|too fast|rate|wait", options: [.caseInsensitive])
    private static let retryableCodes: Set<String> = ["nick_taken", "rate_limited"]
    public static let firstJoinMaxRetries = 3

    public static func decision(text: String, code: String, hasJoinedOnce: Bool, firstJoinRejects: Int) -> JoinRetryDecision {
        if hasJoinedOnce { return .retry }
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        let textMatches = retryable.firstMatch(in: text, range: range) != nil
        if (textMatches || retryableCodes.contains(code)), firstJoinRejects < firstJoinMaxRetries {
            return .retry
        }
        return .stop
    }
}
