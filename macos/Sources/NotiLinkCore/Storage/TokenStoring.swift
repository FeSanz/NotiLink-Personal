import Foundation
import Security

/// Seam between PairedDeviceStore and wherever pairing tokens actually live. The real
/// implementation is the macOS Keychain (`KeychainTokenStore`); tests/tooling that can't rely on
/// a Keychain UI session being available (e.g. an unsigned dev build run headlessly, which can
/// hang waiting for a confirmation sheet that will never appear) use `InMemoryTokenStore` instead.
public protocol TokenStoring {
    func token(forAccount account: String) -> String?
    func setToken(_ token: String, forAccount account: String) throws
    func removeToken(forAccount account: String) throws
}

public enum TokenStoringError: Error {
    case keychainFailure(OSStatus)
}

public final class KeychainTokenStore: TokenStoring {
    private let service: String

    public init(service: String = "com.notilink.mac.pairing") {
        self.service = service
    }

    public func token(forAccount account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    public func setToken(_ token: String, forAccount account: String) throws {
        let data = Data(token.utf8)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let attributes: [String: Any] = [kSecValueData as String: data]

        let updateStatus = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecItemNotFound {
            var addQuery = query
            addQuery[kSecValueData as String] = data
            addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let addStatus = SecItemAdd(addQuery as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw TokenStoringError.keychainFailure(addStatus) }
        } else if updateStatus != errSecSuccess {
            throw TokenStoringError.keychainFailure(updateStatus)
        }
    }

    public func removeToken(forAccount account: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw TokenStoringError.keychainFailure(status)
        }
    }
}

/// Process-lifetime only — for tests and dev tooling, never for the shipped app.
public final class InMemoryTokenStore: TokenStoring {
    private var tokens: [String: String] = [:]
    private let lock = NSLock()

    public init() {}

    public func token(forAccount account: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return tokens[account]
    }

    public func setToken(_ token: String, forAccount account: String) throws {
        lock.lock(); defer { lock.unlock() }
        tokens[account] = token
    }

    public func removeToken(forAccount account: String) throws {
        lock.lock(); defer { lock.unlock() }
        tokens.removeValue(forKey: account)
    }
}
