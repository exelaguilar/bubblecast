import Foundation

/// Where and how to reach the Windows receiver.
struct RelayEndpointConfiguration: Equatable {
    let address: String
    let token: String
    let useTLS: Bool
    let fingerprint: String

    init(settings: RelaySettings) {
        address = settings.windowsAddress
        token = settings.pairingToken
        useTLS = settings.useTLS
        fingerprint = settings.tlsCertificateFingerprint
    }
}

/// Builds and sends signed protocol v2 requests. Pinned-TLS requests share one URL session per
/// certificate fingerprint instead of creating a session for every request. Thread-safe.
final class RelayClient {
    private let lock = NSLock()
    private var pinnedSession: URLSession?
    private var pinnedFingerprint = ""

    func send(path: String,
              method: String,
              sender: String = "",
              preview: String = "",
              kind: RelayRequestKind,
              messageIdentity: String = "",
              configuration: RelayEndpointConfiguration,
              completion: @escaping (DeliveryOutcome, String) -> Void) {
        guard !configuration.token.isEmpty,
              let url = WindowsRelayEndpoint.url(address: configuration.address, path: path, useTLS: configuration.useTLS) else {
            completion(.transient, "enter a valid Windows host name/IP and pairing token")
            return
        }
        if configuration.useTLS && !RelayPinnedSessionDelegate.isValidFingerprint(configuration.fingerprint) {
            completion(.transient, "enter the 64-character SHA-256 TLS fingerprint shown by Windows")
            return
        }

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 5
        let senderBase64 = sender.isEmpty ? "" : Data(sender.utf8).base64EncodedString()
        let previewBase64 = preview.isEmpty ? "" : Data(preview.utf8).base64EncodedString()
        let timestamp = String(Int(Date().timeIntervalSince1970))
        let nonce = UUID().uuidString.lowercased()
        let messageID = kind == .message ? RelayProtocolV2.messageID(token: configuration.token, guid: messageIdentity) : ""
        let canonical = RelayProtocolV2.canonical(method: method, path: path, timestamp: timestamp, nonce: nonce, kind: kind,
                                                  messageID: messageID, senderBase64: senderBase64, previewBase64: previewBase64)
        request.setValue("2", forHTTPHeaderField: "X-Relay-Version")
        request.setValue(kind.rawValue, forHTTPHeaderField: "X-Relay-Kind")
        request.setValue(messageID, forHTTPHeaderField: "X-Relay-Message-Id")
        request.setValue(timestamp, forHTTPHeaderField: "X-Relay-Time")
        request.setValue(nonce, forHTTPHeaderField: "X-Relay-Nonce")
        request.setValue(RelayHMAC.signature(token: configuration.token, canonical: canonical), forHTTPHeaderField: "X-Relay-Signature")
        if !senderBase64.isEmpty { request.setValue(senderBase64, forHTTPHeaderField: "X-Sender-Base64") }
        if !previewBase64.isEmpty { request.setValue(previewBase64, forHTTPHeaderField: "X-Preview-Base64") }

        let session = configuration.useTLS ? pinnedSession(for: configuration.fingerprint) : URLSession.shared
        session.dataTask(with: request) { _, response, error in
            if let error {
                completion(.transient, error.localizedDescription)
                return
            }
            guard let http = response as? HTTPURLResponse else {
                completion(.transient, "no HTTP response")
                return
            }
            switch http.statusCode {
            case 204:
                completion(.delivered, "Windows accepted the request")
            case 403:
                let code = http.value(forHTTPHeaderField: "X-Relay-Error") ?? "auth"
                completion(.rejected, "Windows rejected the request (\(code))")
            case 404:
                completion(.notFound, "Windows returned HTTP 404")
            default:
                completion(.transient, "Windows returned HTTP \(http.statusCode)")
            }
        }.resume()
    }

    /// Drops the cached pinned session (for example after settings change).
    func reset() {
        lock.lock()
        defer { lock.unlock() }
        pinnedSession?.finishTasksAndInvalidate()
        pinnedSession = nil
        pinnedFingerprint = ""
    }

    private func pinnedSession(for fingerprint: String) -> URLSession {
        lock.lock()
        defer { lock.unlock() }
        let normalized = RelayPinnedSessionDelegate.normalizeFingerprint(fingerprint)
        if let pinnedSession, pinnedFingerprint == normalized { return pinnedSession }
        pinnedSession?.finishTasksAndInvalidate()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 5
        let session = URLSession(configuration: configuration, delegate: RelayPinnedSessionDelegate(expectedFingerprint: normalized), delegateQueue: nil)
        pinnedSession = session
        pinnedFingerprint = normalized
        return session
    }
}
