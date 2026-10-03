import Foundation

enum OnDeviceSummary {
    static func gate() -> IntelligenceGate {
        #if canImport(FoundationModels)
        if #available(iOS 26.0, macOS 26.0, visionOS 26.0, *) {
            return FoundationBackend.gate()
        }
        #endif
        return IntelligenceGate.decide(
            osSupportsOnDeviceModel: false,
            runtimeAvailable: false,
            languageSupported: false
        )
    }

    static func summarize(_ context: SummaryContext) async throws -> String {
        #if canImport(FoundationModels)
        if #available(iOS 26.0, macOS 26.0, visionOS 26.0, *) {
            return try await FoundationBackend.summarize(context)
        }
        #endif
        throw SummaryUnavailable(message: "On-device summaries are unavailable. Chat is unchanged.")
    }
}

struct SummaryUnavailable: Error {
    var message: String
}

#if canImport(FoundationModels)
import FoundationModels

@available(iOS 26.0, macOS 26.0, visionOS 26.0, *)
enum FoundationBackend {
    static func gate() -> IntelligenceGate {
        switch SystemLanguageModel.default.availability {
        case .available:
            return IntelligenceGate.decide(
                osSupportsOnDeviceModel: true,
                runtimeAvailable: true,
                languageSupported: true
            )
        case .unavailable(let reason):
            let described = String(describing: reason)
            let languageBlocked = described.lowercased().contains("language")
            return IntelligenceGate.decide(
                osSupportsOnDeviceModel: true,
                runtimeAvailable: !languageBlocked,
                languageSupported: !languageBlocked,
                unavailableReason: "Apple Intelligence is unavailable (\(described)). Chat is unchanged."
            )
        }
    }

    static func summarize(_ context: SummaryContext) async throws -> String {
        let session = LanguageModelSession(instructions: """
            Summarize only the lines you are given. This is a private, session-only chat summary.
            Do not invent messages. Do not treat a relay receipt as a completed task.
            Lines marked recentReplay are catch-up, not the live conversation, and they already carry their original time, coverage, and connection gap.
            """)
        let response = try await session.respond(to: context.prompt)
        return response.content
    }
}
#endif
