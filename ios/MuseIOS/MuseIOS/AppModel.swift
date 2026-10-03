import Foundation

@MainActor
final class RoomIntentBridge {
    static let shared = RoomIntentBridge()
    private(set) var roomID: String?

    func offer(roomID: String) {
        let trimmed = roomID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        self.roomID = RoomFocus(roomID: trimmed).roomID
    }

    func consume() -> String? {
        let value = roomID
        roomID = nil
        return value
    }
}

@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var session: ChatSession
    @Published var draft = ""
    @Published var roomField: String
    @Published var nickField: String
    @Published var tripField: String
    @Published var includeReplayInSummary = false
    @Published var summaryText: String?
    @Published var summaryNote: String?
    @Published var summaryBusy = false
    @Published var splitReview: [String]?
    @Published var offeredRoomID: String?

    let settings: ClientSettings
    private let socket = RelaySocket()
    private var generation = 0
    private var reconnectTask: Task<Void, Never>?

    init(settings: ClientSettings) {
        self.settings = settings
        roomField = settings.room
        nickField = settings.nick
        tripField = settings.publicTrip
        let url = settings.relayURL ?? ClientSettings.defaultRelayURL
        session = ChatSession(
            identity: JoinIdentity(relayURL: url, room: settings.room, nick: settings.nick, publicTrip: settings.publicTrip)
        )
        bindSocket()
    }

    var composer: ComposerState {
        session.composerState(for: draft)
    }

    var inChat: Bool {
        session.wantConnected && session.phase != .stopped
    }

    func connect() {
        guard let url = settings.relayURL else { return }
        reconnectTask?.cancel()
        session = ChatSession(
            identity: JoinIdentity(
                relayURL: url,
                room: roomField,
                nick: nickField,
                publicTrip: tripField
            )
        )
        apply(session.connect(now: Date()))
    }

    func disconnect() {
        reconnectTask?.cancel()
        apply(session.disconnect(now: Date()))
    }

    func sendDraft() {
        let text = draft
        switch session.submit(text, now: Date()) {
        case .sent(let effects):
            draft = ""
            apply(effects)
        case .needsReview(let chunks):
            splitReview = chunks
        case .blocked:
            break
        }
    }

    func confirmSplit() {
        guard let chunks = splitReview else { return }
        switch session.confirmReviewedSplit(chunks, now: Date()) {
        case .sent(let effects):
            splitReview = nil
            draft = ""
            apply(effects)
        case .blocked, .needsReview:
            break
        }
    }

    func sceneBecameActive() {
        applyPendingRoomFocus()
        apply(session.enterForeground(now: Date()))
    }

    func sceneEnteredBackground() {
        reconnectTask?.cancel()
        apply(session.enterBackground(now: Date()))
    }

    func applyPendingRoomFocus() {
        guard let roomID = RoomIntentBridge.shared.consume() else { return }
        if session.wantConnected {
            offeredRoomID = roomID
        } else {
            roomField = roomID
        }
    }

    func acceptOfferedRoom() {
        guard let offeredRoomID else { return }
        roomField = offeredRoomID
        self.offeredRoomID = nil
        disconnect()
    }

    func summaryContext() -> SummaryContext {
        SummaryBuilder.make(items: session.items, includeReplay: includeReplayInSummary)
    }

    func refreshSummary() async {
        summaryBusy = true
        summaryText = nil
        let context = summaryContext()
        let gate = OnDeviceSummary.gate()
        if gate.usesOrdinaryChat {
            summaryNote = gate.unavailableReason
            summaryText = nil
            summaryBusy = false
            return
        }
        do {
            summaryText = try await OnDeviceSummary.summarize(context)
            summaryNote = context.privacyNote
        } catch {
            summaryText = nil
            summaryNote = "Summary didn't finish. Chat is unchanged."
        }
        summaryBusy = false
    }

    private func bindSocket() {
        socket.onOpen = { [weak self] generation in
            guard let self, generation == self.generation else { return }
            self.apply(self.session.socketOpened(now: Date()))
        }
        socket.onText = { [weak self] text, generation in
            guard let self, generation == self.generation else { return }
            self.apply(self.session.receive(text, now: Date()))
        }
        socket.onClose = { [weak self] generation in
            guard let self, generation == self.generation else { return }
            self.apply(self.session.socketClosed(now: Date()))
        }
        socket.onSendResult = { [weak self] localID, ok in
            guard let self, !ok, !localID.isEmpty else { return }
            self.session.transportFailed(localID: localID, now: Date())
        }
    }

    private func apply(_ effects: [SessionEffect]) {
        for effect in effects {
            switch effect {
            case .openSocket(let url):
                generation += 1
                socket.connect(url: url, generation: generation)
            case .send(let text, let localID):
                socket.send(text: text, localID: localID)
            case .closeSocket:
                socket.close()
            case .cancelReconnect:
                reconnectTask?.cancel()
                reconnectTask = nil
            case .scheduleReconnect(let milliseconds):
                reconnectTask?.cancel()
                let wait = milliseconds
                reconnectTask = Task { [weak self] in
                    try? await Task.sleep(nanoseconds: UInt64(wait) * 1_000_000)
                    guard !Task.isCancelled else { return }
                    await MainActor.run {
                        guard let self else { return }
                        self.apply(self.session.reconnectDue(now: Date()))
                    }
                }
            }
        }
    }
}
