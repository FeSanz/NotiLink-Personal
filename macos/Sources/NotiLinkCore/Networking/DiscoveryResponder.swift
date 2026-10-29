import Foundation
import Network

/// UDP discovery responder (port 45211, per docs/PROTOCOL.md). Phone broadcasts a plain,
/// unframed JSON datagram `{"type":"notilink.discover","deviceName":...}`; we reply unicast
/// to the sender with `{"type":"notilink.announce","pcName":...,"tcpPort":...}`.
public enum DiscoveryResponderError: Error {
    case invalidPort
}

public final class DiscoveryResponder {
    public static let port: UInt16 = 45211

    public var pcName: String
    public let tcpPort: UInt16
    public var onError: ((Error) -> Void)?

    private var listener: NWListener?
    private let queue = DispatchQueue(label: "notilink.discovery")

    public init(pcName: String, tcpPort: UInt16) {
        self.pcName = pcName
        self.tcpPort = tcpPort
    }

    public func start() throws {
        let params = NWParameters.udp
        params.allowLocalEndpointReuse = true

        guard let port = NWEndpoint.Port(rawValue: Self.port) else {
            throw DiscoveryResponderError.invalidPort
        }
        let listener = try NWListener(using: params, on: port)
        listener.newConnectionHandler = { [weak self] connection in
            self?.handle(connection)
        }
        listener.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state {
                self?.onError?(error)
            }
        }
        listener.start(queue: queue)
        self.listener = listener
    }

    public func stop() {
        listener?.cancel()
        listener = nil
    }

    private func handle(_ connection: NWConnection) {
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard case .ready = state, let connection else { return }
            self?.receiveOnce(connection)
        }
        connection.start(queue: queue)
    }

    private func receiveOnce(_ connection: NWConnection) {
        connection.receiveMessage { [weak self] data, _, _, error in
            defer { connection.cancel() }
            guard let self, let data, error == nil else { return }
            self.respond(to: data, on: connection)
        }
    }

    private func respond(to data: Data, on connection: NWConnection) {
        guard let discover = try? JSONDecoder().decode(DiscoveryDiscover.self, from: data),
              discover.type == "notilink.discover" else { return }

        let announce = DiscoveryAnnounce(pcName: pcName, tcpPort: Int(tcpPort))
        guard let replyData = try? JSONEncoder().encode(announce) else { return }

        connection.send(content: replyData, completion: .contentProcessed { [weak self] error in
            if let error { self?.onError?(error) }
        })
    }
}
