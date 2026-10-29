import WebKit

/// Hosts the real UI (windows/NotiLink.App/wwwroot/, copied into the app bundle's Resources by
/// Scripts/build_app.sh — see Package.swift's header note) in a WKWebView, the same role
/// MainWindow.xaml.cs's WebView2 host plays on Windows.
final class ScriptMessageHandler: NSObject, WKScriptMessageHandler {
    private let appState: AppState
    init(appState: AppState) { self.appState = appState }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let dict = message.body as? [String: Any] else { return }
        appState.handleAction(dict)
    }
}

final class WebViewNavigationDelegate: NSObject, WKNavigationDelegate {
    private let appState: AppState
    init(appState: AppState) { self.appState = appState }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        appState.webViewDidBecomeReady()
    }
}

enum WebViewHost {
    // WKWebView holds its message handler/navigation delegate weakly — keep strong refs here.
    private static var messageHandler: ScriptMessageHandler?
    private static var navigationDelegate: WebViewNavigationDelegate?

    static func makeWebView(appState: AppState) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        let handler = ScriptMessageHandler(appState: appState)
        messageHandler = handler
        configuration.userContentController.add(handler, name: "notilink")

        let webView = WKWebView(frame: .zero, configuration: configuration)
        let navDelegate = WebViewNavigationDelegate(appState: appState)
        navigationDelegate = navDelegate
        webView.navigationDelegate = navDelegate

        appState.runJS = { [weak webView] script in
            webView?.evaluateJavaScript(script, completionHandler: nil)
        }

        if let wwwrootURL = Bundle.main.url(forResource: "wwwroot", withExtension: nil) {
            let indexURL = wwwrootURL.appendingPathComponent("index.html")
            webView.loadFileURL(indexURL, allowingReadAccessTo: wwwrootURL)
        } else {
            appState.diagLog.log("wwwroot not found in app bundle Resources — was this built via Scripts/build_app.sh?")
        }

        return webView
    }
}
