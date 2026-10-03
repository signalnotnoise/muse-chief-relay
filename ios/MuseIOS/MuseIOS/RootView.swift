import SwiftUI

struct RootView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Group {
            if model.inChat {
                ChatScreen(model: model)
            } else {
                JoinForm(model: model)
            }
        }
        .tint(.accentColor)
    }
}

struct JoinForm: View {
    @ObservedObject var model: AppModel
    @FocusState private var focused: Field?

    private enum Field: Hashable {
        case room, nick, trip
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Room", text: $model.roomField)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .focused($focused, equals: .room)
                        .accessibilityHint("The room id. It stays on this device.")
                    TextField("Nick", text: $model.nickField)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .focused($focused, equals: .nick)
                    TextField("Public trip", text: $model.tripField)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .focused($focused, equals: .trip)
                        .accessibilityHint("Optional six-character display code. Not a password.")
                } header: {
                    Text("Join")
                } footer: {
                    Text("A public trip is display metadata, like !Ab12Cd. This client does not send a password or an owner secret, and a trip does not sign you in.")
                }

                Section {
                    LabeledContent("Relay", value: relayLabel)
                    if let error = model.settings.urlError {
                        Text(error)
                            .foregroundStyle(.red)
                    }
                    if !model.session.joinIssues.isEmpty {
                        ForEach(model.session.joinIssues, id: \.self) { issue in
                            Text(issue)
                                .foregroundStyle(.red)
                        }
                    }
                } footer: {
                    Text(model.session.statusText)
                }
            }
            .navigationTitle("Muse")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Connect") { model.connect() }
                        .disabled(model.settings.relayURL == nil)
                }
            }
        }
    }

    private var relayLabel: String {
        model.settings.relayURL?.host ?? "Not configured"
    }
}
