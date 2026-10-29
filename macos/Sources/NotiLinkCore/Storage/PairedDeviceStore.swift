import Foundation

/// Non-secret pairing metadata (one entry per paired phone). Mirrors PairedDeviceStore.cs's
/// deviceId -> {token, deviceName} mapping, minus the token itself (that lives behind
/// `TokenStoring` — the macOS Keychain in production — see TokenStoring.swift) — the macOS
/// equivalent of DPAPI-protecting the token without storing it in plaintext.
public struct PairedDeviceRecord: Codable, Equatable {
    public let deviceId: String
    public var deviceName: String
    public let deviceModel: String
}

/// Everything except the token itself (deviceName/deviceModel, needed to render the paired-device
/// list) lives in a plain JSON file; the token goes through `tokenStore` (real Keychain in
/// production, in-memory for tests — see TokenStoring.swift's header for why that seam exists).
public final class PairedDeviceStore {
    private let tokenStore: TokenStoring
    private let fileURL: URL
    private let fileLock = NSLock()

    public init(directory: URL? = nil, tokenStore: TokenStoring = KeychainTokenStore()) {
        let baseDir = directory ?? PairedDeviceStore.defaultDirectory()
        try? FileManager.default.createDirectory(at: baseDir, withIntermediateDirectories: true)
        self.fileURL = baseDir.appendingPathComponent("paired-devices.json")
        self.tokenStore = tokenStore
    }

    public static func defaultDirectory() -> URL {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return appSupport.appendingPathComponent("NotiLink", isDirectory: true)
    }

    // MARK: - Records (JSON file)

    public func allRecords() -> [PairedDeviceRecord] {
        fileLock.lock()
        defer { fileLock.unlock() }
        return loadRecordsLocked()
    }

    public func record(forDeviceId deviceId: String) -> PairedDeviceRecord? {
        allRecords().first { $0.deviceId == deviceId }
    }

    /// Same physical phone re-appearing under a new deviceId (reinstall) — matched by the
    /// immutable deviceModel, excluding the deviceId currently being handled. Mirrors
    /// `FindByDeviceModel` in PairedDeviceStore.cs.
    public func findByDeviceModel(_ deviceModel: String, excludingDeviceId deviceId: String) -> PairedDeviceRecord? {
        allRecords().first { $0.deviceModel == deviceModel && $0.deviceId != deviceId }
    }

    // MARK: - Token validation

    /// True if `deviceId` is already paired and any of `candidateTokens` matches its stored
    /// token — mirrors checking the phone's `pairingTokens` array (newest-first, up to 5) against
    /// what the PC has stored for that deviceId.
    public func isValid(deviceId: String, candidateTokens: [String]) -> Bool {
        guard let stored = token(forDeviceId: deviceId) else { return false }
        return candidateTokens.contains(stored)
    }

    public func token(forDeviceId deviceId: String) -> String? {
        tokenStore.token(forAccount: deviceId)
    }

    // MARK: - Pair / rename / unpair

    /// Persists a fresh pairing. If an existing record under a *different* deviceId shares the
    /// same deviceModel (reinstall), carries over its display name and drops the stale entry —
    /// mirrors the "device name silently reset" bug fix in PairedDeviceStore.cs.
    @discardableResult
    public func pair(deviceId: String, deviceName: String, deviceModel: String, token: String) throws -> PairedDeviceRecord {
        try tokenStore.setToken(token, forAccount: deviceId)

        fileLock.lock()
        defer { fileLock.unlock() }
        var records = loadRecordsLocked()
        records.removeAll { $0.deviceId == deviceId }

        var resolvedName = deviceName
        if let stale = records.first(where: { $0.deviceModel == deviceModel }) {
            resolvedName = stale.deviceName
            records.removeAll { $0.deviceModel == deviceModel }
            try? tokenStore.removeToken(forAccount: stale.deviceId)
        }

        let record = PairedDeviceRecord(deviceId: deviceId, deviceName: resolvedName, deviceModel: deviceModel)
        records.append(record)
        try saveRecordsLocked(records)
        return record
    }

    public func rename(deviceId: String, to name: String) throws {
        fileLock.lock()
        defer { fileLock.unlock() }
        var records = loadRecordsLocked()
        guard let index = records.firstIndex(where: { $0.deviceId == deviceId }) else { return }
        records[index].deviceName = name
        try saveRecordsLocked(records)
    }

    public func remove(deviceId: String) throws {
        try tokenStore.removeToken(forAccount: deviceId)
        fileLock.lock()
        defer { fileLock.unlock() }
        var records = loadRecordsLocked()
        records.removeAll { $0.deviceId == deviceId }
        try saveRecordsLocked(records)
    }

    // MARK: - Private

    /// Transient read failures (file momentarily locked, etc.) are retried a few times rather
    /// than treated as "no pairings ever existed" — the same class of bug PairedDeviceStore.cs
    /// had to fix after a real "re-pair on every reboot" report.
    private func loadRecordsLocked() -> [PairedDeviceRecord] {
        for attempt in 0..<3 {
            guard let data = try? Data(contentsOf: fileURL) else { return [] }
            if let records = try? JSONDecoder().decode([PairedDeviceRecord].self, from: data) {
                return records
            }
            if attempt < 2 { Thread.sleep(forTimeInterval: 0.2) }
        }
        return []
    }

    private func saveRecordsLocked(_ records: [PairedDeviceRecord]) throws {
        let data = try JSONEncoder().encode(records)
        try data.write(to: fileURL, options: .atomic)
    }
}
