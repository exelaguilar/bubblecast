import CryptoKit
import Foundation
import Security

/// HMAC-SHA256 keyed by the UTF-8 bytes of the 64-character pairing token, as lowercase hex.
enum RelayHMAC {
    static func signature(token: String, canonical: String) -> String {
        let key = SymmetricKey(data: Data(token.utf8))
        return HMAC<SHA256>.authenticationCode(for: Data(canonical.utf8), using: key)
            .map { String(format: "%02x", $0) }.joined()
    }
}

enum RelayRequestKind: String, Equatable {
    case message
    case test
    case health
    /// Shares notification settings (`POST /settings`); the settings text takes the preview's place.
    case settings
}

enum RelayProtocolV2 {
    static func canonical(method: String, path: String, timestamp: String, nonce: String, kind: RelayRequestKind, messageID: String, senderBase64: String, previewBase64: String) -> String {
        ["v2", method, path, timestamp, nonce, kind.rawValue, messageID, senderBase64, previewBase64].joined(separator: "\n")
    }

    /// UTF-8 byte budgets that fit Windows' Base64 header limits (1,024 and 4,096 characters).
    static let maximumSenderBytes = 768
    static let maximumPreviewBytes = 3072

    /// `text` cut at a whole character so its UTF-8 form fits in `limit` bytes. An alert over budget
    /// would be rejected by Windows and retried forever, holding up the alerts behind it.
    static func truncated(_ text: String, toUTF8Bytes limit: Int) -> String {
        guard text.utf8.count > limit else { return text }
        var used = 0
        var end = text.startIndex
        for character in text {
            let size = character.utf8.count
            if used + size > limit { break }
            used += size
            end = text.index(after: end)
        }
        return String(text[..<end])
    }

    static func messageID(token: String, guid: String) -> String {
        String(RelayHMAC.signature(token: token, canonical: "msgid\n" + guid).prefix(32))
    }
}

/// Accepts the Windows receiver's self-signed certificate only when its SHA-256 fingerprint matches the
/// pinned value. With no expected fingerprint it trusts the first certificate it sees and records its
/// fingerprint, so the user can compare it with the one Windows shows before pinning it.
final class RelayPinnedSessionDelegate: NSObject, URLSessionDelegate {
    private let expectedFingerprint: String?
    private let lock = NSLock()
    private var observed: String?

    init(expectedFingerprint: String?) {
        self.expectedFingerprint = expectedFingerprint.map { RelayPinnedSessionDelegate.normalizeFingerprint($0) }
    }

    /// The fingerprint of the certificate the server presented, once a handshake has started.
    var observedFingerprint: String? {
        lock.lock()
        defer { lock.unlock() }
        return observed
    }

    static func isValidFingerprint(_ value: String) -> Bool {
        let normalized = normalizeFingerprint(value)
        return normalized.count == 64 && normalized.allSatisfy { $0.isHexDigit }
    }

    static func normalizeFingerprint(_ value: String) -> String {
        String(value.filter { $0.isHexDigit }.uppercased())
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let certificates = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let certificate = certificates.first else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        let certificateData = SecCertificateCopyData(certificate) as Data
        let fingerprint = SHA256.hash(data: certificateData).map { String(format: "%02X", $0) }.joined()
        lock.lock()
        observed = fingerprint
        lock.unlock()
        if let expectedFingerprint, fingerprint != expectedFingerprint {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}
