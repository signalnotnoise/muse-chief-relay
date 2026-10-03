import SwiftUI

struct SummarySheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("Include replay", isOn: $model.includeReplayInSummary)
                    Text("Replayed messages stay out of the summary unless you turn this on. Included replay keeps its original time, coverage, and connection gap.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                } header: {
                    Text("Session summary")
                } footer: {
                    Text("Private to this session. It is not saved and it is not sent to the room. A relay receipt is not a completed task.")
                }

                if let note = model.summaryNote {
                    Section {
                        Text(note)
                    }
                }

                Section {
                    if model.summaryBusy {
                        ProgressView("Summarizing…")
                    } else if let summaryText = model.summaryText {
                        Text(summaryText)
                            .textSelection(.enabled)
                    } else if OnDeviceSummary.gate().usesOrdinaryChat {
                        Text(OnDeviceSummary.gate().unavailableReason ?? "Summary is unavailable. Chat is unchanged.")
                    } else {
                        Text("Uses the latest live messages on this device, up to a local bound.")
                            .foregroundStyle(.secondary)
                    }
                }

                Section {
                    Button(OnDeviceSummary.gate().usesOrdinaryChat ? "Chat stays as it is" : "Summarize") {
                        Task { await model.refreshSummary() }
                    }
                    .disabled(model.summaryBusy || OnDeviceSummary.gate().usesOrdinaryChat)
                }
            }
            .navigationTitle("Summary")
            .museInlineNavigationTitle()
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") {
                        model.discardSummary()
                        dismiss()
                    }
                }
            }
            .onChange(of: model.includeReplayInSummary) { _, _ in
                model.discardSummary()
            }
        }
    }
}
