import Foundation

/// One incoming Messages row waiting to be relayed to Windows.
struct PendingMessage: Equatable {
    let rowID: Int64
    let guid: String
    let senderAddress: String
    let preview: String
    let messageDate: Date?
    let groupName: String
    let isAssociatedMessage: Bool
    let timestampUnit: TimestampUnit?
    var firstReadAt: Date = .distantPast
    var freshWhenRead = true
    var resolvedSenderName: String?
    var senderNameWasResolved = false

    /// Stable identity for de-duplication and the protocol message ID.
    var identity: String { guid.isEmpty ? "rowid:\(rowID)" : guid }
}

enum DeliveryOutcome: Equatable {
    case delivered
    case rejected
    case notFound
    case transient
}

/// What the relay should do next.
enum DeliveryStep: Equatable {
    /// Nothing is queued.
    case idle
    /// A request is already in flight.
    case busy
    /// Try again at this time (retry backoff or the pause after a rejection).
    case waitUntil(Date)
    /// Windows doesn't recognize the request; wait for the user to change settings.
    case paused
    /// Send this message.
    case send(PendingMessage)
    /// Send one count-only summary for rows that waited longer than the maximum age.
    case sendCatchUp([PendingMessage], summary: String)
}

enum CursorChange: Equatable {
    case unchanged
    /// First run: existing conversations were skipped.
    case baselined
    /// The database's maximum ROWID fell below the saved cursor (database replaced or restored).
    case rebaselined
}

/// Persistent delivery checkpoint.
protocol DeliveryStore: AnyObject {
    var cursor: Int64? { get set }
    var processedGUIDs: [String] { get set }
}

final class UserDefaultsDeliveryStore: DeliveryStore {
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var cursor: Int64? {
        get {
            guard defaults.object(forKey: RelayPreferences.lastDeliveredIncomingMessageRowID) != nil else { return nil }
            return Int64(defaults.integer(forKey: RelayPreferences.lastDeliveredIncomingMessageRowID))
        }
        set {
            if let newValue {
                defaults.set(newValue, forKey: RelayPreferences.lastDeliveredIncomingMessageRowID)
            } else {
                defaults.removeObject(forKey: RelayPreferences.lastDeliveredIncomingMessageRowID)
            }
        }
    }

    var processedGUIDs: [String] {
        get { defaults.stringArray(forKey: RelayPreferences.recentlyProcessedMessageGUIDs) ?? [] }
        set { defaults.set(newValue, forKey: RelayPreferences.recentlyProcessedMessageGUIDs) }
    }
}

/// The delivery state machine: freshness, de-duplication, ordering, retry backoff, the pause after a
/// rejection, catch-up summaries, and the persisted cursor. It has no I/O of its own; the caller sends
/// the request for each step and reports the outcome. Not thread-safe: use it from one serial queue.
final class DeliveryQueue {
    static let processedGUIDLimit = 500
    static let rejectedRetryDelay: TimeInterval = 60
    static let futureTolerance: TimeInterval = 300

    var suppressOldMessages: Bool
    var maximumAge: TimeInterval
    /// Called when a duplicate row is dropped.
    var onDuplicate: (() -> Void)?
    /// Called when an old, undated, or far-future row is dropped.
    var onSuppressedOld: (() -> Void)?

    private let store: DeliveryStore
    private let now: () -> Date
    private let jitter: (ClosedRange<Int>) -> Int
    private(set) var pending: [PendingMessage] = []
    private var pendingRowIDs = Set<Int64>()
    private var processedOrder: [String]
    private var processed: Set<String>
    private(set) var cursor: Int64?
    private var inFlight = false
    private var retryAttempt = 0
    private var nextAttemptAt: Date?
    private var pausedUntilSettingsChange = false

    init(store: DeliveryStore,
         suppressOldMessages: Bool,
         maximumAge: TimeInterval,
         now: @escaping () -> Date = Date.init,
         jitter: @escaping (ClosedRange<Int>) -> Int = { Int.random(in: $0) }) {
        self.store = store
        self.suppressOldMessages = suppressOldMessages
        self.maximumAge = maximumAge
        self.now = now
        self.jitter = jitter
        processedOrder = store.processedGUIDs
        processed = Set(processedOrder)
        cursor = store.cursor
    }

    var processedGUIDs: Set<String> { processed }

    /// Aligns the cursor with the database's current maximum ROWID.
    func align(toMaximumRowID maximum: Int64) -> CursorChange {
        guard let current = cursor else {
            setCursor(maximum)
            return .baselined
        }
        if current > maximum {
            setCursor(maximum)
            return .rebaselined
        }
        return .unchanged
    }

    /// Queues newly read rows. Rows already queued are ignored. Returns the rows that were added.
    @discardableResult
    func ingest(_ rows: [PendingMessage]) -> [PendingMessage] {
        var added: [PendingMessage] = []
        for var row in rows where pendingRowIDs.insert(row.rowID).inserted {
            row.firstReadAt = now()
            row.freshWhenRead = isFresh(row)
            pending.append(row)
            added.append(row)
        }
        return added
    }

    /// Stores a sender name resolved for the first queued row so it isn't looked up again on retry.
    func recordResolvedName(_ name: String?, forRowID rowID: Int64) {
        guard let index = pending.firstIndex(where: { $0.rowID == rowID }) else { return }
        pending[index].resolvedSenderName = name
        pending[index].senderNameWasResolved = true
    }

    /// Forgets resolved sender names (after Contacts or settings change).
    func clearResolvedNames() {
        for index in pending.indices {
            pending[index].resolvedSenderName = nil
            pending[index].senderNameWasResolved = false
        }
    }

    /// Clears the retry backoff and any pause, and applies new filtering settings.
    func settingsChanged(suppressOldMessages: Bool, maximumAge: TimeInterval) {
        self.suppressOldMessages = suppressOldMessages
        self.maximumAge = maximumAge
        retryAttempt = 0
        nextAttemptAt = nil
        pausedUntilSettingsChange = false
        clearResolvedNames()
    }

    /// Drops duplicate and stale rows at the front of the queue and returns the next step.
    /// `.send` and `.sendCatchUp` mark a request as in flight until `complete` is called.
    func next() -> DeliveryStep {
        if inFlight { return .busy }
        if pausedUntilSettingsChange { return .paused }
        if let at = nextAttemptAt, now() < at { return .waitUntil(at) }
        nextAttemptAt = nil

        while let first = pending.first {
            if !first.guid.isEmpty && processed.contains(first.guid) {
                onDuplicate?()
                finish(first, remember: false)
                continue
            }
            if !first.freshWhenRead {
                onSuppressedOld?()
                finish(first, remember: true)
                continue
            }
            break
        }
        guard let first = pending.first else { return .idle }

        let current = now()
        let expired = Array(pending.prefix { current.timeIntervalSince($0.firstReadAt) > maximumAge })
        inFlight = true
        if !expired.isEmpty {
            return .sendCatchUp(expired, summary: Self.catchUpSummary(count: expired.count))
        }
        return .send(first)
    }

    /// Records the result of the request started by `step`. Returns the retry time when one is scheduled.
    @discardableResult
    func complete(_ step: DeliveryStep, outcome: DeliveryOutcome) -> Date? {
        inFlight = false
        switch outcome {
        case .delivered:
            switch step {
            case .send(let message):
                finish(message, remember: true)
            case .sendCatchUp(let rows, _):
                for row in rows { finish(row, remember: true) }
            default:
                break
            }
            retryAttempt = 0
            nextAttemptAt = nil
            return nil
        case .rejected:
            nextAttemptAt = now().addingTimeInterval(Self.rejectedRetryDelay)
            return nextAttemptAt
        case .notFound:
            pausedUntilSettingsChange = true
            return nil
        case .transient:
            let base = min(60, 2 << min(retryAttempt, 5))
            retryAttempt += 1
            let delay = min(60, base + jitter(0...min(2, 60 - base)))
            nextAttemptAt = now().addingTimeInterval(TimeInterval(delay))
            return nextAttemptAt
        }
    }

    static func catchUpSummary(count: Int) -> String {
        count == 1
            ? "1 message arrived while this PC was unreachable"
            : "\(count) messages arrived while this PC was unreachable"
    }

    private func isFresh(_ row: PendingMessage) -> Bool {
        guard suppressOldMessages else { return true }
        guard let date = row.messageDate else { return false }
        let age = now().timeIntervalSince(date)
        return age <= maximumAge && age >= -Self.futureTolerance
    }

    private func finish(_ message: PendingMessage, remember: Bool) {
        guard let first = pending.first, first.rowID == message.rowID else { return }
        pending.removeFirst()
        pendingRowIDs.remove(message.rowID)
        setCursor(message.rowID)
        guard remember, !message.guid.isEmpty, processed.insert(message.guid).inserted else { return }
        processedOrder.append(message.guid)
        if processedOrder.count > Self.processedGUIDLimit {
            processed.remove(processedOrder.removeFirst())
        }
        store.processedGUIDs = processedOrder
    }

    private func setCursor(_ value: Int64) {
        cursor = value
        store.cursor = value
    }
}
