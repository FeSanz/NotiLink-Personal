import Foundation
import NotiLinkCore

/// The bridge between TcpServer/DiscoveryResponder and the WKWebView-hosted UI — the macOS
/// equivalent of MainViewModel.cs + the inbound half of MainWindow.xaml.cs's bridge. Owns the
/// native side of the connection; WebViewHost owns only the WKWebView plumbing that calls into it.
public final class AppState: TcpServerDelegate {
    public let pairedDeviceStore: PairedDeviceStore
    public let iconCache: IconCache
    public let diagLog: PcDiagLog
    public let tcpServer: TcpServer
    public let discovery: DiscoveryResponder
    public let screenShare: ScreenShareService

    /// Set by WebViewHost once the real WKWebView exists. Calls queue up until the page itself
    /// has finished loading (`webViewDidBecomeReady`) — mirrors MainWindow.xaml.cs's
    /// `_pendingScripts`/`_webViewReady`/`RunJs` pattern.
    public var runJS: ((String) -> Void)?
    private var pendingScripts: [String] = []
    private var webViewReady = false

    private var notifications: [String: [String: NotificationItem]] = [:] // deviceId -> key -> item
    private var requestedIcons: Set<String> = [] // "deviceId|packageName", so we never ask twice

    /// Binary (0x02) frames carry no identifying metadata of their own — association with a
    /// specific transferId/purpose is purely positional (the next pending entry in the order its
    /// declaring JSON frame was processed). One global per-device FIFO is correct here because a
    /// single phone writes everything onto one TCP stream in send order; mirrors
    /// TcpServer.ExtractExpectedTransferIds (C#).
    private enum PendingTransfer {
        case avatar(key: String)
        case image(key: String)
        case messageImage(key: String, transferId: String, mimeType: String)
        case icon(packageName: String)
    }
    private var pendingTransfers: [String: [PendingTransfer]] = [:]

    public init(pcName: String) {
        pairedDeviceStore = PairedDeviceStore()
        iconCache = IconCache()
        diagLog = PcDiagLog()
        tcpServer = TcpServer(pcName: pcName, pairedDeviceStore: pairedDeviceStore, diagLog: diagLog)
        discovery = DiscoveryResponder(pcName: pcName, tcpPort: TcpServer.port)
        screenShare = ScreenShareService(diagLog: diagLog)
        tcpServer.delegate = self

        screenShare.onStatsUpdated = { [weak self] deviceId, stats in
            guard let self else { return }
            let resArg = stats.resolution.map { self.jsString($0) } ?? "null"
            let fpsArg = stats.fps.map { String($0) } ?? "null"
            self.evaluate("window.setScreenShareStats(\(self.jsString(deviceId)), \(fpsArg), \(resArg))")
        }
        screenShare.onStopped = { [weak self] deviceId, kind in
            guard let self else { return }
            switch kind {
            case .video: self.evaluate("window.setScreenShareStopped(\(self.jsString(deviceId)))")
            case .audio: self.evaluate("window.setAudioShareStopped(\(self.jsString(deviceId)))")
            }
            self.sendShareStatus(deviceId: deviceId)
        }
        screenShare.onAppStateChanged = { [weak self] deviceId, packageName, state in
            guard let self else { return }
            self.evaluate("window.setAppShareState(\(self.jsString(deviceId)), \(self.jsString(packageName)), \(self.jsString(state)))")
        }
        screenShare.onAppStopped = { [weak self] deviceId, packageName in
            guard let self else { return }
            self.evaluate("window.setAppShareStopped(\(self.jsString(deviceId)), \(self.jsString(packageName)))")
            self.sendShareStatus(deviceId: deviceId)
        }
    }

    public func start() {
        do { try tcpServer.start() } catch { diagLog.log("TcpServer failed to start: \(error)") }
        do { try discovery.start() } catch { diagLog.log("DiscoveryResponder failed to start: \(error)") }
        pushConnectionInfo()
    }

    /// Mirrors MainWindow.xaml.cs's `window.setQrCode(dataUrl, ip, others)` push — shown on the
    /// "Vincular dispositivo" screen so the phone can either scan the QR or type the IP manually.
    private func pushConnectionInfo() {
        let addresses = NetworkInfo.localIPv4Addresses()
        guard let primary = addresses.first else {
            diagLog.log("No local IPv4 address found — can't show pairing QR/IP")
            return
        }
        let qr = QrCode.dataUrl(for: primary) ?? ""
        let others = addresses.dropFirst().map { ["ip": $0] }
        evaluate("window.setQrCode(\(jsString(qr)), \(jsString(primary)), \(jsEncode(others)))")
    }

    public func stop() {
        tcpServer.stop()
        discovery.stop()
    }

    // MARK: - JS bridge plumbing

    public func webViewDidBecomeReady() {
        webViewReady = true
        for script in pendingScripts { runJS?(script) }
        pendingScripts.removeAll()
        pushDeviceList()
    }

    private func evaluate(_ script: String) {
        if webViewReady { runJS?(script) } else { pendingScripts.append(script) }
    }

    private func jsEncode<T: Encodable>(_ value: T) -> String {
        guard let data = try? JSONEncoder().encode(value), let text = String(data: data, encoding: .utf8) else { return "null" }
        return text
    }

    private func jsString(_ value: String) -> String { jsEncode(value) }

    private func pushDeviceList() {
        let connected = Set(tcpServer.connectedDeviceIds())
        let list = pairedDeviceStore.allRecords().map {
            JsDeviceListEntryDTO(deviceId: $0.deviceId, name: $0.deviceName, connected: connected.contains($0.deviceId))
        }
        evaluate("window.setDeviceList(\(jsEncode(list)))")
    }

    // MARK: - Inbound JS actions (app.js's `postToHost` catalog, MVP subset)

    public func handleAction(_ json: [String: Any]) {
        guard let action = json["action"] as? String else { return }
        switch action {
        case "delete":
            guard let deviceId = json["deviceId"] as? String, let key = json["key"] as? String else { return }
            try? tcpServer.send(deviceId: deviceId, type: .deleteRequest, payload: DeleteRequest(key: key))

        case "deleteAll":
            guard let deviceId = json["deviceId"] as? String else { return }
            try? tcpServer.send(deviceId: deviceId, type: .deleteRequestAll, payload: EmptyPayload())

        case "invoke":
            guard let deviceId = json["deviceId"] as? String,
                  let key = json["key"] as? String,
                  let actionIndex = json["actionIndex"] as? Int else { return }
            let replyText = json["replyText"] as? String
            try? tcpServer.send(deviceId: deviceId, type: .actionInvoke, payload: ActionInvoke(key: key, actionIndex: actionIndex, replyText: replyText))

        case "setAppAllowed":
            guard let deviceId = json["deviceId"] as? String,
                  let packageName = json["packageName"] as? String,
                  let allowed = json["allowed"] as? Bool else { return }
            try? tcpServer.send(deviceId: deviceId, type: .appFilterUpdate, payload: AppFilterUpdate(packageName: packageName, allowed: allowed))
            evaluate("window.setAppAllowedState(\(jsString(deviceId)), \(jsString(packageName)), \(allowed))")

        case "toggleConnection":
            guard let deviceId = json["deviceId"] as? String, let disconnect = json["disconnect"] as? Bool else { return }
            try? tcpServer.send(deviceId: deviceId, type: .connectionToggle, payload: ConnectionToggle(disconnect: disconnect))
            if disconnect { tcpServer.disconnect(deviceId: deviceId) }

        case "renameDevice":
            guard let deviceId = json["deviceId"] as? String, let name = json["name"] as? String else { return }
            try? pairedDeviceStore.rename(deviceId: deviceId, to: name)
            pushDeviceList()

        case "removeDevice":
            guard let deviceId = json["deviceId"] as? String else { return }
            tcpServer.disconnect(deviceId: deviceId)
            try? pairedDeviceStore.remove(deviceId: deviceId)
            notifications.removeValue(forKey: deviceId)
            pendingTransfers.removeValue(forKey: deviceId)
            pushDeviceList()

        case "shareScreen":
            guard let deviceId = json["deviceId"] as? String else { return }
            let settings = ScreenShareSettings(
                maxSize: json["maxSize"] as? Int ?? 0,
                maxFps: json["maxFps"] as? Int ?? 0,
                videoBitRateMbps: json["bitRate"] as? Int ?? 20,
                videoCodec: json["videoCodec"] as? String ?? "h264",
                videoBufferMs: json["bufferMs"] as? Int ?? 0,
                turnScreenOff: json["turnScreenOff"] as? Bool ?? false,
                stayAwake: json["stayAwake"] as? Bool ?? false,
                audio: json["audio"] as? Bool ?? false,
                dimPhone: json["dimPhone"] as? Bool ?? false
            )
            startVideoShare(deviceId: deviceId, settings: settings)

        case "shareAudio":
            guard let deviceId = json["deviceId"] as? String else { return }
            startAudioShare(deviceId: deviceId)

        case "stopScreenShare":
            guard let deviceId = json["deviceId"] as? String else { return }
            screenShare.stopVideo(deviceId: deviceId)
            sendShareStatus(deviceId: deviceId)

        case "stopAudioShare":
            guard let deviceId = json["deviceId"] as? String else { return }
            screenShare.stopAudio(deviceId: deviceId)
            sendShareStatus(deviceId: deviceId)

        case "stopAllShares":
            guard let deviceId = json["deviceId"] as? String else { return }
            screenShare.stopAll(deviceId: deviceId)
            screenShare.stopAllApps(deviceId: deviceId)
            sendShareStatus(deviceId: deviceId)

        case "shareApp":
            guard let deviceId = json["deviceId"] as? String,
                  let packageName = json["packageName"] as? String,
                  let label = json["label"] as? String else { return }
            let settings = ScreenShareSettings(
                maxSize: json["maxSize"] as? Int ?? 0,
                maxFps: json["maxFps"] as? Int ?? 0,
                videoBitRateMbps: json["bitRate"] as? Int ?? 20,
                videoCodec: json["videoCodec"] as? String ?? "h264",
                videoBufferMs: json["bufferMs"] as? Int ?? 0,
                turnScreenOff: false, stayAwake: false,
                audio: json["audio"] as? Bool ?? false,
                dimPhone: false
            )
            let appOptions = AppShareOptions(
                displaySize: json["displaySize"] as? String ?? "",
                dpi: json["dpi"] as? Int ?? 0,
                decorations: json["decorations"] as? Bool ?? false,
                imePolicy: json["imePolicy"] as? String ?? "local",
                keepOnClose: json["keepOnClose"] as? Bool ?? false,
                audio: json["audio"] as? Bool ?? false,
                alwaysOnTop: json["alwaysOnTop"] as? Bool ?? false,
                borderless: json["borderless"] as? Bool ?? false,
                restartApp: json["restartApp"] as? Bool ?? false,
                autoReturn: json["autoReturn"] as? Bool ?? true
            )
            startAppShare(deviceId: deviceId, packageName: packageName, label: label, settings: settings, appOptions: appOptions)

        case "stopAppShare":
            guard let deviceId = json["deviceId"] as? String, let packageName = json["packageName"] as? String else { return }
            screenShare.stopApp(deviceId: deviceId, packageName: packageName)
            sendShareStatus(deviceId: deviceId)

        case "returnAppShare":
            guard let deviceId = json["deviceId"] as? String, let packageName = json["packageName"] as? String else { return }
            Task { await screenShare.returnApp(deviceId: deviceId, packageName: packageName) }

        case "syncShareRecents":
            guard let deviceId = json["deviceId"] as? String,
                  let rawApps = json["apps"] as? [[String: Any]],
                  let updatedAt = json["updatedAt"] as? Int else { return }
            let apps = rawApps.compactMap { entry -> ShareRecentApp? in
                guard let packageName = entry["packageName"] as? String else { return nil }
                return ShareRecentApp(packageName: packageName, label: entry["label"] as? String ?? packageName)
            }
            try? tcpServer.send(deviceId: deviceId, type: .shareRecents, payload: ShareRecents(apps: apps, updatedAt: Int64(updatedAt)))

        default:
            break // media bar/PC-link/etc. — out of scope for this phase, intentionally ignored
        }
    }

    private func startAppShare(deviceId: String, packageName: String, label: String, settings: ScreenShareSettings, appOptions: AppShareOptions) {
        guard let ip = tcpServer.remoteAddress(forDeviceId: deviceId) else { return }
        let deviceName = pairedDeviceStore.record(forDeviceId: deviceId)?.deviceName ?? deviceId
        Task {
            let result = await screenShare.startApp(deviceId: deviceId, ip: ip, deviceName: deviceName,
                                                      packageName: packageName, label: label, settings: settings, appOptions: appOptions)
            evaluate("window.setAppShareStatus(\(jsString(deviceId)), \(jsString(packageName)), \(result.success), \(jsString(result.message)))")
            if result.success { sendShareStatus(deviceId: deviceId) }
        }
    }

    private func startVideoShare(deviceId: String, settings: ScreenShareSettings) {
        guard let ip = tcpServer.remoteAddress(forDeviceId: deviceId) else { return }
        let deviceName = pairedDeviceStore.record(forDeviceId: deviceId)?.deviceName ?? deviceId
        Task {
            let result = await screenShare.startVideo(deviceId: deviceId, ip: ip, deviceName: deviceName, settings: settings)
            evaluate("window.setScreenShareStatus(\(jsString(deviceId)), \(result.success), \(jsString(result.message)))")
            if result.success { sendShareStatus(deviceId: deviceId) }
        }
    }

    private func startAudioShare(deviceId: String) {
        guard let ip = tcpServer.remoteAddress(forDeviceId: deviceId) else { return }
        let deviceName = pairedDeviceStore.record(forDeviceId: deviceId)?.deviceName ?? deviceId
        Task {
            let result = await screenShare.startAudioOnly(deviceId: deviceId, ip: ip, deviceName: deviceName)
            evaluate("window.setAudioShareStatus(\(jsString(deviceId)), \(result.success), \(jsString(result.message)))")
            if result.success { sendShareStatus(deviceId: deviceId) }
        }
    }

    /// Mirrors MainViewModel.cs's `SendShareStatus` — pushes the phone's own real current share
    /// state over the wire (drives its "Compartiendo..." status notification), independent of
    /// the JS-facing `setScreenShareStatus`/`setAudioShareStatus` start-attempt replies above.
    private func sendShareStatus(deviceId: String) {
        let apps = screenShare.activeApps(deviceId: deviceId).map {
            ShareStatusApp(packageName: $0.packageName, label: $0.label, state: $0.state)
        }
        let status = ShareStatus(screenSharing: screenShare.isVideoActive(deviceId: deviceId),
                                  audioSharing: screenShare.isAudioActive(deviceId: deviceId),
                                  apps: apps)
        try? tcpServer.send(deviceId: deviceId, type: .shareStatus, payload: status)
    }

    // MARK: - TcpServerDelegate

    public func tcpServer(_ server: TcpServer, deviceConnected deviceId: String, deviceName: String, isReconnect: Bool) {
        if isReconnect {
            evaluate("window.showDeviceReconnected(\(jsString(deviceId)), \(jsString(deviceName)))")
        } else {
            evaluate("window.showPaired(\(jsString(deviceId)), \(jsString(deviceName)))")
        }
        pushDeviceList()
        // Always re-announce real current share state on every (re)connect, not just on change —
        // mirrors MainViewModel.DeviceConnected's fix for the phone's "Compartiendo..." status
        // notification getting stuck stale after a reconnect (see CLAUDE.md's reconnect-robustness
        // section). A fresh connection never has anything actually running yet, but the phone
        // doesn't know that until it's told.
        sendShareStatus(deviceId: deviceId)
    }

    public func tcpServer(_ server: TcpServer, deviceDisconnected deviceId: String) {
        // A disconnect (watchdog-triggered or real) always tears down any active scrcpy process
        // for that phone — mirrors MainViewModel.DeviceDisconnected's StopAllSharing() call.
        screenShare.stopAll(deviceId: deviceId)
        screenShare.stopAllApps(deviceId: deviceId)
        pushDeviceList()
    }

    public func tcpServer(_ server: TcpServer, devicePairingCodeShown deviceId: String, code: String) {
        evaluate("window.showPairingCode(\(jsString(code)))")
    }

    public func tcpServer(_ server: TcpServer, deviceId: String, received envelope: Envelope) {
        guard let type = MessageType(rawValue: envelope.type) else { return } // unknown type: ignore, forward-compat

        switch type {
        case .notificationPosted:
            guard let posted = try? envelope.decodedPayload(as: NotificationPosted.self) else { return }
            handleNotificationPosted(posted, deviceId: deviceId)

        case .notificationRemoved:
            guard let removed = try? envelope.decodedPayload(as: NotificationRemoved.self) else { return }
            notifications[deviceId]?.removeValue(forKey: removed.key)
            evaluate("window.removeNotification(\(jsString(deviceId)), \(jsString(removed.key)))")

        case .notificationSync:
            guard let sync = try? envelope.decodedPayload(as: NotificationSync.self) else { return }
            let keySet = Set(sync.keys)
            notifications[deviceId] = notifications[deviceId]?.filter { keySet.contains($0.key) }
            evaluate("window.syncNotificationKeys(\(jsString(deviceId)), \(jsEncode(sync.keys)))")

        case .batteryUpdate:
            guard let battery = try? envelope.decodedPayload(as: BatteryUpdate.self) else { return }
            handleBatteryUpdate(battery, deviceId: deviceId)

        case .appSync:
            guard let sync = try? envelope.decodedPayload(as: AppSync.self) else { return }
            handleAppSync(sync, deviceId: deviceId)

        case .iconData:
            guard let iconData = try? envelope.decodedPayload(as: IconData.self) else { return }
            pendingTransfers[deviceId, default: []].append(.icon(packageName: iconData.packageName))

        case .shareRequest:
            guard let request = try? envelope.decodedPayload(as: ShareRequest.self) else { return }
            // Deliberately not handled here: the saved screen-share quality settings live in the
            // page's own localStorage (see app.js), so JS decides what to actually start with and
            // calls back via the normal shareScreen/shareAudio actions — mirrors MainWindow.xaml.cs's
            // same reasoning for window.onPhoneShareRequest.
            let pkgArg = request.packageName.map { jsString($0) } ?? "null"
            let labelArg = request.label.map { jsString($0) } ?? "null"
            evaluate("window.onPhoneShareRequest(\(jsString(deviceId)), \(jsString(request.type)), \(pkgArg), \(labelArg))")

        case .shareStopRequest:
            screenShare.stopAll(deviceId: deviceId)
            sendShareStatus(deviceId: deviceId)

        case .shareStopApp:
            guard let stop = try? envelope.decodedPayload(as: ShareStopApp.self) else { return }
            screenShare.stopApp(deviceId: deviceId, packageName: stop.packageName)
            sendShareStatus(deviceId: deviceId)

        case .shareAppReturn:
            guard let request = try? envelope.decodedPayload(as: ShareAppReturn.self) else { return }
            Task { await screenShare.returnApp(deviceId: deviceId, packageName: request.packageName) }

        case .shareRecents:
            guard let recents = try? envelope.decodedPayload(as: ShareRecents.self) else { return }
            let appsDTO = recents.apps.map { ["packageName": $0.packageName, "label": $0.label] }
            evaluate("window.onPhoneShareRecents(\(jsString(deviceId)), \(jsEncode(appsDTO)), \(recents.updatedAt))")

        default:
            break // handshake/pairing types never reach here (consumed pre-establish by TcpServer)
        }
    }

    public func tcpServer(_ server: TcpServer, deviceId: String, receivedBinary data: Data) {
        guard var queue = pendingTransfers[deviceId], !queue.isEmpty else { return }
        let transfer = queue.removeFirst()
        pendingTransfers[deviceId] = queue

        switch transfer {
        case .avatar(let key):
            notifications[deviceId]?[key]?.avatarBytes = data
            evaluate("window.setAvatar(\(jsString(deviceId)), \(jsString(key)), \(jsString(dataUrl(data, mimeType: "image/jpeg") ?? "")))")
        case .image(let key):
            notifications[deviceId]?[key]?.imageBytes = data
            evaluate("window.setImage(\(jsString(deviceId)), \(jsString(key)), \(jsString(dataUrl(data, mimeType: "image/jpeg") ?? "")))")
        case .messageImage(let key, let transferId, let mimeType):
            evaluate("window.setMessageImage(\(jsString(deviceId)), \(jsString(key)), \(jsString(transferId)), \(jsString(dataUrl(data, mimeType: mimeType) ?? "")))")
        case .icon(let packageName):
            try? iconCache.store(data, for: packageName)
            evaluate("window.setIcon(\(jsString(packageName)), \(jsString(dataUrl(data, mimeType: "image/png") ?? "")))")
        }
    }

    // MARK: - Private handlers

    private func handleNotificationPosted(_ posted: NotificationPosted, deviceId: String) {
        let iconBytes = iconCache.data(for: posted.packageName)
        let item = NotificationItem.from(posted, iconBytes: iconBytes)
        notifications[deviceId, default: [:]][item.key] = item

        if iconBytes == nil { requestIconIfNeeded(packageName: posted.packageName, deviceId: deviceId) }

        // Strict order per docs/PROTOCOL.md: avatar -> top-level bigPicture -> each messages[i] image.
        var queue: [PendingTransfer] = []
        if posted.hasAvatar, posted.avatarTransferId != nil { queue.append(.avatar(key: item.key)) }
        if posted.hasImage, posted.imageTransferId != nil { queue.append(.image(key: item.key)) }
        for message in posted.messages ?? [] where message.hasImage {
            guard let transferId = message.imageTransferId else { continue }
            queue.append(.messageImage(key: item.key, transferId: transferId, mimeType: message.imageMimeType ?? "image/jpeg"))
        }
        pendingTransfers[deviceId, default: []].append(contentsOf: queue)

        evaluate("window.upsertNotification(\(jsString(deviceId)), \(jsEncode(item.toJsDTO())))")
    }

    private func handleBatteryUpdate(_ battery: BatteryUpdate, deviceId: String) {
        evaluate("window.setBattery(\(jsString(deviceId)), \(battery.level), \(battery.charging))")
        if let ssid = battery.wifiSsid {
            let levelArg = battery.wifiLevel.map(String.init) ?? "null"
            evaluate("window.setWifi(\(jsString(deviceId)), \(jsString(ssid)), \(levelArg))")
        } else {
            evaluate("window.setWifi(\(jsString(deviceId)), null, null)")
        }
        let cellularDTO = battery.cellular.map { JsCellularLineDTO(carrierName: $0.carrierName, level: $0.level) }
        evaluate("window.setCellular(\(jsString(deviceId)), \(jsEncode(cellularDTO)))")
    }

    private func handleAppSync(_ sync: AppSync, deviceId: String) {
        let dto = sync.apps.map {
            JsAppEntryDTO(packageName: $0.packageName, label: $0.label, allowed: $0.allowed,
                          iconDataUrl: dataUrl(iconCache.data(for: $0.packageName), mimeType: "image/png"),
                          isSystemApp: $0.isSystemApp)
        }
        evaluate("window.setAppList(\(jsString(deviceId)), \(jsEncode(dto)))")

        for app in sync.apps where iconCache.data(for: app.packageName) == nil {
            requestIconIfNeeded(packageName: app.packageName, deviceId: deviceId)
        }
    }

    private func requestIconIfNeeded(packageName: String, deviceId: String) {
        let requestKey = "\(deviceId)|\(packageName)"
        guard !requestedIcons.contains(requestKey) else { return }
        requestedIcons.insert(requestKey)
        try? tcpServer.send(deviceId: deviceId, type: .iconRequest, payload: IconRequest(packageName: packageName, transferId: UUID().uuidString))
    }
}
