import SwiftUI

@main
struct MuseIOSApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var model = AppModel(settings: ClientSettings.loadFromBundle())

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
                .onAppear { model.applyPendingRoomFocus() }
        }
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active:
                model.sceneBecameActive()
            case .background:
                model.sceneEnteredBackground()
            case .inactive:
                break
            @unknown default:
                break
            }
        }
    }
}

extension ClientSettings {
    static func loadFromBundle() -> ClientSettings {
        let info = Bundle.main.infoDictionary ?? [:]
        return ClientSettings.load(
            relayURL: info["MuseRelayURL"] as? String,
            room: info["MuseRoom"] as? String,
            nick: info["MuseNick"] as? String,
            publicTrip: info["MusePublicTrip"] as? String
        )
    }
}
