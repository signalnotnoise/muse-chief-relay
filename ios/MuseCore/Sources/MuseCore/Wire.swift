import Foundation

/// Public trip codes are display metadata. A password is never a trip and is never sent.
public enum PublicTripDecision: Equatable, Sendable {
    case omit
    case send(String)
    case reject
}

public enum PublicTrip {
    private static let body = try! NSRegularExpression(pattern: "^[A-Za-z0-9+/]{6}$")

    public static func decide(_ raw: String) -> PublicTripDecision {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return .omit }
        if trimmed.contains("#") || trimmed.contains(where: \.isWhitespace) {
            return .reject
        }
        let code = trimmed.hasPrefix("!") ? String(trimmed.dropFirst()) : trimmed
        let range = NSRange(code.startIndex..<code.endIndex, in: code)
        guard body.firstMatch(in: code, range: range) != nil else { return .reject }
        return .send("!" + code)
    }
}

public struct HelloCapabilities: Equatable, Sendable {
    public var durableAdvertised: Bool
    public var durableVersion: Int?
    public var replayLimit: Int
    public var nickLimit: Int
    public var roomLimit: Int
    public var tripLimit: Int
    public var textLimit: Int
    /// Present when hello carries `versions`. This client records it and still joins v1.
    public var versionsFieldPresent: Bool
    public var advertisedVersions: [Int]
    /// This client never opts into durable join, pull, or ack.
    public var optedIntoDurable: Bool

    public static let fallbackTextLimit = 2048
    public static let fallbackNickLimit = 24
    public static let fallbackRoomLimit = 64
    public static let fallbackTripLimit = 16
    public static let fallbackReplayLimit = 50
}

public enum HelloParse: Equatable, Sendable {
    case accepted(HelloCapabilities)
    /// The first frame was not a hello. The caller may back off and try again.
    case retry(String)
    /// The server spoke a hello this client will not negotiate. Stop. Do not send another version.
    case stop(String)
}

public enum HelloFrame {
    public static func parse(_ object: [String: Any]) -> HelloParse {
        guard WireJSON.string(object["type"]) == "hello" else {
            return .retry("expected hello")
        }
        guard WireJSON.string(object["protocol"]) == "voizle-text-relay" else {
            return .stop("expected voizle-text-relay")
        }
        guard WireJSON.int(object["v"]) == 1 else {
            return .stop("expected hello v 1")
        }
        let limits = WireJSON.object(object["limits"]) ?? [:]
        let versions = WireJSON.array(object["versions"]) ?? []
        let capabilities = HelloCapabilities(
            durableAdvertised: WireJSON.bool(object["durable"]) ?? false,
            durableVersion: WireJSON.int(object["durableVersion"]),
            replayLimit: WireJSON.int(object["replayLimit"]) ?? HelloCapabilities.fallbackReplayLimit,
            nickLimit: WireJSON.int(limits["nick"]) ?? HelloCapabilities.fallbackNickLimit,
            roomLimit: WireJSON.int(limits["room"]) ?? HelloCapabilities.fallbackRoomLimit,
            tripLimit: WireJSON.int(limits["trip"]) ?? HelloCapabilities.fallbackTripLimit,
            textLimit: WireJSON.int(limits["text"]) ?? HelloCapabilities.fallbackTextLimit,
            versionsFieldPresent: object["versions"] != nil,
            advertisedVersions: versions.compactMap { WireJSON.int($0) },
            optedIntoDurable: false
        )
        return .accepted(capabilities)
    }
}

public enum ClientFrame {
    public static func join(room: String, nick: String, trip: String?) -> [String: Any] {
        var frame: [String: Any] = [
            "v": 1,
            "type": "join",
            "room": room,
            "nick": nick
        ]
        if let trip {
            frame["trip"] = trip
        }
        return frame
    }

    public static func chat(text: String) -> [String: Any] {
        [
            "v": 1,
            "type": "chat",
            "text": text
        ]
    }

    public static func json(_ object: [String: Any]) -> String? {
        WireJSON.stringify(object)
    }
}

public enum ChatIdentity {
    /// Canonical lowercase UUID, or nil when the server id is missing or not a UUID.
    public static func canonicalServerID(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let uuid = UUID(uuidString: trimmed) else { return nil }
        return uuid.uuidString.lowercased()
    }

    /// UUID chat id wins. Otherwise a stable hash of room, nick, trip, timestamp, and text.
    public static func key(
        room: String,
        nick: String,
        trip: String?,
        unixMilliseconds: Int64?,
        text: String,
        serverID: String?
    ) -> String {
        if let serverID = canonicalServerID(serverID) {
            return "srv:" + serverID
        }
        let tripBody = canonicalTrip(trip) ?? ""
        let stamp = unixMilliseconds.map(String.init) ?? ""
        let raw = [room, nick, tripBody, stamp, text].joined(separator: "\n")
        return "msg:" + String(SHA256.hex(raw).prefix(32))
    }

    public static func canonicalTrip(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty || trimmed == "<null>" { return nil }
        let body = trimmed.hasPrefix("!") ? String(trimmed.dropFirst()) : trimmed
        return body.isEmpty ? nil : body
    }
}

public enum OriginalTime {
    public static func label(unixMilliseconds: Int64?) -> String {
        guard let unixMilliseconds else { return "time unknown" }
        let date = Date(timeIntervalSince1970: TimeInterval(unixMilliseconds) / 1000)
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}

public enum GapLabel {
    public static func make(from earlier: Date?, to later: Date) -> String {
        guard let earlier else { return "connection gap: none in this session" }
        let seconds = max(0, Int(later.timeIntervalSince(earlier).rounded(.down)))
        if seconds < 60 { return "connection gap: \(seconds)s" }
        let minutes = seconds / 60
        if minutes < 60 { return "connection gap: \(minutes)m \(seconds % 60)s" }
        let hours = minutes / 60
        return "connection gap: \(hours)h \(minutes % 60)m"
    }
}
