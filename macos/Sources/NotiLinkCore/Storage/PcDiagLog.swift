import Foundation

/// Minimal rolling diagnostic log at ~/Library/Application Support/NotiLink/logs/pc.log —
/// the macOS equivalent of PcDiagLog.cs. This project's own established debugging culture is
/// "check pc.log first before guessing" for exactly this kind of brand-new, bug-prone surface
/// (handshake/pairing decisions), so it's included from day one rather than added after the
/// first confusing bug report.
public final class PcDiagLog {
    private let fileURL: URL
    private let queue = DispatchQueue(label: "notilink.pcdiaglog")
    private static let maxBytes = 2 * 1024 * 1024

    public init(directory: URL? = nil) {
        let dir = directory ?? PairedDeviceStore.defaultDirectory().appendingPathComponent("logs", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        self.fileURL = dir.appendingPathComponent("pc.log")
    }

    public func log(_ message: String) {
        queue.async { [fileURL] in
            let formatter = ISO8601DateFormatter()
            let line = "[\(formatter.string(from: Date()))] \(message)\n"
            guard let data = line.data(using: .utf8) else { return }

            if let attrs = try? FileManager.default.attributesOfItem(atPath: fileURL.path),
               let size = attrs[.size] as? Int, size > Self.maxBytes {
                try? FileManager.default.removeItem(at: fileURL)
            }

            if let handle = try? FileHandle(forWritingTo: fileURL) {
                defer { try? handle.close() }
                handle.seekToEndOfFile()
                handle.write(data)
            } else {
                try? data.write(to: fileURL)
            }
        }
    }
}
