import AppIntents

struct OpenRoomIntent: AppIntent {
    static var title: LocalizedStringResource = "Open room"
    static var description = IntentDescription("Opens Muse to a room id. The shortcut does not read messages, nicks, or trips.")
    static var openAppWhenRun: Bool = true

    @Parameter(title: "Room ID", requestValueDialog: IntentDialog("Which room?"))
    var roomID: String

    func perform() async throws -> some IntentResult {
        let focus = RoomFocus(roomID: roomID)
        await MainActor.run {
            RoomIntentBridge.shared.offer(roomID: focus.roomID)
        }
        return .result()
    }
}

struct MuseShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: OpenRoomIntent(),
            phrases: ["Open a room in \(.applicationName)"],
            shortTitle: "Open room",
            systemImageName: "bubble.left.and.bubble.right"
        )
    }
}
