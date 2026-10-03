import Foundation

public struct ComposerState: Equatable, Sendable {
    public var count: Int
    public var limit: Int
    public var canSend: Bool
    public var blockedReason: String?
    public var offersReviewSplit: Bool
}

public enum Composer {
    /// JavaScript `String.length`. The relay rejects any other count with `invalid_text`.
    public static func utf16Count(_ text: String) -> Int {
        text.utf16.count
    }

    public static func state(text: String, limit: Int) -> ComposerState {
        let count = utf16Count(text)
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return ComposerState(
                count: count,
                limit: limit,
                canSend: false,
                blockedReason: "Nothing to send",
                offersReviewSplit: false
            )
        }
        if count > limit {
            return ComposerState(
                count: count,
                limit: limit,
                canSend: false,
                blockedReason: "Over the \(limit)-character relay limit. Shorten it, or review a split. Nothing is sent until you confirm.",
                offersReviewSplit: true
            )
        }
        return ComposerState(
            count: count,
            limit: limit,
            canSend: true,
            blockedReason: nil,
            offersReviewSplit: false
        )
    }

    /// Split on character boundaries without dropping or rewriting text.
    /// The caller must show the chunks and wait for an explicit confirm.
    public static func split(text: String, limit: Int) -> [String] {
        let cap = max(1, limit)
        if text.isEmpty { return [] }
        if utf16Count(text) <= cap { return [text] }

        var chunks: [String] = []
        var current = ""
        var currentCount = 0
        for character in text {
            let units = character.utf16.count
            if units > cap {
                if !current.isEmpty {
                    chunks.append(current)
                    current = ""
                    currentCount = 0
                }
                chunks.append(contentsOf: splitByScalar(character, limit: cap))
                continue
            }
            if currentCount + units > cap {
                chunks.append(current)
                current = String(character)
                currentCount = units
            } else {
                current.append(character)
                currentCount += units
            }
        }
        if !current.isEmpty { chunks.append(current) }
        return chunks
    }

    private static func splitByScalar(_ character: Character, limit: Int) -> [String] {
        var chunks: [String] = []
        var current = ""
        var currentCount = 0
        for scalar in character.unicodeScalars {
            let units = String(scalar).utf16.count
            if currentCount + units > limit, !current.isEmpty {
                chunks.append(current)
                current = ""
                currentCount = 0
            }
            current.unicodeScalars.append(scalar)
            currentCount += units
        }
        if !current.isEmpty { chunks.append(current) }
        return chunks
    }
}
