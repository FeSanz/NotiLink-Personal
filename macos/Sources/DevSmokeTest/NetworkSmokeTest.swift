import Foundation
import Network
import NotiLinkCore

/// Exercises TcpServer's real handshake/pairing state machine and DiscoveryResponder's real
/// UDP round-trip over loopback — no mocks, matching this project's "root-cause from a real
/// capture, don't guess" culture. Written against plain Network.framework + semaphores since
/// XCTest/swift-testing aren't available in this environment (see main.swift's header note).

private func wait(_ semaphore: DispatchSemaphore, _ name: String, seconds: Double = 3) -> Bool {
    let ok = semaphore.wait(timeout: .now() + seconds) == .success
    if !ok { print("FAIL - timed out waiting for: \(name)") }
    return ok
}

/// A minimal fake-phone TCP client: connects, lets you send raw envelope frames, and collects
/// every decoded JSON frame it receives back.
private final class FakePhone {
    let connection: NWConnection
    private let decoder = FrameDecoder()
    private let queue = DispatchQueue(label: "fakephone")
    private let lock = NSLock()
    private var receivedEnvelopes: [Envelope] = []
    var onEnvelope: ((Envelope) -> Void)?

    init(host: String, port: UInt16) {
        connection = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
    }

    func connect(readySemaphore: DispatchSemaphore) {
        connection.stateUpdateHandler = { state in
            if case .ready = state { readySemaphore.signal() }
        }
        connection.start(queue: queue)
        receiveLoop()
    }

    private func receiveLoop() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, _ in
            guard let self else { return }
            if let data, !data.isEmpty {
                if let frames = try? self.decoder.feed(data) {
                    for frame in frames where frame.type == .json {
                        if let envelope = try? Envelope.decode(from: frame.payload) {
                            self.lock.lock(); self.receivedEnvelopes.append(envelope); self.lock.unlock()
                            self.onEnvelope?(envelope)
                        }
                    }
                }
            }
            if !isComplete { self.receiveLoop() }
        }
    }

    func send<T: Encodable>(type: MessageType, payload: T) throws {
        let envelope = try Envelope.make(type: type, payload: payload)
        let json = try envelope.encodeToData()
        let frame = FrameCodec.encode(type: .json, payload: json)
        connection.send(content: frame, completion: .contentProcessed { _ in })
    }

    func envelopes(ofType type: MessageType) -> [Envelope] {
        lock.lock(); defer { lock.unlock() }
        return receivedEnvelopes.filter { $0.type == type.rawValue }
    }

    func close() { connection.cancel() }
}

private final class TestDelegate: TcpServerDelegate {
    var onConnected: ((String, String, Bool) -> Void)?
    var onDisconnected: ((String) -> Void)?
    var onPairingCode: ((String, String) -> Void)?
    var onReceived: ((String, Envelope) -> Void)?

    func tcpServer(_ server: TcpServer, deviceConnected deviceId: String, deviceName: String, isReconnect: Bool) {
        onConnected?(deviceId, deviceName, isReconnect)
    }
    func tcpServer(_ server: TcpServer, deviceDisconnected deviceId: String) {
        onDisconnected?(deviceId)
    }
    func tcpServer(_ server: TcpServer, devicePairingCodeShown deviceId: String, code: String) {
        onPairingCode?(deviceId, code)
    }
    func tcpServer(_ server: TcpServer, deviceId: String, received envelope: Envelope) {
        onReceived?(deviceId, envelope)
    }
    func tcpServer(_ server: TcpServer, deviceId: String, receivedBinary data: Data) {}
}

func runNetworkSmokeTests() -> Int {
    var failures = 0
    func check(_ condition: Bool, _ name: String) {
        if condition { print("ok   - \(name)") }
        else { print("FAIL - \(name)"); failures += 1 }
    }

    let tmpDir = FileManager.default.temporaryDirectory.appendingPathComponent("notilink-smoketest-\(UUID().uuidString)")
    // In-memory token store: the real Keychain can hang indefinitely waiting for a confirmation
    // sheet when an unsigned dev binary (freshly rebuilt, no stable code signature) is run
    // headlessly with no Window Server session to show it — confirmed live while writing this
    // test. KeychainTokenStore is still what NotiLinkMac uses for real; see TokenStoring.swift.
    let store = PairedDeviceStore(directory: tmpDir, tokenStore: InMemoryTokenStore())
    let logDir = tmpDir.appendingPathComponent("logs")
    let diagLog = PcDiagLog(directory: logDir)
    let server = TcpServer(pcName: "Smoke-Test-Mac", pairedDeviceStore: store, diagLog: diagLog)
    let delegate = TestDelegate()
    server.delegate = delegate

    do {
        try server.start()
    } catch {
        print("FAIL - server.start() threw \(error)")
        return failures + 1
    }
    defer {
        server.stop()
        Thread.sleep(forTimeInterval: 0.3)
        if let logData = try? Data(contentsOf: logDir.appendingPathComponent("pc.log")),
           let logText = String(data: logData, encoding: .utf8) {
            print("--- pc.log ---")
            print(logText)
            print("--- end pc.log ---")
        }
        try? FileManager.default.removeItem(at: tmpDir)
    }

    // --- First-time pairing flow ---
    let readySem = DispatchSemaphore(value: 0)
    let pairCodeSem = DispatchSemaphore(value: 0)
    let pairTokenSem = DispatchSemaphore(value: 0)
    let connectedSem = DispatchSemaphore(value: 0)
    let notificationSem = DispatchSemaphore(value: 0)

    var shownCode: String?
    delegate.onPairingCode = { _, code in shownCode = code; pairCodeSem.signal() }
    var connectedDeviceName: String?
    var connectedIsReconnect: Bool?
    delegate.onConnected = { _, name, isReconnect in
        connectedDeviceName = name
        connectedIsReconnect = isReconnect
        connectedSem.signal()
    }
    var sawNotification = false
    delegate.onReceived = { _, envelope in
        if envelope.type == MessageType.notificationPosted.rawValue { sawNotification = true; notificationSem.signal() }
    }

    let phone = FakePhone(host: "127.0.0.1", port: TcpServer.port)
    phone.onEnvelope = { envelope in
        if envelope.type == MessageType.pairToken.rawValue { pairTokenSem.signal() }
    }
    phone.connect(readySemaphore: readySem)
    check(wait(readySem, "fake phone TCP connect"), "fake phone connects to TcpServer")

    let deviceId = "smoke-device-1"
    try? phone.send(type: .handshakeHello, payload: HandshakeHello(
        deviceId: deviceId, deviceName: "Smoke Phone", deviceModel: "Smoke Model",
        pairingToken: nil, protocolVersion: 1, pairingTokens: nil
    ))
    check(wait(pairCodeSem, "pair.code received"), "server sent pair.code for unpaired device")
    check(shownCode?.count == 6, "pair.code is 6 digits")

    try? phone.send(type: .pairConfirm, payload: PairConfirm(confirmed: true))
    check(wait(connectedSem, "deviceConnected after pair.confirm"), "device considered connected after pairing")
    check(connectedDeviceName == "Smoke Phone", "connected device name matches handshake.deviceName")
    check(connectedIsReconnect == false, "fresh pairing reports isReconnect == false")
    check(wait(pairTokenSem, "pair.token received"), "phone received pair.token")

    let pairTokenEnvelopes = phone.envelopes(ofType: .pairToken)
    check(!pairTokenEnvelopes.isEmpty, "phone received pair.token")
    let issuedToken = (try? pairTokenEnvelopes.first?.decodedPayload(as: PairToken.self))?.token
    check(issuedToken != nil && !(issuedToken!.isEmpty), "issued token is non-empty")

    // Normal operation: a notification.posted after pairing should reach the delegate.
    try? phone.send(type: .notificationPosted, payload: NotificationPosted(
        key: "0|com.example|1|null|1000", packageName: "com.example", appLabel: "Example",
        title: "Hi", text: "Hello from smoke test", subText: nil, style: "plain", bigText: nil,
        messages: nil, isGroupConversation: false, inboxLines: nil, hasImage: false,
        imageTransferId: nil, hasAvatar: false, avatarTransferId: nil, accentColor: nil,
        hasProgress: false, progress: 0, progressMax: 0, progressIndeterminate: false,
        postedAt: 0, isOngoing: false, isClearable: true, actions: []
    ))
    check(wait(notificationSem, "notification.posted forwarded to delegate"), "notification.posted reaches delegate once established")
    check(sawNotification, "delegate actually saw notification.posted")

    check(store.record(forDeviceId: deviceId) != nil, "PairedDeviceStore persisted the new device")
    check(store.isValid(deviceId: deviceId, candidateTokens: [issuedToken ?? ""]), "issued token validates against the store")

    phone.close()

    // --- Reconnect with the now-valid token should skip pairing entirely ---
    let readySem2 = DispatchSemaphore(value: 0)
    let connectedSem2 = DispatchSemaphore(value: 0)
    var reconnectIsReconnect: Bool?
    delegate.onConnected = { _, _, isReconnect in
        reconnectIsReconnect = isReconnect
        connectedSem2.signal()
    }

    let phone2 = FakePhone(host: "127.0.0.1", port: TcpServer.port)
    phone2.connect(readySemaphore: readySem2)
    check(wait(readySem2, "second fake phone connects"), "reconnect: TCP connects")
    try? phone2.send(type: .handshakeHello, payload: HandshakeHello(
        deviceId: deviceId, deviceName: "Smoke Phone", deviceModel: "Smoke Model",
        pairingToken: issuedToken, protocolVersion: 1, pairingTokens: [issuedToken ?? ""]
    ))
    check(wait(connectedSem2, "reconnect: deviceConnected fires without pairing"), "known token skips pairing on reconnect")
    check(reconnectIsReconnect == true, "reconnect reports isReconnect == true")
    phone2.close()

    // --- UDP discovery round trip ---
    let discovery = DiscoveryResponder(pcName: "Smoke-Test-Mac", tcpPort: TcpServer.port)
    discovery.onError = { print("    discovery error: \($0)") }
    do {
        try discovery.start()
    } catch {
        print("FAIL - discovery.start() threw \(error)")
        failures += 1
    }
    defer { discovery.stop() }
    Thread.sleep(forTimeInterval: 0.2) // let the UDP listener actually reach .ready before we send

    let udpSocketFd = socket(AF_INET, SOCK_DGRAM, 0)
    check(udpSocketFd >= 0, "raw UDP socket created")
    var timeout = timeval(tv_sec: 3, tv_usec: 0)
    setsockopt(udpSocketFd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = DiscoveryResponder.port.bigEndian
    addr.sin_addr.s_addr = inet_addr("127.0.0.1")

    let discoverPayload = try! JSONEncoder().encode(DiscoveryDiscover(type: "notilink.discover", deviceName: "Smoke Phone"))
    let sendResult = withUnsafePointer(to: &addr) { ptr -> Int in
        ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPtr in
            discoverPayload.withUnsafeBytes { buf in
                sendto(udpSocketFd, buf.baseAddress, buf.count, 0, sockaddrPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
    }
    check(sendResult > 0, "UDP discover datagram sent")

    var buffer = [UInt8](repeating: 0, count: 4096)
    let received = recv(udpSocketFd, &buffer, buffer.count, 0)
    close(udpSocketFd)
    check(received > 0, "UDP announce reply received")
    if received > 0 {
        let announce = try? JSONDecoder().decode(DiscoveryAnnounce.self, from: Data(buffer[0..<received]))
        check(announce?.type == "notilink.announce", "announce has correct type")
        check(announce?.pcName == "Smoke-Test-Mac", "announce carries pcName")
        check(announce?.tcpPort == Int(TcpServer.port), "announce carries tcpPort")
    }

    return failures
}
