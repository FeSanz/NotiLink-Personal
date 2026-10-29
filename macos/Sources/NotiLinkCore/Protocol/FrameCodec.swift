import Foundation

/// Wire framing per docs/PROTOCOL.md: [4-byte big-endian length][1-byte type][payload].
/// `length` counts only the payload, never the 5-byte header itself.
public enum FrameType: UInt8 {
    case json = 0x01
    case binary = 0x02
}

public struct Frame: Equatable {
    public let type: FrameType
    public let payload: Data

    public init(type: FrameType, payload: Data) {
        self.type = type
        self.payload = payload
    }
}

public enum FrameCodecError: Error, Equatable {
    case frameTooLarge(length: UInt32)
    case unknownType(UInt8)
}

public struct FrameCodec {
    public static let headerLength = 5
    /// Matches TcpServer.MaxFrameLength / NotificationConnection.MAX_FRAME_LENGTH.
    public static let maxFrameLength: UInt32 = 8 * 1024 * 1024

    public static func encode(type: FrameType, payload: Data) -> Data {
        let length = UInt32(payload.count)
        var frame = Data(capacity: headerLength + payload.count)
        frame.append(UInt8((length >> 24) & 0xFF))
        frame.append(UInt8((length >> 16) & 0xFF))
        frame.append(UInt8((length >> 8) & 0xFF))
        frame.append(UInt8(length & 0xFF))
        frame.append(type.rawValue)
        frame.append(payload)
        return frame
    }
}

/// Incremental decoder: feed it arbitrarily-chunked bytes as they arrive from the socket,
/// get back zero or more complete frames once enough bytes have accumulated.
public final class FrameDecoder {
    private var buffer = Data()

    public init() {}

    public func feed(_ data: Data) throws -> [Frame] {
        buffer.append(data)
        var frames: [Frame] = []

        while buffer.count >= FrameCodec.headerLength {
            let start = buffer.startIndex
            let b0 = UInt32(buffer[start])
            let b1 = UInt32(buffer[start + 1])
            let b2 = UInt32(buffer[start + 2])
            let b3 = UInt32(buffer[start + 3])
            let length = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3

            if length > FrameCodec.maxFrameLength {
                throw FrameCodecError.frameTooLarge(length: length)
            }

            let totalLength = FrameCodec.headerLength + Int(length)
            guard buffer.count >= totalLength else { break }

            let typeByte = buffer[start + 4]
            guard let type = FrameType(rawValue: typeByte) else {
                throw FrameCodecError.unknownType(typeByte)
            }

            let payloadStart = start + FrameCodec.headerLength
            let payloadEnd = start + totalLength
            let payload = buffer.subdata(in: payloadStart..<payloadEnd)
            frames.append(Frame(type: type, payload: payload))
            buffer.removeSubrange(start..<payloadEnd)
        }

        return frames
    }
}
