import Foundation
import Security

protocol PairingTokenStore {
    func readToken() throws -> String?
    func writeToken(_ token: String) throws
}

enum PairingTokenStoreError: Error, LocalizedError {
    case keychainStatus(OSStatus)
    case invalidStoredValue
    case invalidToken

    var errorDescription: String? {
        switch self {
        case .keychainStatus(let status): return "Keychain error \(status)"
        case .invalidStoredValue: return "The Keychain pairing token is not valid UTF-8."
        case .invalidToken: return "The pairing token must contain exactly 64 hexadecimal characters."
        }
    }
}

struct KeychainTokenStore: PairingTokenStore {
    private let service = "local.bubblecast"
    private let account = "pairing-token"

    func readToken() throws -> String? {
        var query = identityQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw PairingTokenStoreError.keychainStatus(status) }
        guard let data = result as? Data, let token = String(data: data, encoding: .utf8) else {
            throw PairingTokenStoreError.invalidStoredValue
        }
        guard PairingTokenValidation.isValid(token) else { throw PairingTokenStoreError.invalidToken }
        return token
    }

    func writeToken(_ token: String) throws {
        guard PairingTokenValidation.isValid(token) else { throw PairingTokenStoreError.invalidToken }
        let data = Data(token.utf8)
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
        ]
        let status = SecItemUpdate(identityQuery as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var item = identityQuery
            item.merge(attributes) { _, new in new }
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw PairingTokenStoreError.keychainStatus(addStatus) }
        } else if status != errSecSuccess {
            throw PairingTokenStoreError.keychainStatus(status)
        }
    }

    private var identityQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
    }
}

enum PairingTokenValidation {
    static func isValid(_ token: String) -> Bool {
        token.utf8.count == 64 && token.utf8.allSatisfy {
            (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
        }
    }
}
