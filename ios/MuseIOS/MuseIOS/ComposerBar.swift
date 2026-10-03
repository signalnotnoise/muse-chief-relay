import SwiftUI

struct ComposerBar: View {
    @Binding var text: String
    var state: ComposerState
    var onSend: () -> Void
    var onReview: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .bottom, spacing: 8) {
                editor
                Button(action: state.offersReviewSplit ? onReview : onSend) {
                    Text(state.offersReviewSplit ? "Review" : "Send")
                        .frame(minWidth: 64, minHeight: 44)
                }
                .buttonStyle(.borderedProminent)
                .disabled(!state.canSend && !state.offersReviewSplit)
                .accessibilityHint(state.offersReviewSplit ? "Review the split before anything is sent" : "Send the message")
            }
            HStack {
                Text("\(state.count)/\(state.limit)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(state.offersReviewSplit ? Color.red : Color.secondary)
                    .accessibilityLabel("\(state.count) of \(state.limit) characters")
                Spacer()
            }
            if let reason = state.blockedReason, state.count > 0 || state.offersReviewSplit {
                Text(reason)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.bar)
    }

    private var editor: some View {
        let field = TextEditor(text: $text)
            .frame(minHeight: 44, maxHeight: 140)
            .accessibilityLabel("Message")
        return Group {
            if #available(iOS 18.0, macOS 15.0, visionOS 2.0, *) {
                field.writingToolsBehavior(.complete)
            } else {
                field
            }
        }
    }
}
