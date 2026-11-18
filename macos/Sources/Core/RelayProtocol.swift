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
}

enum RelayProtocolV2 {
    static func canonical(method: String, path: String, timestamp: String, nonce: String, kind: RelayRequestKind, messageID: String, senderBase64: String, previewBase64: String) -> String {
        ["v2", method, path, timestamp, nonce, kind.rawValue, messageID, senderBase64, previewBase64].joined(separator: "\n")
    }

    static func messageID(token: String, guid: String) -> String {
        String(RelayHMAC.signature(token: token, canonical: "msgid\n" + guid).prefix(32))
    }
}

final class RelayPinnedSessionDelegate: NSObject, URLSessionDelegate {
    private let expectedFingerprint: String

    init(expectedFingerprint: String) {
        self.expectedFingerprint = RelayPinnedSessionDelegate.normalizeFingerprint(expectedFingerprint)
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
        guard fingerprint == expectedFingerprint else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}
