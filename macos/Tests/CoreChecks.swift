import Foundation
import CryptoKit

// Pure-logic checks. The delivery checks drive the app's real DeliveryQueue with a fake clock.

private final class FakeClock {
    var now = Date(timeIntervalSince1970: 1_700_000_000)
    func advance(_ seconds: TimeInterval) { now = now.addingTimeInterval(seconds) }
}

private final class MemoryDeliveryStore: DeliveryStore {
    var cursor: Int64?
    var processedGUIDs: [String] = []
}

private final class FakeTokenStore: PairingTokenStore {
    var token: String?
    func readToken() throws -> String? { token }
    func writeToken(_ token: String) throws { self.token = token }
}

/// Sends each step through a scripted transport, as RelayModel does with RelayClient.
private final class DeliveryHarness {
    let clock: FakeClock
    let store: MemoryDeliveryStore
    let queue: DeliveryQueue
    var results: [DeliveryOutcome] = []
    private(set) var sent: [DeliveryStep] = []
    private(set) var suppressedOld = 0
    private(set) var duplicates = 0
    private(set) var skipped = 0
    /// Row IDs the fake read-state check reports as already seen.
    var seen = Set<Int64>()

    init(maximumAge: TimeInterval = 600, cursor: Int64? = 0) {
        let clock = FakeClock()
        let store = MemoryDeliveryStore()
        store.cursor = cursor
        self.clock = clock
        self.store = store
        queue = DeliveryQueue(store: store, suppressOldMessages: true, maximumAge: maximumAge, now: { clock.now }, jitter: { $0.lowerBound })
        queue.onSuppressedOld = { [unowned self] in self.suppressedOld += 1 }
        queue.onDuplicate = { [unowned self] in self.duplicates += 1 }
        queue.shouldSkip = { [unowned self] row in self.seen.contains(row.rowID) }
        queue.onSkipped = { [unowned self] in self.skipped += 1 }
    }

    func row(_ id: Int64, guid: String, ageSeconds: TimeInterval? = 0) -> PendingMessage {
        PendingMessage(rowID: id, guid: guid, senderAddress: "", preview: "message",
                       messageDate: ageSeconds.map { clock.now.addingTimeInterval(-$0) },
                       groupName: "", isAssociatedMessage: false, timestampUnit: .appleNanoseconds)
    }

    /// Runs one delivery attempt if one is due.
    func pump() {
        let step = queue.next()
        switch step {
        case .send, .sendCatchUp:
            sent.append(step)
            queue.complete(step, outcome: results.isEmpty ? .delivered : results.removeFirst())
        default:
            break
        }
    }

    var catchUpSummaries: [String] {
        sent.compactMap { if case .sendCatchUp(_, let summary) = $0 { return summary } else { return nil } }
    }
}

private func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    if !condition() { fatalError("FAIL: \(message)") }
    print("PASS: \(message)")
}

@main
struct CoreChecks {
static func main() {
let retry = DeliveryHarness()
retry.results = Array(repeating: .transient, count: 64)
retry.queue.ingest([retry.row(1, guid: "retry-guid")])
for _ in 0..<600 {
    retry.pump()
    retry.clock.advance(1)
}
check(retry.sent.count <= 25, "transient failures use bounded exponential retries (\(retry.sent.count) attempts / 10 minutes)")

let reject = DeliveryHarness()
reject.results = [.rejected, .delivered]
reject.queue.ingest([reject.row(2, guid: "rejected-guid")])
reject.pump()
for _ in 0..<59 { reject.clock.advance(1); reject.pump() }
check(reject.sent.count == 1 && reject.queue.pending.count == 1, "HTTP 403 pauses delivery for a minute without a request storm")
reject.clock.advance(1)
reject.pump()
check(reject.sent.count == 2 && reject.queue.pending.isEmpty, "delivery resumes on its own after the 403 pause")

let resume = DeliveryHarness()
resume.results = [.rejected, .delivered]
resume.queue.ingest([resume.row(3, guid: "resume-guid")])
resume.pump()
resume.queue.settingsChanged(suppressOldMessages: true, maximumAge: 600)
resume.pump()
check(resume.sent.count == 2 && resume.queue.pending.isEmpty, "saving corrected settings resumes the queued message immediately")

let unknown = DeliveryHarness()
unknown.results = [.notFound]
unknown.queue.ingest([unknown.row(4, guid: "unknown-guid")])
unknown.pump()
unknown.clock.advance(3600)
unknown.pump()
check(unknown.sent.count == 1 && unknown.queue.next() == .paused, "an unrecognized request pauses until settings change")

let catchUp = DeliveryHarness()
catchUp.queue.ingest([catchUp.row(10, guid: "guid-a"), catchUp.row(11, guid: "guid-b"), catchUp.row(12, guid: "guid-c")])
catchUp.clock.advance(601)
catchUp.pump()
check(catchUp.sent.count == 1, "expired fresh backlog produces one request")
check(catchUp.catchUpSummaries == ["3 messages arrived while this PC was unreachable"], "catch-up is count-only and uses the specified summary")
check(catchUp.queue.pending.isEmpty && catchUp.queue.processedGUIDs == ["guid-a", "guid-b", "guid-c"] && catchUp.store.cursor == 12,
      "catch-up records all summarized GUIDs and advances the cursor")

let singular = DeliveryHarness()
singular.queue.ingest([singular.row(13, guid: "guid-singular")])
singular.clock.advance(601)
singular.pump()
check(singular.catchUpSummaries == ["1 message arrived while this PC was unreachable"], "single-row catch-up uses singular wording")

let stale = DeliveryHarness()
stale.queue.ingest([stale.row(20, guid: "stale-guid", ageSeconds: 900), stale.row(21, guid: "undated-guid", ageSeconds: nil)])
stale.pump()
check(stale.sent.isEmpty && stale.suppressedOld == 2 && stale.queue.processedGUIDs.contains("stale-guid"),
      "old and undated rows are suppressed when first read")

let duplicate = DeliveryHarness()
duplicate.queue.ingest([duplicate.row(30, guid: "dup-guid")])
duplicate.pump()
duplicate.queue.ingest([duplicate.row(31, guid: "dup-guid")])
duplicate.pump()
check(duplicate.sent.count == 1 && duplicate.duplicates == 1 && duplicate.store.cursor == 31, "a repeated GUID is dropped and the cursor still advances")

let fresh = DeliveryHarness(cursor: nil)
check(fresh.queue.align(toMaximumRowID: 500) == .baselined && fresh.store.cursor == 500, "first run starts at the end of the database")
let restored = DeliveryHarness(cursor: 900)
check(restored.queue.align(toMaximumRowID: 120) == .rebaselined && restored.store.cursor == 120, "cursor re-baselines when saved ROWID exceeds database maximum")
let steady = DeliveryHarness(cursor: 90)
check(steady.queue.align(toMaximumRowID: 120) == .unchanged && steady.store.cursor == 90, "cursor remains unchanged when it is within database range")

let testToken0 = String(repeating: "a", count: 64)

let seenHead = DeliveryHarness()
seenHead.seen = [40]
seenHead.queue.ingest([seenHead.row(40, guid: "seen-guid"), seenHead.row(41, guid: "unseen-guid")])
seenHead.pump()
check(seenHead.skipped == 1 && seenHead.sent.count == 1 && seenHead.queue.pending.isEmpty && seenHead.store.cursor == 41,
      "a row already seen is skipped, the next row is sent, and the cursor advances past both")
check(seenHead.queue.processedGUIDs.contains("seen-guid"), "a skipped row is remembered so it is never sent later")

let seenLater = DeliveryHarness()
seenLater.results = [.transient, .delivered]
seenLater.queue.ingest([seenLater.row(42, guid: "read-while-waiting")])
seenLater.pump()
seenLater.seen = [42]
seenLater.clock.advance(5)
seenLater.pump()
check(seenLater.sent.count == 1 && seenLater.skipped == 1 && seenLater.queue.pending.isEmpty,
      "a message read while Windows was unreachable is dropped on the next attempt")

let seenCatchUp = DeliveryHarness()
seenCatchUp.seen = [51]
seenCatchUp.queue.ingest([seenCatchUp.row(50, guid: "cu-a"), seenCatchUp.row(51, guid: "cu-b"), seenCatchUp.row(52, guid: "cu-c")])
seenCatchUp.clock.advance(601)
seenCatchUp.pump()
check(seenCatchUp.catchUpSummaries == ["2 messages arrived while this PC was unreachable"] && seenCatchUp.queue.pending.isEmpty,
      "catch-up summaries count only rows not already seen, and clear every row")

let sharedSample = SharedSettings(revision: 1_700_000_000_000, includeSenderNames: true, includeMessagePreviews: false,
                                  suppressOldMessages: true, maximumMessageAgeMinutes: 10, notifyForReactions: false,
                                  skipReadMessages: true, quietWhileActive: true, activeMinutes: 2)
let sharedText = "rev=1700000000000;names=1;previews=0;old=1;age=10;reactions=0;read=1;active=1;activemin=2"
check(sharedSample.encoded() == sharedText, "shared settings encode in the documented order")
check(SharedSettings.parse(sharedText) == sharedSample, "shared settings round-trip")
var defaultsAtRevision5 = SharedSettings.defaults
defaultsAtRevision5.revision = 5
check(SharedSettings.parse("rev=5;future=9") == defaultsAtRevision5,
      "unknown shared-settings keys are ignored and missing keys use defaults")
check(SharedSettings.parse("names=1") == nil && SharedSettings.parse("rev=x") == nil && SharedSettings.parse("rev=1;age") == nil,
      "shared settings without a revision or with malformed pairs are rejected")
check(SharedSettings.parse("rev=1;age=99999;activemin=0")?.maximumMessageAgeMinutes == 1440 &&
      SharedSettings.parse("rev=1;age=99999;activemin=0")?.activeMinutes == 1, "shared settings clamp out-of-range minutes")
check(SharedSettings.parse("rev=1;" + String(repeating: "x=1;", count: 200)) == nil, "oversized shared settings are rejected")
check(SharedSettings.responseSignature(token: testToken0, nonce: "123e4567-e89b-12d3-a456-426614174002", encoded: sharedText)
      == "467902cb72f63cb24a7df57588fbd9c29a3b7626b3aed8836c51c2f427ef550b", "settings response signature matches the protocol vector")
let settingsCanonical = RelayProtocolV2.canonical(method: "POST", path: "/settings", timestamp: "1700000000",
                                                  nonce: "123e4567-e89b-12d3-a456-426614174002", kind: .settings,
                                                  messageID: "", senderBase64: "", previewBase64: sharedText)
check(RelayHMAC.signature(token: testToken0, canonical: settingsCanonical) == "284b277e830115a39d9c89a696468820655071f5ec5ae3323dccd14ef9354ca9",
      "settings request signature matches the protocol vector")

let attentionNow = Date(timeIntervalSince1970: 1_700_000_000)
var quietOnly = SharedSettings.defaults
quietOnly.skipReadMessages = false
check(SharedSettings.defaults.shouldSkip(MessageAttention(isRead: true, lastActivity: nil), now: attentionNow), "a read message is skipped")
check(!quietOnly.shouldSkip(MessageAttention(isRead: true, lastActivity: nil), now: attentionNow), "read messages are sent when that option is off")
check(quietOnly.shouldSkip(MessageAttention(isRead: false, lastActivity: attentionNow.addingTimeInterval(-60)), now: attentionNow),
      "recent activity in the conversation keeps it quiet")
check(!quietOnly.shouldSkip(MessageAttention(isRead: false, lastActivity: attentionNow.addingTimeInterval(-180)), now: attentionNow),
      "activity older than the quiet window doesn't")
check(!SharedSettings.defaults.shouldSkip(nil, now: attentionNow), "an unreadable database never drops a message")

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
    skipReadMessages: false,
    quietWhileActive: true,
    activeMinutes: 7,
    useTLS: true,
    tlsCertificateFingerprint: String(repeating: "A", count: 64)
)
try! settingsSnapshot.save(to: testDefaults, tokenStore: testTokenStore)
check(RelaySettings.load(from: testDefaults, tokenStore: testTokenStore) == settingsSnapshot, "saved settings reload as an immutable snapshot")
check(testTokenStore.token == settingsSnapshot.pairingToken && testDefaults.string(forKey: "pairingToken") == nil,
      "pairing token is stored by the token store and not in UserDefaults")
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
      "alternate port is rejected in the address field")
check(WindowsRelayEndpoint.url(address: "relay.local", path: "/health", useTLS: true)?.absoluteString == "https://relay.local:8766/health",
      "pinned TLS uses its separate port without changing the HTTP endpoint")
check(RelayPinnedSessionDelegate.isValidFingerprint(String(repeating: "a", count: 64)), "TLS pin accepts a 64-digit SHA-256 fingerprint")
check(!RelayPinnedSessionDelegate.isValidFingerprint(String(repeating: "g", count: 64)), "TLS pin rejects non-hex fingerprints")
check(WindowsRelayEndpoint.url(address: "relay.local/not-an-address", path: "/health") == nil,
      "path suffix is rejected in the address field")

let testToken = String(repeating: "a", count: 64)
check(RelayHMAC.signature(token: testToken, canonical: "/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nSmFuZQ==\nSGVsbG8=") == "8f94d22c2419fb72df278b2238ba5fa017b9d1a05f4ed516056a1715e0f347bc",
      "HMAC-SHA256 keyed by the token's UTF-8 bytes matches the reference vector")
let v2MessageID = RelayProtocolV2.messageID(token: testToken, guid: "guid-demo")
let v2Canonical = RelayProtocolV2.canonical(method: "POST", path: "/notify", timestamp: "1700000000", nonce: "123e4567-e89b-12d3-a456-426614174000", kind: .message, messageID: v2MessageID, senderBase64: "SmFuZQ==", previewBase64: "SGVsbG8=")
check(v2Canonical == "v2\nPOST\n/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nmessage\n\(v2MessageID)\nSmFuZQ==\nSGVsbG8=",
      "v2 canonical string covers method, kind, message ID, and payload fields")
check(v2MessageID.count == 32 && v2MessageID == RelayProtocolV2.messageID(token: testToken, guid: "guid-demo"),
      "v2 message ID is stable and opaque-sized")
check(RelayHMAC.signature(token: testToken, canonical: v2Canonical).count == 64,
      "v2 signature is an HMAC-SHA256 hex digest")
let fixedV2Canonical = RelayProtocolV2.canonical(method: "POST", path: "/notify", timestamp: "1700000000", nonce: "123e4567-e89b-12d3-a456-426614174000", kind: .message, messageID: "0123456789abcdef0123456789abcdef", senderBase64: "SmFuZQ==", previewBase64: "SGVsbG8=")
check(RelayHMAC.signature(token: testToken, canonical: fixedV2Canonical) == "f4182f094163c132a423b6be497efd9d40e79f57503a19b644d2b2bce3c7f876",
      "v2 notify HMAC matches the protocol vector")
}
}
