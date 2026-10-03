import SwiftUI

struct PresenceList: View {
    var users: [PresenceUser]

    var body: some View {
        Group {
            if users.isEmpty {
                ContentUnavailableView("No one yet", systemImage: "person.2") {
                    Text("People appear here from presence updates.")
                }
            } else {
                List(users) { user in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(user.nick)
                            .font(.body)
                        if let trip = user.trip, !trip.isEmpty {
                            Text("\(trip) · display only")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel(accessibilityLabel(for: user))
                }
            }
        }
    }

    private func accessibilityLabel(for user: PresenceUser) -> String {
        if let trip = user.trip, !trip.isEmpty {
            return "\(user.nick), trip \(trip), display only"
        }
        return user.nick
    }
}
