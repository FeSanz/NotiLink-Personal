import Foundation

/// PNG app-icon cache keyed by packageName (which may be a Dual Messenger composite
/// "pkg#profileUserId" id) — mirrors IconCache.cs's role of only re-requesting an icon
/// from the phone when the cache file is missing.
public final class IconCache {
    private let directory: URL

    public init(directory: URL? = nil) {
        self.directory = directory ?? PairedDeviceStore.defaultDirectory().appendingPathComponent("icons", isDirectory: true)
        try? FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true)
    }

    public func has(packageName: String) -> Bool {
        FileManager.default.fileExists(atPath: fileURL(for: packageName).path)
    }

    public func data(for packageName: String) -> Data? {
        try? Data(contentsOf: fileURL(for: packageName))
    }

    public func store(_ data: Data, for packageName: String) throws {
        try data.write(to: fileURL(for: packageName), options: .atomic)
    }

    private func fileURL(for packageName: String) -> URL {
        // packageName never contains '/', but '#' (Dual Messenger composite ids) is still a
        // valid filename character on macOS — no sanitization needed beyond that guarantee.
        directory.appendingPathComponent("\(packageName).png")
    }
}
