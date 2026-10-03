import SwiftUI

@main
struct MuseIOSApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var model = AppModel(settings: ClientSettings.loadFromBundle())

    var body: some Scene {
        framed(chatScene)
    }

    private var chatScene: some Scene {
        WindowGroup {
            RootView(model: model)
                .onAppear { model.applyPendingRoomFocus() }
        }
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active:
                model.sceneBecameActive()
            case .background:
                // iOS and visionOS suspend the scene. macOS does not.
                if SceneSuspension.closesSocketWhenSceneBackgrounds(MuseDestination.current) {
                    model.sceneEnteredBackground()
                }
            case .inactive:
                break
            @unknown default:
                break
            }
        }
    }

    private func framed<S: Scene>(_ scene: S) -> some Scene {
        #if os(macOS)
        scene.defaultSize(width: 960, height: 680)
        #elseif os(visionOS)
        scene.defaultSize(width: 980, height: 720)
        #else
        scene
        #endif
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
