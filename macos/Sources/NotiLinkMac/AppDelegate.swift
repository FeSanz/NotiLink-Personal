import AppKit

/// Pure-AppKit app shell (no SwiftUI window — see NotiLinkMacApp.swift's header note). Mirrors
/// MainWindow.xaml.cs + the tray-icon half of this project's Windows build: closing the window
/// hides it rather than quitting (same "close != quit" behavior as Discord/Spotify/the Windows
/// build), and only the status-item "Salir" really exits.
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let appState = AppState(pcName: Host.current().localizedName ?? "Mac")

    private var window: NSWindow!
    private var statusItem: NSStatusItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)

        appState.start()

        let webView = WebViewHost.makeWebView(appState: appState)
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1100, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered, defer: false
        )
        window.title = "NotiLink"
        window.contentView = webView
        window.delegate = self
        window.center()
        window.makeKeyAndOrderFront(nil)
        self.window = window

        setUpStatusItem()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// "×"/red-button close hides the window instead of closing it — matches
    /// windows/NotiLink.App's own "Close ≠ quit" design decision (see CLAUDE.md).
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        sender.orderOut(nil)
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        appState.stop()
    }

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.title = "NL"

        let menu = NSMenu()
        let showItem = NSMenuItem(title: "Mostrar NotiLink", action: #selector(showWindow), keyEquivalent: "")
        showItem.target = self
        menu.addItem(showItem)
        menu.addItem(.separator())
        let quitItem = NSMenuItem(title: "Salir", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)

        item.menu = menu
        statusItem = item
    }

    @objc private func showWindow() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }
}
