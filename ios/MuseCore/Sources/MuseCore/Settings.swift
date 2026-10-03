import Foundation

/// Where this client is running. The wire does not change between them.
public enum ClientDestination: String, Equatable, Sendable {
    case iOS
    case macOS
    case visionOS
}

public enum SceneSuspension {
    /// iOS and visionOS suspend the scene. macOS keeps the app in the foreground.
    public static func closesSocketWhenSceneBackgrounds(_ destination: ClientDestination) -> Bool {
        switch destination {
        case .iOS, .visionOS:
            return true
        case .macOS:
            return false
        }
    }
}

public struct ClientSettings: Equatable, Sendable {
    public static let defaultRelayURL = URL(string: "wss://ws.voizel.com/relay")!

    public var relayURL: URL?
    public var room: String
    public var nick: String
    public var publicTrip: String
    public var urlError: String?

    public static func load(
        relayURL rawURL: String?,
        room: String?,
        nick: String?,
        publicTrip: String?
    ) -> ClientSettings {
        let urlText = rawURL?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let resolved: URL?
        let error: String?
        if urlText.isEmpty {
            resolved = defaultRelayURL
            error = nil
        } else if let url = validate(urlText) {
            resolved = url
            error = nil
        } else {
            resolved = nil
            error = "Relay URL must be an absolute ws:// or wss:// URL with no user, password, query, or fragment."
        }
        return ClientSettings(
            relayURL: resolved,
            room: room?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "",
            nick: nick?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "",
            publicTrip: publicTrip?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "",
            urlError: error
        )
    }

    public static func validate(_ raw: String) -> URL? {
        guard let url = URL(string: raw),
              let scheme = url.scheme?.lowercased(),
              scheme == "ws" || scheme == "wss",
              url.host != nil,
              url.user == nil,
              url.password == nil,
              (url.query ?? "").isEmpty,
              url.fragment == nil
        else { return nil }
        return url
    }
}
