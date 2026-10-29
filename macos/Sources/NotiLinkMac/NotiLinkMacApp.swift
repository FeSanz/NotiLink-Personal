import SwiftUI

/// All real UI lives in AppDelegate's own NSWindow (hosting a raw WKWebView) — this `App`/`Scene`
/// exists only to get SwiftUI's `@main` lifecycle and `NSApplicationDelegateAdaptor` plumbing; it
/// renders nothing of its own. A SwiftUI `WindowGroup` was deliberately avoided: its automatic
/// "close the last window -> maybe terminate" behavior fights the "close != quit" tray-app model
/// this app needs (see AppDelegate's header note), so AppDelegate manages the one real NSWindow by hand.
@main
struct NotiLinkMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        Settings {
            EmptyView()
        }
    }
}
