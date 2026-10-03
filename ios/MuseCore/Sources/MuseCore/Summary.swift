import Foundation

public struct SummaryLine: Equatable, Sendable {
    public var nick: String
    public var text: String
    public var origin: String
    public var originalTimeLabel: String
    public var coverageLabel: String?
    public var connectionGapLabel: String?
}

public struct SummaryContext: Equatable, Sendable {
    public static let defaultMaxMessages = 30
    public static let defaultMaxCharacters = 4000

    public var includeReplay: Bool
    public var lines: [SummaryLine]
    public var replayExcluded: Int
    public var omittedByBound: Int
    public var sessionOnly: Bool
    public var privacyNote: String
    public var prompt: String
}

public struct IntelligenceGate: Equatable, Sendable {
    public var osSupportsOnDeviceModel: Bool
    public var runtimeAvailable: Bool
    public var languageSupported: Bool
    public var unavailableReason: String?

    public var usesOrdinaryChat: Bool {
        !osSupportsOnDeviceModel || !runtimeAvailable || !languageSupported
    }

    public static func decide(
        osSupportsOnDeviceModel: Bool,
        runtimeAvailable: Bool,
        languageSupported: Bool,
        unavailableReason: String? = nil
    ) -> IntelligenceGate {
        let reason: String?
        if !osSupportsOnDeviceModel {
            reason = unavailableReason ?? "On-device summaries need a newer iOS. Chat is unchanged."
        } else if !runtimeAvailable || !languageSupported {
            reason = unavailableReason ?? "Apple Intelligence is unavailable for this device or language. Chat is unchanged."
        } else {
            reason = nil
        }
        return IntelligenceGate(
            osSupportsOnDeviceModel: osSupportsOnDeviceModel,
            runtimeAvailable: runtimeAvailable,
            languageSupported: languageSupported,
            unavailableReason: reason
        )
    }
}

/// App Intents carry a room id and nothing else. Messages, nicks, and trips stay off the intent.
public struct RoomFocus: Equatable, Codable, Sendable {
    public var roomID: String

    public init(roomID: String) {
        self.roomID = roomID
    }
}

public enum SummaryBuilder {
    public static func make(
        items: [TranscriptItem],
        includeReplay: Bool,
        maxMessages: Int = SummaryContext.defaultMaxMessages,
        maxCharacters: Int = SummaryContext.defaultMaxCharacters
    ) -> SummaryContext {
        var replayExcluded = 0
        var eligible: [SummaryLine] = []
        for item in items {
            switch item.kind {
            case .liveChat:
                eligible.append(line(item, origin: "live"))
            case .recentReplay:
                if includeReplay {
                    eligible.append(line(item, origin: "recentReplay"))
                } else {
                    replayExcluded += 1
                }
            case .presence, .system, .unconfirmedGap:
                break
            }
        }

        let capMessages = max(1, maxMessages)
        let capCharacters = max(1, maxCharacters)
        var kept: [SummaryLine] = []
        var characters = 0
        for line in eligible.reversed() {
            let cost = line.text.utf16.count + line.nick.utf16.count
            if kept.count >= capMessages || characters + cost > capCharacters {
                break
            }
            kept.append(line)
            characters += cost
        }
        kept.reverse()
        let omitted = eligible.count - kept.count
        let privacy = "Session-only. This summary is not saved, not sent to the room, and not an agent-task result."
        return SummaryContext(
            includeReplay: includeReplay,
            lines: kept,
            replayExcluded: replayExcluded,
            omittedByBound: omitted,
            sessionOnly: true,
            privacyNote: privacy,
            prompt: prompt(lines: kept, includeReplay: includeReplay, privacy: privacy, omitted: omitted, replayExcluded: replayExcluded)
        )
    }

    private static func line(_ item: TranscriptItem, origin: String) -> SummaryLine {
        SummaryLine(
            nick: item.nick,
            text: item.text,
            origin: origin,
            originalTimeLabel: item.originalTimeLabel,
            coverageLabel: origin == "recentReplay" ? item.coverageLabel : nil,
            connectionGapLabel: origin == "recentReplay" ? item.connectionGapLabel : nil
        )
    }

    private static func prompt(
        lines: [SummaryLine],
        includeReplay: Bool,
        privacy: String,
        omitted: Int,
        replayExcluded: Int
    ) -> String {
        var rows = lines.map { line -> String in
            var parts = ["[\(line.origin)]", line.originalTimeLabel, line.nick + ":", line.text]
            if line.origin == "recentReplay" {
                if let coverage = line.coverageLabel { parts.append(coverage) }
                if let gap = line.connectionGapLabel { parts.append(gap) }
            }
            return parts.joined(separator: " ")
        }
        if rows.isEmpty {
            rows = ["(no live messages in this summary)"]
        }
        return """
        \(privacy)
        Replay included: \(includeReplay ? "yes, labeled recentReplay" : "no")
        Replayed messages left out: \(replayExcluded)
        Older live lines left out by the local bound: \(omitted)
        A relay receipt is not a completed task.
        \(rows.joined(separator: "\n"))
        """
    }
}
