import Foundation
import Network

public protocol TcpServerDelegate: AnyObject {
    /// A phone finished handshake/pairing (or reconnected with an already-valid token) and is
    /// now a live, routable connection.
    func tcpServer(_ server: TcpServer, deviceConnected deviceId: String, deviceName: String, isReconnect: Bool)
    func tcpServer(_ server: TcpServer, deviceDisconnected deviceId: String)
    /// A 6-digit pairing code was generated for a not-yet-paired phone — show it in the UI.
    func tcpServer(_ server: TcpServer, devicePairingCodeShown deviceId: String, code: String)
    /// Every envelope except `heartbeat` (swallowed internally) is forwarded here.
    func tcpServer(_ server: TcpServer, deviceId: String, received envelope: Envelope)
    /// A binary (0x02) frame, immediately following some prior JSON frame — association with a
    /// specific transferId is the delegate's responsibility (purely positional per the protocol).
    func tcpServer(_ server: TcpServer, deviceId: String, receivedBinary data: Data)
}

public enum TcpServerError: Error {
    case invalidPort
    case notConnected
}

/// TCP server (port 45212, per docs/PROTOCOL.md) — PC is always the server, phones are always
/// clients, and the server supports several phones concurrently, each identified by its own
/// deviceId established once at handshake and then purely connection-scoped (the protocol
/// carries no deviceId on subsequent messages).
public final class TcpServer {
    public static let port: UInt16 = 45212
    private static let heartbeatInterval: TimeInterval = 15
    private static let silenceTimeout: TimeInterval = 45
    private static let maintenanceInterval: TimeInterval = 10

    public weak var delegate: TcpServerDelegate?
    public var pcName: String

    private let pairedDeviceStore: PairedDeviceStore
    private let diagLog: PcDiagLog?
    private let queue = DispatchQueue(label: "notilink.tcp")

    private var listener: NWListener?
    private var maintenanceTimer: DispatchSourceTimer?

    /// Connections not yet past handshake — keyed by identity, not deviceId (unknown until then).
    private var pendingConnections: [ObjectIdentifier: Connection] = [:]
    /// Established connections, keyed by deviceId (the thing every send/route operates on).
    private var connections: [String: Connection] = [:]

    public init(pcName: String, pairedDeviceStore: PairedDeviceStore, diagLog: PcDiagLog? = nil) {
        self.pcName = pcName
        self.pairedDeviceStore = pairedDeviceStore
        self.diagLog = diagLog
    }

    public func start() throws {
        guard let port = NWEndpoint.Port(rawValue: Self.port) else { throw TcpServerError.invalidPort }
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true

        let listener = try NWListener(using: params, on: port)
        listener.newConnectionHandler = { [weak self] nwConnection in
            self?.accept(nwConnection)
        }
        listener.stateUpdateHandler = { [weak self] state in
            self?.diagLog?.log("TcpServer listener state: \(state)")
            if case .failed(let error) = state {
                self?.diagLog?.log("TcpServer listener failed: \(error)")
            }
        }
        listener.start(queue: queue)
        self.listener = listener

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.maintenanceInterval, repeating: Self.maintenanceInterval)
        timer.setEventHandler { [weak self] in self?.maintenanceTick() }
        timer.resume()
        maintenanceTimer = timer

        diagLog?.log("TcpServer started on port \(Self.port)")
    }

    public func stop() {
        listener?.cancel()
        listener = nil
        maintenanceTimer?.cancel()
        maintenanceTimer = nil
        for connection in connections.values { connection.nwConnection.cancel() }
        for connection in pendingConnections.values { connection.nwConnection.cancel() }
        connections.removeAll()
        pendingConnections.removeAll()
    }

    // MARK: - Sending

    public func send<T: Encodable>(deviceId: String, type: MessageType, payload: T) throws {
        guard let connection = connections[deviceId] else { throw TcpServerError.notConnected }
        let envelope = try Envelope.make(type: type, payload: payload)
        try write(envelope: envelope, on: connection)
    }

    public func sendBinary(deviceId: String, data: Data) throws {
        guard let connection = connections[deviceId] else { throw TcpServerError.notConnected }
        let frame = FrameCodec.encode(type: .binary, payload: data)
        connection.nwConnection.send(content: frame, completion: .contentProcessed { [weak self] error in
            if let error { self?.diagLog?.log("send() failed: \(error)") }
        })
    }

    public func connectedDeviceIds() -> [String] {
        Array(connections.keys)
    }

    /// The remote IPv4/IPv6 address of a connected device's live socket — used as the ADB
    /// target for screen/audio sharing. Mirrors MainViewModel.cs's `_deviceIps`: there's no
    /// separate discovery step, this is just the peer address of the already-connected socket.
    public func remoteAddress(forDeviceId deviceId: String) -> String? {
        connections[deviceId]?.remoteHost
    }

    public func disconnect(deviceId: String) {
        connections[deviceId]?.nwConnection.cancel()
    }

    // MARK: - Accepting connections

    private func accept(_ nwConnection: NWConnection) {
        let connection = Connection(nwConnection: nwConnection)
        pendingConnections[ObjectIdentifier(connection)] = connection

        nwConnection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }
            switch state {
            case .ready:
                self.receiveLoop(connection)
            case .failed, .cancelled:
                self.handleClose(connection)
            default:
                break
            }
        }
        nwConnection.start(queue: queue)
    }

    private func receiveLoop(_ connection: Connection) {
        connection.nwConnection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self, weak connection] data, _, isComplete, error in
            guard let self, let connection else { return }
            if let data, !data.isEmpty {
                connection.lastSeen = Date()
                self.ingest(data, on: connection)
            }
            if isComplete || error != nil {
                self.handleClose(connection)
                return
            }
            self.receiveLoop(connection)
        }
    }

    private func ingest(_ data: Data, on connection: Connection) {
        let frames: [Frame]
        do {
            frames = try connection.decoder.feed(data)
        } catch {
            diagLog?.log("Frame decode error on connection, closing: \(error)")
            connection.nwConnection.cancel()
            return
        }

        for frame in frames {
            switch frame.type {
            case .json:
                handleJsonFrame(frame.payload, on: connection)
            case .binary:
                guard let deviceId = connection.deviceId else { continue } // binary before handshake is a protocol violation; drop it
                delegate?.tcpServer(self, deviceId: deviceId, receivedBinary: frame.payload)
            }
        }
    }

    // MARK: - Handshake / pairing / routing

    private func handleJsonFrame(_ payload: Data, on connection: Connection) {
        guard let envelope = try? Envelope.decode(from: payload) else {
            diagLog?.log("Malformed JSON envelope, ignoring frame")
            return
        }

        guard connection.deviceId != nil else {
            if connection.pendingDeviceId == nil {
                handlePreHandshake(envelope, on: connection)
            } else {
                dispatchPending(envelope, on: connection)
            }
            return
        }

        if envelope.type == MessageType.heartbeat.rawValue {
            return // lastSeen already bumped on any byte received; nothing else to do
        }

        delegate?.tcpServer(self, deviceId: connection.deviceId!, received: envelope)
    }

    private func handlePreHandshake(_ envelope: Envelope, on connection: Connection) {
        guard envelope.type == MessageType.handshakeHello.rawValue,
              let hello = try? envelope.decodedPayload(as: HandshakeHello.self) else {
            diagLog?.log("First frame on a new connection wasn't handshake.hello; closing")
            connection.nwConnection.cancel()
            return
        }

        connection.pendingDeviceId = hello.deviceId
        connection.pendingDeviceName = hello.deviceName
        connection.pendingDeviceModel = hello.deviceModel

        var candidateTokens = hello.pairingTokens ?? []
        if let legacy = hello.pairingToken { candidateTokens.append(legacy) }

        if pairedDeviceStore.isValid(deviceId: hello.deviceId, candidateTokens: candidateTokens) {
            diagLog?.log("handshake.hello from \(hello.deviceId): token valid, skipping pairing")
            establish(connection, deviceId: hello.deviceId, isReconnect: true)
        } else {
            let code = String(format: "%06d", Int.random(in: 0...999_999))
            connection.pendingPairCode = code
            diagLog?.log("handshake.hello from \(hello.deviceId): no valid token (hadToken=\(!candidateTokens.isEmpty)); showing pair.code")
            do {
                try write(envelope: Envelope.make(type: .pairCode, payload: PairCode(code: code)), on: connection)
                delegate?.tcpServer(self, devicePairingCodeShown: hello.deviceId, code: code)
            } catch {
                diagLog?.log("Failed to send pair.code: \(error)")
            }
        }
    }

    /// Handles everything that can arrive while a connection is mid-pairing (after pair.code was
    /// sent, before the deviceId is "established"). Pulled out of handleJsonFrame's main switch
    /// since pre-handshake connections never reach the "established" dispatch path above.
    private func handlePairConfirm(_ confirm: PairConfirm, on connection: Connection) {
        guard confirm.confirmed,
              let deviceId = connection.pendingDeviceId,
              let deviceName = connection.pendingDeviceName,
              let deviceModel = connection.pendingDeviceModel else { return }

        let token = UUID().uuidString
        do {
            try pairedDeviceStore.pair(deviceId: deviceId, deviceName: deviceName, deviceModel: deviceModel, token: token)
            try write(envelope: Envelope.make(type: .pairToken, payload: PairToken(token: token)), on: connection)
            diagLog?.log("pair.confirm from \(deviceId): issued new token")
            establish(connection, deviceId: deviceId, isReconnect: false)
        } catch {
            diagLog?.log("Failed to persist pairing for \(deviceId): \(error)")
        }
    }

    private func establish(_ connection: Connection, deviceId: String, isReconnect: Bool) {
        pendingConnections.removeValue(forKey: ObjectIdentifier(connection))
        connection.deviceId = deviceId
        connections[deviceId]?.nwConnection.cancel() // a second connection for the same phone replaces the first
        connections[deviceId] = connection

        let name = pairedDeviceStore.record(forDeviceId: deviceId)?.deviceName ?? connection.pendingDeviceName ?? deviceId
        delegate?.tcpServer(self, deviceConnected: deviceId, deviceName: name, isReconnect: isReconnect)
    }

    private func handleClose(_ connection: Connection) {
        pendingConnections.removeValue(forKey: ObjectIdentifier(connection))
        guard let deviceId = connection.deviceId, connections[deviceId] === connection else { return }
        connections.removeValue(forKey: deviceId)
        diagLog?.log("Device \(deviceId) disconnected")
        delegate?.tcpServer(self, deviceDisconnected: deviceId)
    }

    private func write(envelope: Envelope, on connection: Connection) throws {
        let json = try envelope.encodeToData()
        let frame = FrameCodec.encode(type: .json, payload: json)
        connection.nwConnection.send(content: frame, completion: .contentProcessed { [weak self] error in
            if let error { self?.diagLog?.log("send() failed: \(error)") }
        })
    }

    // MARK: - Heartbeat send + silence watchdog

    private func maintenanceTick() {
        let now = Date()
        for connection in connections.values {
            if now.timeIntervalSince(connection.lastSeen) > Self.silenceTimeout {
                diagLog?.log("Watchdog: \(connection.deviceId ?? "?") silent for >\(Int(Self.silenceTimeout))s, force-closing")
                connection.nwConnection.cancel()
                handleClose(connection)
                continue
            }
            try? write(envelope: Envelope.make(type: .heartbeat, payload: EmptyPayload()), on: connection)
        }

        // Pending (pre-pairing) connections that never complete handshake/pairing within the
        // same window are just as dead — nothing recovers a stuck pairing flow on its own.
        for connection in pendingConnections.values where now.timeIntervalSince(connection.lastSeen) > Self.silenceTimeout {
            connection.nwConnection.cancel()
        }
    }

    // MARK: - Pre-handshake pair.confirm dispatch

    /// `handleJsonFrame` only forwards to the delegate once a connection is established; while
    /// pending, `pair.confirm` is the only message type it should ever see, so route it here
    /// instead of threading pairing state through the public delegate API.
    private func dispatchPending(_ envelope: Envelope, on connection: Connection) {
        guard envelope.type == MessageType.pairConfirm.rawValue,
              let confirm = try? envelope.decodedPayload(as: PairConfirm.self) else { return }
        handlePairConfirm(confirm, on: connection)
    }
}

extension TcpServer {
    /// Internal per-connection state. A class (not a struct) since identity matters — it's used
    /// as a dictionary key before deviceId is known, and mutated in place from callback closures.
    final class Connection {
        let nwConnection: NWConnection
        let decoder = FrameDecoder()
        var deviceId: String?
        var pendingDeviceId: String?
        var pendingDeviceName: String?
        var pendingDeviceModel: String?
        var pendingPairCode: String?
        var lastSeen = Date()

        var remoteHost: String? {
            guard case .hostPort(let host, _) = nwConnection.endpoint else { return nil }
            switch host {
            case .ipv4(let address): return "\(address)"
            case .ipv6(let address): return "\(address)"
            case .name(let name, _): return name
            @unknown default: return nil
            }
        }

        init(nwConnection: NWConnection) {
            self.nwConnection = nwConnection
        }
    }
}
