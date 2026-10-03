import SwiftUI

enum MuseDestination {
    static var current: ClientDestination {
        #if os(macOS)
        .macOS
        #elseif os(visionOS)
        .visionOS
        #else
        .iOS
        #endif
    }
}

extension View {
    @ViewBuilder
    func museInlineNavigationTitle() -> some View {
        #if os(iOS) || os(visionOS)
        self.navigationBarTitleDisplayMode(.inline)
        #else
        self
        #endif
    }
}

extension ToolbarItemPlacement {
    static var museLeading: ToolbarItemPlacement {
        #if os(macOS)
        .navigation
        #else
        .topBarLeading
        #endif
    }

    static var museTrailing: ToolbarItemPlacement {
        #if os(macOS)
        .primaryAction
        #else
        .topBarTrailing
        #endif
    }
}
