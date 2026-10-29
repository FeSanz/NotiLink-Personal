import Foundation
import NotiLinkCore

// Mirrors Tests/NotiLinkCoreTests/FrameCodecTests.swift's assertions using plain `precondition`,
// since XCTest/swift-testing macro plugins are unavailable with this Command Line Tools install.
// Run with `swift run DevSmokeTest`. Delete this target once `swift test` works for real.

var failures = 0

func check(_ condition: Bool, _ name: String) {
    if condition {
        print("ok   - \(name)")
    } else {
        print("FAIL - \(name)")
        failures += 1
    }
}

func checkThrows<T>(_ name: String, _ body: () throws -> T) {
    do {
        _ = try body()
        print("FAIL - \(name) (expected throw, got none)")
        failures += 1
    } catch {
        print("ok   - \(name) (threw \(error))")
    }
}

// MARK: FrameCodec

do {
    let payload = Data(#"{"hello":"world"}"#.utf8)
    let encoded = FrameCodec.encode(type: .json, payload: payload)
    check(encoded.count == FrameCodec.headerLength + payload.count, "json frame length == header + payload")
    let frames = try FrameDecoder().feed(encoded)
    check(frames == [Frame(type: .json, payload: payload)], "json frame round-trip")
}

do {
    let payload = Data((0..<256).map { UInt8($0 % 256) })
    let encoded = FrameCodec.encode(type: .binary, payload: payload)
    let frames = try FrameDecoder().feed(encoded)
    check(frames == [Frame(type: .binary, payload: payload)], "binary frame round-trip")
}

do {
    let payload = Data(repeating: 0xAB, count: 300)
    let encoded = FrameCodec.encode(type: .json, payload: payload)
    check(Array(encoded.prefix(4)) == [0x00, 0x00, 0x01, 0x2C], "length header is big-endian, excludes header")
    check(encoded[encoded.startIndex + 4] == FrameType.json.rawValue, "type byte follows length")
}

do {
    let payload = Data(#"{"a":1}"#.utf8)
    let encoded = FrameCodec.encode(type: .json, payload: payload)
    let decoder = FrameDecoder()
    var frames: [Frame] = []
    for byte in encoded {
        frames += try decoder.feed(Data([byte]))
    }
    check(frames == [Frame(type: .json, payload: payload)], "incremental byte-at-a-time feed yields one frame")
}

do {
    let a = FrameCodec.encode(type: .json, payload: Data(#"{"n":1}"#.utf8))
    let b = FrameCodec.encode(type: .json, payload: Data(#"{"n":2}"#.utf8))
    var combined = a
    combined.append(b)
    let frames = try FrameDecoder().feed(combined)
    check(frames.count == 2, "two frames in one chunk -> count 2")
    check(frames.first?.payload == Data(#"{"n":1}"#.utf8), "first frame payload")
    check(frames.last?.payload == Data(#"{"n":2}"#.utf8), "second frame payload")
}

checkThrows("oversized length is rejected") {
    var header = Data()
    let tooLarge = FrameCodec.maxFrameLength + 1
    header.append(UInt8((tooLarge >> 24) & 0xFF))
    header.append(UInt8((tooLarge >> 16) & 0xFF))
    header.append(UInt8((tooLarge >> 8) & 0xFF))
    header.append(UInt8(tooLarge & 0xFF))
    header.append(FrameType.json.rawValue)
    return try FrameDecoder().feed(header)
}

checkThrows("unknown type byte is rejected") {
    var frame = FrameCodec.encode(type: .json, payload: Data(#"{}"#.utf8))
    frame[frame.startIndex + 4] = 0xFF
    return try FrameDecoder().feed(frame)
}

// MARK: Envelope

do {
    struct Payload: Codable, Equatable { let code: String }
    let envelope = try Envelope.make(type: .pairCode, payload: Payload(code: "482913"))
    let data = try envelope.encodeToData()

    var json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
    json["futureField"] = "ignore me"
    let patched = try JSONSerialization.data(withJSONObject: json)

    let decoded = try Envelope.decode(from: patched)
    check(decoded.type == MessageType.pairCode.rawValue, "envelope type preserved")
    check(decoded.v == 1, "envelope v == 1")
    let payload = try decoded.decodedPayload(as: Payload.self)
    check(payload == Payload(code: "482913"), "envelope payload decodes despite unknown sibling field")
}

do {
    let envelope = Envelope(type: "some.future.message", payload: .object(["x": .number(1)]))
    let data = try envelope.encodeToData()
    let decoded = try Envelope.decode(from: data)
    check(decoded.type == "some.future.message", "unknown envelope type string round-trips without throwing")
}

print("")
print("--- Networking (TcpServer + DiscoveryResponder, real loopback sockets) ---")
failures += runNetworkSmokeTests()

print("")
if failures == 0 {
    print("All smoke checks passed.")
} else {
    print("\(failures) smoke check(s) FAILED.")
    exit(1)
}
