import Foundation

/// The `{v, type, id, ts, payload}` JSON envelope carried by every type-0x01 frame.
/// Per docs/PROTOCOL.md: unknown `type` values and unknown fields inside a known payload
/// must be ignored, never treated as an error (forward-compat hook).
public struct Envelope: Codable {
    public let v: Int
    public let type: String
    public let id: String
    public let ts: Int64
    public let payload: JSONValue

    public init(v: Int = 1, type: String, id: String = UUID().uuidString, ts: Int64? = nil, payload: JSONValue) {
        self.v = v
        self.type = type
        self.id = id
        self.ts = ts ?? Int64(Date().timeIntervalSince1970 * 1000)
        self.payload = payload
    }

    public static func make<T: Encodable>(type: MessageType, payload: T) throws -> Envelope {
        Envelope(type: type.rawValue, payload: try JSONValue.encoded(payload))
    }

    public func decodedPayload<T: Decodable>(as type: T.Type) throws -> T {
        try payload.decoded(as: type)
    }

    public func encodeToData() throws -> Data {
        try JSONEncoder().encode(self)
    }

    public static func decode(from data: Data) throws -> Envelope {
        try JSONDecoder().decode(Envelope.self, from: data)
    }
}
