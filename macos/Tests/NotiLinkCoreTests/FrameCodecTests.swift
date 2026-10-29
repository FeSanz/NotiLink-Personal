import Testing
import Foundation
@testable import NotiLinkCore

struct FrameCodecTests {
    @Test func encodeDecodeJsonFrameRoundTrip() throws {
        let payload = Data(#"{"hello":"world"}"#.utf8)
        let encoded = FrameCodec.encode(type: .json, payload: payload)

        // 5-byte header, then the payload bytes verbatim.
        #expect(encoded.count == FrameCodec.headerLength + payload.count)

        let decoder = FrameDecoder()
        let frames = try decoder.feed(encoded)
        #expect(frames == [Frame(type: .json, payload: payload)])
    }

    @Test func encodeDecodeBinaryFrameRoundTrip() throws {
        let payload = Data((0..<256).map { UInt8($0 % 256) })
        let encoded = FrameCodec.encode(type: .binary, payload: payload)

        let decoder = FrameDecoder()
        let frames = try decoder.feed(encoded)
        #expect(frames == [Frame(type: .binary, payload: payload)])
    }

    @Test func lengthHeaderIsBigEndianAndExcludesHeaderItself() {
        let payload = Data(repeating: 0xAB, count: 300)
        let encoded = FrameCodec.encode(type: .json, payload: payload)

        // 300 == 0x0000012C
        #expect(Array(encoded.prefix(4)) == [0x00, 0x00, 0x01, 0x2C])
        #expect(encoded[encoded.startIndex + 4] == FrameType.json.rawValue)
    }

    @Test func incrementalFeedAcrossMultipleChunksYieldsOneFrame() throws {
        let payload = Data(#"{"a":1}"#.utf8)
        let encoded = FrameCodec.encode(type: .json, payload: payload)

        let decoder = FrameDecoder()
        var frames: [Frame] = []
        // Feed one byte at a time — simulates a socket delivering partial reads.
        for byte in encoded {
            frames += try decoder.feed(Data([byte]))
        }
        #expect(frames == [Frame(type: .json, payload: payload)])
    }

    @Test func multipleFramesInOneChunkAreAllDecoded() throws {
        let a = FrameCodec.encode(type: .json, payload: Data(#"{"n":1}"#.utf8))
        let b = FrameCodec.encode(type: .json, payload: Data(#"{"n":2}"#.utf8))
        var combined = a
        combined.append(b)

        let decoder = FrameDecoder()
        let frames = try decoder.feed(combined)
        #expect(frames.count == 2)
        #expect(frames[0].payload == Data(#"{"n":1}"#.utf8))
        #expect(frames[1].payload == Data(#"{"n":2}"#.utf8))
    }

    @Test func oversizedLengthIsRejected() {
        var header = Data()
        let tooLarge = FrameCodec.maxFrameLength + 1
        header.append(UInt8((tooLarge >> 24) & 0xFF))
        header.append(UInt8((tooLarge >> 16) & 0xFF))
        header.append(UInt8((tooLarge >> 8) & 0xFF))
        header.append(UInt8(tooLarge & 0xFF))
        header.append(FrameType.json.rawValue)

        let decoder = FrameDecoder()
        #expect(throws: FrameCodecError.frameTooLarge(length: tooLarge)) {
            try decoder.feed(header)
        }
    }

    @Test func unknownTypeByteIsRejected() {
        let payload = Data(#"{}"#.utf8)
        var frame = FrameCodec.encode(type: .json, payload: payload)
        frame[frame.startIndex + 4] = 0xFF // corrupt the type byte

        let decoder = FrameDecoder()
        #expect(throws: FrameCodecError.unknownType(0xFF)) {
            try decoder.feed(frame)
        }
    }

    // MARK: - Envelope

    @Test func envelopeRoundTripPreservesKnownFieldsAndIgnoresUnknownOnes() throws {
        struct Payload: Codable, Equatable { let code: String }
        let envelope = try Envelope.make(type: .pairCode, payload: Payload(code: "482913"))
        let data = try envelope.encodeToData()

        // Simulate a future server adding an extra top-level field; must not break decoding.
        var json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        json["futureField"] = "ignore me"
        let patched = try JSONSerialization.data(withJSONObject: json)

        let decoded = try Envelope.decode(from: patched)
        #expect(decoded.type == MessageType.pairCode.rawValue)
        #expect(decoded.v == 1)
        let payload = try decoded.decodedPayload(as: Payload.self)
        #expect(payload == Payload(code: "482913"))
    }

    @Test func unknownEnvelopeTypeDoesNotThrowWhenJustReadingTypeString() throws {
        let envelope = Envelope(type: "some.future.message", payload: .object(["x": .number(1)]))
        let data = try envelope.encodeToData()
        let decoded = try Envelope.decode(from: data)
        #expect(decoded.type == "some.future.message")
    }
}
