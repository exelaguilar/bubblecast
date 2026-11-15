import Foundation
import CryptoKit

private enum Result {
    case delivered
    case rejected
    case transient
}

private struct Request {
    let sender: String
    let preview: String
}

private final class FakeClock {
    var seconds: TimeInterval = 0
    func advance(_ amount: TimeInterval) { seconds += amount }
}

private final class FakeTransport {
    var results: [Result] = []
    private(set) var requests: [Request] = []
    func send(sender: String, preview: String) -> Result {
        requests.append(Request(sender: sender, preview: preview))
        return results.isEmpty ? .delivered : results.removeFirst()
    }
}

private struct Row {
    let id: Int
    let guid: String
    let freshWhenRead: Bool
    let firstReadAt: TimeInterval
}

private final class FakeTokenStore: PairingTokenStore {
    var token: String?
    func readToken() throws -> String? { token }
    func writeToken(_ token: String) throws { self.token = token }
}

private final class DeliveryMachine {
    let clock: FakeClock
    let transport: FakeTransport
    let maxAge: TimeInterval
    private(set) var pending: [Row] = []
    private(set) var processedGUIDs = Set<String>()
    private(set) var attempts = 0
    private(set) var retryAt: TimeInterval?
    private(set) var rejectedPause = false
    private var retryAttempt = 0

    init(clock: FakeClock, transport: FakeTransport, maxAge: TimeInterval = 600) {
        self.clock = clock
        self.transport = transport
        self.maxAge = maxAge
    }

    func enqueue(_ id: Int, guid: String, isFreshAtRead: Bool) {
        if isFreshAtRead { pending.append(Row(id: id, guid: guid, freshWhenRead: true, firstReadAt: clock.seconds)) }
        else { processedGUIDs.insert(guid) }
    }

    func settingsSaved() {
        rejectedPause = false
        retryAt = nil
        retryAttempt = 0
        pump()
    }

    func pump() {
        guard !rejectedPause, retryAt.map({ clock.seconds >= $0 }) ?? true, let first = pending.first else { return }
        retryAt = nil
        guard first.freshWhenRead else { return }
        let expired = pending.prefix { clock.seconds - $0.firstReadAt > maxAge }
        if !expired.isEmpty {
            let rows = Array(expired)
            let summary = rows.count == 1
                ? "1 message arrived while this PC was unreachable"
                : "\(rows.count) messages arrived while this PC was unreachable"
            attempts += 1
            switch transport.send(sender: "", preview: summary) {
            case .delivered:
                for row in rows { processedGUIDs.insert(row.guid) }
                pending.removeFirst(rows.count)
                retryAttempt = 0
            case .rejected:
                rejectedPause = true
            case .transient:
                scheduleRetry()
            }
            return
        }
        attempts += 1
        switch transport.send(sender: "", preview: "message") {
        case .delivered:
            processedGUIDs.insert(first.guid)
            pending.removeFirst()
            retryAttempt = 0
        case .rejected:
            rejectedPause = true
        case .transient:
            scheduleRetry()
        }
    }

    private func scheduleRetry() {
        let delay = min(60, 2 << min(retryAttempt, 5))
        retryAttempt += 1
        retryAt = clock.seconds + TimeInterval(delay)
    }
}

private func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    if !condition() { fatalError("FAIL: \(message)") }
    print("PASS: \(message)")
}

private func cursorBaseline(cursor: Int64, maximumRowID: Int64) -> (Int64, Bool) {
    return maximumRowID < cursor ? (maximumRowID, true) : (cursor, false)
}

@main
struct CoreChecks {
static func main() {
let retryClock = FakeClock()
let retryTransport = FakeTransport()
retryTransport.results = Array(repeating: .transient, count: 64)
let retryMachine = DeliveryMachine(clock: retryClock, transport: retryTransport)
retryMachine.enqueue(1, guid: "retry-guid", isFreshAtRead: true)
for _ in 0..<600 {
    retryMachine.pump()
    retryClock.advance(1)
}
check(retryMachine.attempts <= 25, "transient failures use bounded exponential retries (\(retryMachine.attempts) attempts / 10 minutes)")

let rejectClock = FakeClock()
let rejectTransport = FakeTransport()
rejectTransport.results = [.rejected, .delivered]
let rejectMachine = DeliveryMachine(clock: rejectClock, transport: rejectTransport)
rejectMachine.enqueue(2, guid: "rejected-guid", isFreshAtRead: true)
rejectMachine.pump()
for _ in 0..<60 { rejectClock.advance(1); rejectMachine.pump() }
check(rejectMachine.rejectedPause && rejectMachine.attempts == 1, "HTTP 403 pauses delivery without a request storm")
rejectMachine.settingsSaved()
check(rejectMachine.attempts == 2 && rejectMachine.pending.isEmpty, "saving corrected settings resumes the queued message")

let catchUpClock = FakeClock()
let catchUpTransport = FakeTransport()
let catchUpMachine = DeliveryMachine(clock: catchUpClock, transport: catchUpTransport)
catchUpMachine.enqueue(10, guid: "guid-a", isFreshAtRead: true)
catchUpMachine.enqueue(11, guid: "guid-b", isFreshAtRead: true)
catchUpMachine.enqueue(12, guid: "guid-c", isFreshAtRead: true)
catchUpClock.advance(601)
catchUpMachine.pump()
check(catchUpTransport.requests.count == 1, "expired fresh backlog produces one request")
check(catchUpTransport.requests[0].sender.isEmpty && catchUpTransport.requests[0].preview == "3 messages arrived while this PC was unreachable", "catch-up is count-only and uses the specified summary")
check(catchUpMachine.pending.isEmpty && catchUpMachine.processedGUIDs == ["guid-a", "guid-b", "guid-c"], "catch-up records all summarized GUIDs")

let singularClock = FakeClock()
let singularTransport = FakeTransport()
let singularMachine = DeliveryMachine(clock: singularClock, transport: singularTransport)
singularMachine.enqueue(13, guid: "guid-singular", isFreshAtRead: true)
singularClock.advance(601)
singularMachine.pump()
check(singularTransport.requests.first?.preview == "1 message arrived while this PC was unreachable", "single-row catch-up uses singular wording")

let staleClock = FakeClock()
let staleTransport = FakeTransport()
let staleMachine = DeliveryMachine(clock: staleClock, transport: staleTransport)
staleMachine.enqueue(20, guid: "stale-guid", isFreshAtRead: false)
staleClock.advance(900)
staleMachine.pump()
check(staleTransport.requests.isEmpty && staleMachine.processedGUIDs.contains("stale-guid"), "already-stale rows are suppressed when first read")

let baseline = cursorBaseline(cursor: 900, maximumRowID: 120)
check(baseline.0 == 120 && baseline.1, "cursor re-baselines when saved ROWID exceeds database maximum")
let unchanged = cursorBaseline(cursor: 90, maximumRowID: 120)
check(unchanged.0 == 90 && !unchanged.1, "cursor remains unchanged when it is within database range")

let testSuiteName = "local.bubblecast.CoreChecks.\(UUID().uuidString)"
let testDefaults = UserDefaults(suiteName: testSuiteName)!
let testTokenStore = FakeTokenStore()
check(PairingTokenValidation.isValid(String(repeating: "a", count: 64)), "64-character hex pairing token passes pure validation")
check(!PairingTokenValidation.isValid(String(repeating: "g", count: 64)), "non-hex pairing token fails pure validation")
let settingsSnapshot = RelaySettings(
    windowsAddress: "synthetic-host",
    pairingToken: String(repeating: "a", count: 64),
    includeSenderNames: false,
    includeMessagePreviews: true,
    suppressOldMessages: true,
    maximumMessageAgeMinutes: 12,
    senderNameOverrides: "synthetic = Sender",
    notifyForReactions: true,
    useTLS: true,
    tlsCertificateFingerprint: String(repeating: "A", count: 64)
)
try! settingsSnapshot.save(to: testDefaults, tokenStore: testTokenStore)
check(RelaySettings.load(from: testDefaults, tokenStore: testTokenStore) == settingsSnapshot, "saved settings reload as an immutable snapshot")
check(testTokenStore.token == settingsSnapshot.pairingToken && testDefaults.string(forKey: RelayPreferences.pairingToken) == nil,
      "pairing token is stored by the token store and not in UserDefaults")
testDefaults.set(settingsSnapshot.pairingToken, forKey: RelayPreferences.pairingToken)
let migrationStore = FakeTokenStore()
let migratedSettings = RelaySettings.load(from: testDefaults, tokenStore: migrationStore)
check(migratedSettings.pairingToken == settingsSnapshot.pairingToken && migrationStore.token == settingsSnapshot.pairingToken,
      "legacy pairing token migrates to the token store")
check(testDefaults.string(forKey: RelayPreferences.pairingToken) == settingsSnapshot.pairingToken,
      "legacy token remains until an accepted signed request")
RelaySettings.retireLegacyToken(from: testDefaults)
check(testDefaults.string(forKey: RelayPreferences.pairingToken) == nil, "legacy token can be retired after successful authentication")
testDefaults.removePersistentDomain(forName: testSuiteName)

let expectedUnixTime = 1_700_000_000.0
let secondsOffset = expectedUnixTime - 978_307_200.0
let timestampSamples: [(Double, TimestampUnit)] = [
    (secondsOffset, .appleSeconds),
    (secondsOffset * 1e3, .appleMilliseconds),
    (secondsOffset * 1e6, .appleMicroseconds),
    (secondsOffset * 1e9, .appleNanoseconds)
]
for (raw, expectedUnit) in timestampSamples {
    let decoded = TimestampDecoder.decode(raw)
    check(decoded?.unit == expectedUnit && abs((decoded?.date.timeIntervalSince1970 ?? 0) - expectedUnixTime) < 0.001,
          "timestamp decoder maps \(expectedUnit.rawValue) deterministically")
}
check(TimestampDecoder.decode(0) == nil, "zero timestamp remains undated")

check(WindowsRelayEndpoint.url(address: "192.168.1.20", path: "/health")?.absoluteString == "http://192.168.1.20:8765/health",
      "IPv4 endpoint uses the fixed relay port")
check(WindowsRelayEndpoint.url(address: "relay.local", path: "/notify")?.absoluteString == "http://relay.local:8765/notify",
      "host name endpoint uses the fixed relay port")
check(WindowsRelayEndpoint.url(address: "[2001:db8::20]", path: "/health")?.absoluteString == "http://[2001:db8::20]:8765/health",
      "bracketed IPv6 endpoint uses the fixed relay port")
check(WindowsRelayEndpoint.url(address: "192.168.1.20:9000", path: "/health") == nil,
      "alternate port is rejected to preserve the v1 endpoint")
check(WindowsRelayEndpoint.url(address: "relay.local", path: "/health", useTLS: true)?.absoluteString == "https://relay.local:8766/health",
      "pinned TLS uses its separate port without changing the HTTP endpoint")
check(RelayPinnedSessionDelegate.isValidFingerprint(String(repeating: "a", count: 64)), "TLS pin accepts a 64-digit SHA-256 fingerprint")
check(!RelayPinnedSessionDelegate.isValidFingerprint(String(repeating: "g", count: 64)), "TLS pin rejects non-hex fingerprints")
check(WindowsRelayEndpoint.url(address: "relay.local/not-an-address", path: "/health") == nil,
      "path suffix is rejected in the address field")

let testToken = String(repeating: "a", count: 64)
let notifyCanonical = RelayProtocolV1.canonical(
    path: "/notify",
    timestamp: "1700000000",
    nonce: "123e4567-e89b-12d3-a456-426614174000",
    senderBase64: "SmFuZQ==",
    previewBase64: "SGVsbG8="
)
check(RelayProtocolV1.signature(token: testToken, canonical: notifyCanonical) == "8f94d22c2419fb72df278b2238ba5fa017b9d1a05f4ed516056a1715e0f347bc",
      "v1 notify HMAC matches the protocol vector")
let healthCanonical = RelayProtocolV1.canonical(
    path: "/health",
    timestamp: "1700000000",
    nonce: "123e4567-e89b-12d3-a456-426614174001",
    senderBase64: "",
    previewBase64: ""
)
check(RelayProtocolV1.signature(token: testToken, canonical: healthCanonical) == "cf00cc8ba632375d1ba7cb70e6605a1e89e6fdee089436330aa83c03afebba1a",
      "v1 health HMAC matches the protocol vector")
let v2MessageID = RelayProtocolV2.messageID(token: testToken, guid: "guid-demo")
let v2Canonical = RelayProtocolV2.canonical(method: "POST", path: "/notify", timestamp: "1700000000", nonce: "123e4567-e89b-12d3-a456-426614174000", kind: .message, messageID: v2MessageID, senderBase64: "SmFuZQ==", previewBase64: "SGVsbG8=")
check(v2Canonical == "v2\nPOST\n/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nmessage\n\(v2MessageID)\nSmFuZQ==\nSGVsbG8=",
      "v2 canonical string covers method, kind, message ID, and payload fields")
check(v2MessageID.count == 32 && v2MessageID == RelayProtocolV2.messageID(token: testToken, guid: "guid-demo"),
      "v2 message ID is stable and opaque-sized")
check(RelayProtocolV1.signature(token: testToken, canonical: v2Canonical).count == 64,
      "v2 signature is an HMAC-SHA256 hex digest")
let fixedV2Canonical = RelayProtocolV2.canonical(method: "POST", path: "/notify", timestamp: "1700000000", nonce: "123e4567-e89b-12d3-a456-426614174000", kind: .message, messageID: "0123456789abcdef0123456789abcdef", senderBase64: "SmFuZQ==", previewBase64: "SGVsbG8=")
check(RelayProtocolV1.signature(token: testToken, canonical: fixedV2Canonical) == "f4182f094163c132a423b6be497efd9d40e79f57503a19b644d2b2bce3c7f876",
      "v2 notify HMAC matches the protocol vector")
}
}
