import SwiftUI

/// Chat layout uses system navigation, lists, and materials.
/// Cupertino (HIG review) still needs to review polish.
struct ChatScreen: View {
    @ObservedObject var model: AppModel
    #if os(iOS)
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var showPeople = false
    #endif
    @State private var showSummary = false

    var body: some View {
        destination
            .sheet(isPresented: $showSummary) {
                SummarySheet(model: model)
            }
            .sheet(isPresented: splitPresented) {
            SplitReviewSheet(
                chunks: model.splitReview ?? [],
                onCancel: { model.splitReview = nil },
                onConfirm: { model.confirmSplit() }
            )
        }
    }

    @ViewBuilder
    private var destination: some View {
        #if os(iOS)
        if sizeClass == .regular {
            split
        } else {
            transcript.sheet(isPresented: $showPeople) {
                NavigationStack {
                    PresenceList(users: model.session.users)
                        .navigationTitle("People")
                        .toolbar {
                            ToolbarItem(placement: .confirmationAction) {
                                Button("Done") { showPeople = false }
                            }
                        }
                }
                .presentationDetents([.medium, .large])
            }
        }
        #else
        split
        #endif
    }

    private var split: some View {
        NavigationSplitView {
            PresenceList(users: model.session.users)
                .navigationTitle("People")
        } detail: {
            transcript
        }
    }

    private var splitPresented: Binding<Bool> {
        Binding(
            get: { model.splitReview != nil },
            set: { if !$0 { model.splitReview = nil } }
        )
    }

    private var transcript: some View {
        NavigationStack {
            ScrollViewReader { proxy in
                List {
                    if let banner = model.session.banner {
                        Section {
                            ReplayHeader(banner: banner)
                        }
                    }
                    if let offered = model.offeredRoomID {
                        Section {
                            VStack(alignment: .leading, spacing: 8) {
                                Text("A shortcut asked to open a room.")
                                Text(offered)
                                    .font(.body.monospaced())
                                    .textSelection(.enabled)
                                Button("Leave this room and use it") { model.acceptOfferedRoom() }
                            }
                        }
                    }
                    ForEach(model.session.items) { item in
                        MessageRow(item: item)
                            .id(item.id)
                    }
                }
                .listStyle(.plain)
                .onChange(of: model.session.items.count) { _, _ in
                    if let last = model.session.items.last {
                        withAnimation { proxy.scrollTo(last.id, anchor: .bottom) }
                    }
                }
            }
            .navigationTitle(model.session.identity.room.isEmpty ? "Muse" : model.session.identity.room)
            .museInlineNavigationTitle()
            .safeAreaInset(edge: .bottom, spacing: 0) {
                ComposerBar(
                    text: $model.draft,
                    state: model.composer,
                    onSend: { model.sendDraft() },
                    onReview: { model.sendDraft() }
                )
            }
            .toolbar {
                #if os(iOS)
                if sizeClass == .compact {
                    ToolbarItem(placement: .topBarLeading) {
                        Button {
                            showPeople = true
                        } label: {
                            Label("People", systemImage: "person.2")
                        }
                    }
                }
                #endif
                ToolbarItemGroup(placement: .museTrailing) {
                    Button {
                        showSummary = true
                    } label: {
                        Label("Summary", systemImage: "text.quote")
                    }
                    Button {
                        model.disconnect()
                    } label: {
                        Label("Disconnect", systemImage: "xmark.circle")
                    }
                }
            }
            .overlay(alignment: .top) {
                if model.session.phase != .joined {
                    Text(model.session.statusText)
                        .font(.footnote)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(.thinMaterial, in: Capsule())
                        .padding(.top, 8)
                        .accessibilityAddTraits(.updatesFrequently)
                }
            }
        }
    }
}

struct ReplayHeader: View {
    var banner: ReplayBanner

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("recentReplay")
                .font(.headline)
            Text("\(banner.shown) new, \(banner.alreadyVisible) already on screen, \(banner.received) in this window.")
                .font(.subheadline)
            Text(banner.coverageLabel)
                .font(.footnote)
                .foregroundStyle(.secondary)
            if let uncertain = banner.uncertainLabel {
                Text(uncertain)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            Text(banner.connectionGapLabel)
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }
}

struct MessageRow: View {
    var item: TranscriptItem

    var body: some View {
        switch item.kind {
        case .system, .presence:
            Text(item.text)
                .font(.footnote)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .center)
                .accessibilityLabel(item.text)
        case .liveChat, .recentReplay, .unconfirmedGap:
            chat
        }
    }

    private var chat: some View {
        VStack(alignment: item.mine ? .trailing : .leading, spacing: 4) {
            Text(item.nick.isEmpty ? " " : item.nick)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            Text(item.text)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: item.mine ? .trailing : .leading)
            if item.kind == .recentReplay {
                Text("Recent replay")
                    .font(.caption2.weight(.semibold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 2)
                    .background(.quaternary, in: Capsule())
            }
            if !item.originalTimeLabel.isEmpty, item.kind != .system {
                Text(item.originalTimeLabel)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }
            if let coverage = item.coverageLabel {
                Text(coverage)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            if let gap = item.connectionGapLabel {
                Text(gap)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            if let trip = item.trip, !trip.isEmpty {
                Text("\(trip) · display only")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }
            if let receipt = item.receipt {
                Text(receipt.caption)
                    .font(.caption2)
                    .foregroundStyle(item.delivery == .unconfirmed || item.delivery == .rejected ? Color.orange : Color.secondary)
            } else if item.delivery == .sending {
                Text("Sending…")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

struct SplitReviewSheet: View {
    var chunks: [String]
    var onCancel: () -> Void
    var onConfirm: () -> Void

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text("Nothing has been sent. Confirm to send these parts in order. If the connection drops, an unconfirmed part is marked as a gap and is not sent again.")
                        .font(.subheadline)
                }
                ForEach(Array(chunks.enumerated()), id: \.offset) { index, chunk in
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Part \(index + 1) of \(chunks.count)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text(chunk)
                            .textSelection(.enabled)
                    }
                }
            }
            .navigationTitle("Review split")
            .museInlineNavigationTitle()
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: onCancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Send parts", action: onConfirm)
                }
            }
        }
    }
}
