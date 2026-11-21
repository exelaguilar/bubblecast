import AppKit
import Combine
import Contacts
import Foundation
import ServiceManagement
import SwiftUI

/// Settings-window state and the relay's orchestration. Database reads, delivery state, contact lookups,
/// and requests live in `MessagesDatabase`, `DeliveryQueue`, `ContactResolver`, and `RelayClient`; this
/// class wires them together on one serial queue and publishes UI state on the main thread.
final class RelayModel: ObservableObject {
    static let shared = RelayModel()

    /// Fallback poll when no file-change event arrives (events normally wake the relay immediately).
    private static let fallbackPollInterval: DispatchTimeInterval = .seconds(5)
    private static let healthCheckInterval: DispatchTimeInterval = .seconds(60)

    @Published var windowsAddress: String { didSet { updateUnsavedChanges() } }
    @Published var pairingToken: String { didSet { updateUnsavedChanges() } }
    @Published var includeSenderNames: Bool { didSet { updateUnsavedChanges() } }
    @Published var includeMessagePreviews: Bool { didSet { updateUnsavedChanges() } }
    @Published var suppressOldMessages: Bool { didSet { updateUnsavedChanges() } }
    @Published var maximumMessageAgeMinutes: Int { didSet { updateUnsavedChanges() } }
    @Published var senderNameOverrides: String { didSet { updateUnsavedChanges() } }
    @Published var notifyForReactions: Bool { didSet { updateUnsavedChanges() } }
    @Published var skipReadMessages: Bool { didSet { updateUnsavedChanges() } }
    @Published var quietWhileActive: Bool { didSet { updateUnsavedChanges() } }
    @Published var activeMinutes: Int { didSet { updateUnsavedChanges() } }
    @Published var useTLS: Bool { didSet { updateUnsavedChanges() } }
    @Published var tlsCertificateFingerprint: String { didSet { updateUnsavedChanges() } }
    @Published private(set) var hasUnsavedChanges = false
    @Published private(set) var status = "Starting…"
    @Published private(set) var statusLevel: RelayStatusLevel = .info
    @Published private(set) var windowsHealth = "Not checked yet"
    @Published private(set) var windowsHealthLevel: RelayStatusLevel = .info
    @Published private(set) var addressError: String?
    @Published private(set) var loginItemStatus = "Checking…"
    @Published private(set) var openAtLogin = false
    @Published private(set) var suppressedOldMessageCount: Int
    @Published private(set) var duplicateMessageCount: Int
    @Published private(set) var catchUpSummaryCount: Int
    @Published private(set) var cursorRebaselineCount: Int
    @Published private(set) var skippedSeenCount: Int
    /// Whether the notification settings are being shared with Windows.
    @Published private(set) var sharingStatus = "Not checked yet"
    @Published private(set) var lastSuccessfulNotification: Date?
    @Published private(set) var contactLookupSummary = "Not checked yet"
    @Published private(set) var timestampUnitSummary = "not observed"
    /// Names of Bubblecast receivers announcing on the local network.
    @Published private(set) var discoveredPCs: [String] = []

    private let databasePath: String
    private let queue = DispatchQueue(label: "local.bubblecast.database", qos: .utility)
    private let contactStore: CNContactStore
    private let client = RelayClient()
    private let discovery = WindowsDiscovery()
    private let defaults = UserDefaults.standard

    // Main-thread state.
    private var savedSettingsForUI: RelaySettings?
    private var contactObserver: NSObjectProtocol?

    // Queue-owned state. Only touched on `queue`.
    private var settings: RelaySettings
    private let database: MessagesDatabase
    private let delivery: DeliveryQueue
    private let contacts: ContactResolver
    private var monitor: MessagesChangeMonitor?
    private var pollTimer: DispatchSourceTimer?
    private var healthTimer: DispatchSourceTimer?
    private var retryWork: DispatchWorkItem?
    private var timestampUnitCounts: [TimestampUnit: Int] = [:]
    private var suppressedOldTotal: Int
    private var duplicateTotal: Int
    private var catchUpTotal: Int
    private var rebaselineTotal: Int
    private var skippedSeenTotal: Int
    /// Revision of the shared settings this Mac has (see `SharedSettings`).
    private var sharedRevision: Int64
    private var sharingPushInFlight = false

    private init() {
        let loadedSettings = RelaySettings.load()
        settings = loadedSettings
        savedSettingsForUI = loadedSettings
        windowsAddress = loadedSettings.windowsAddress
        pairingToken = loadedSettings.pairingToken
        includeSenderNames = loadedSettings.includeSenderNames
        includeMessagePreviews = loadedSettings.includeMessagePreviews
        suppressOldMessages = loadedSettings.suppressOldMessages
        maximumMessageAgeMinutes = loadedSettings.maximumMessageAgeMinutes
        senderNameOverrides = loadedSettings.senderNameOverrides
        notifyForReactions = loadedSettings.notifyForReactions
        skipReadMessages = loadedSettings.skipReadMessages
        quietWhileActive = loadedSettings.quietWhileActive
        activeMinutes = loadedSettings.activeMinutes
        useTLS = loadedSettings.useTLS
        tlsCertificateFingerprint = loadedSettings.tlsCertificateFingerprint

        let standard = UserDefaults.standard
        let suppressed = standard.integer(forKey: RelayPreferences.suppressedOldMessageCount)
        let duplicates = standard.integer(forKey: RelayPreferences.duplicateMessageCount)
        let catchUp = standard.integer(forKey: RelayPreferences.catchUpSummaryCount)
        let rebaselines = standard.integer(forKey: RelayPreferences.cursorRebaselineCount)
        let skippedSeen = standard.integer(forKey: RelayPreferences.skippedSeenCount)
        // Settings saved before sharing existed count as revision 1: older than any edit, but newer
        // than a PC that has never received them, so the Mac's existing choices seed Windows.
        let storedRevision = (standard.object(forKey: RelayPreferences.sharedSettingsRevision) as? NSNumber)?.int64Value ?? 1
        suppressedOldTotal = suppressed
        duplicateTotal = duplicates
        catchUpTotal = catchUp
        rebaselineTotal = rebaselines
        skippedSeenTotal = skippedSeen
        sharedRevision = max(1, storedRevision)
        suppressedOldMessageCount = suppressed
        duplicateMessageCount = duplicates
        catchUpSummaryCount = catchUp
        cursorRebaselineCount = rebaselines
        skippedSeenCount = skippedSeen
        lastSuccessfulNotification = standard.object(forKey: RelayPreferences.lastSuccessfulNotification) as? Date

        let path = (NSHomeDirectory() as NSString).appendingPathComponent("Library/Messages/chat.db")
        let store = CNContactStore()
        databasePath = path
        contactStore = store
        database = MessagesDatabase(path: path)
        delivery = DeliveryQueue(store: UserDefaultsDeliveryStore(),
                                 suppressOldMessages: loadedSettings.suppressOldMessages,
                                 maximumAge: TimeInterval(loadedSettings.maximumMessageAgeMinutes * 60))
        contacts = ContactResolver(store: store)

        delivery.onSuppressedOld = { [weak self] in
            guard let self else { return }
            self.addToCounter(&self.suppressedOldTotal, key: RelayPreferences.suppressedOldMessageCount, published: \.suppressedOldMessageCount)
            self.setStatus("Ignored an old, undated, or unexpected future message.")
        }
        delivery.onDuplicate = { [weak self] in
            guard let self else { return }
            self.addToCounter(&self.duplicateTotal, key: RelayPreferences.duplicateMessageCount, published: \.duplicateMessageCount)
        }
        delivery.shouldSkip = { [weak self] row in
            guard let self else { return false }
            let shared = self.settings.shared(revision: 0)
            guard shared.skipReadMessages || shared.quietWhileActive else { return false }
            return shared.shouldSkip(try? self.database.attention(forRowID: row.rowID))
        }
        delivery.onSkipped = { [weak self] in
            guard let self else { return }
            self.addToCounter(&self.skippedSeenTotal, key: RelayPreferences.skippedSeenCount, published: \.skippedSeenCount)
            self.setStatus("Skipped a message you've already seen or are replying to elsewhere.", level: .ok)
        }
        contacts.onSummary = { [weak self] summary in
            DispatchQueue.main.async { self?.contactLookupSummary = summary }
        }
        client.onSharedSettings = { [weak self] text in
            self?.queue.async { self?.receiveSharedSettings(text) }
        }
    }

    // MARK: - Lifecycle

    func start() {
        guard pollTimer == nil else { return }
        refreshLoginItemStatus()

        let poll = DispatchSource.makeTimerSource(queue: queue)
        poll.schedule(deadline: .now(), repeating: Self.fallbackPollInterval, leeway: .milliseconds(500))
        poll.setEventHandler { [weak self] in self?.pollMessages() }
        pollTimer = poll
        poll.resume()

        let health = DispatchSource.makeTimerSource(queue: queue)
        health.schedule(deadline: .now() + .seconds(2), repeating: Self.healthCheckInterval, leeway: .seconds(3))
        health.setEventHandler { [weak self] in self?.checkWindowsHealth() }
        healthTimer = health
        health.resume()

        queue.async {
            let monitor = MessagesChangeMonitor(databasePath: self.databasePath, queue: self.queue) { [weak self] in
                self?.pollMessages()
            }
            self.monitor = monitor
            monitor.start()
        }

        discovery.onChange = { [weak self] names in self?.discoveredPCs = names }
        discovery.start()

        requestContactsAccessIfNeeded(enabled: savedSettingsForUI?.includeSenderNames ?? false)
        contactObserver = NotificationCenter.default.addObserver(forName: .CNContactStoreDidChange, object: nil, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.queue.async {
                self.contacts.reset()
                self.delivery.clearResolvedNames()
            }
        }
    }

    // MARK: - Settings actions

    @discardableResult
    func saveSettings() -> Bool {
        windowsAddress = windowsAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        pairingToken = pairingToken.trimmingCharacters(in: .whitespacesAndNewlines)
        maximumMessageAgeMinutes = max(1, min(1440, maximumMessageAgeMinutes))
        activeMinutes = max(1, min(60, activeMinutes))
        guard windowsAddress.isEmpty || WindowsRelayEndpoint.url(address: windowsAddress, path: "/health") != nil else {
            addressError = "Enter a host name, IPv4 address, or bracketed IPv6 address. The relay uses port 8765."
            setStatus("Windows address is invalid.", level: .error)
            return false
        }
        addressError = windowsAddress.isEmpty ? "Enter the Windows PC address to check its status or send alerts." : nil
        let snapshot = makeSettingsFromDraft()
        let sharedChanged = savedSettingsForUI.map { !$0.shared(revision: 0).sameValues(as: snapshot.shared(revision: 0)) } ?? true
        do {
            try snapshot.save()
        } catch {
            setStatus("Couldn't save the pairing token to Keychain: \(error.localizedDescription)", level: .error)
            return false
        }
        savedSettingsForUI = snapshot
        hasUnsavedChanges = false
        if snapshot.includeSenderNames { requestContactsAccessIfNeeded(enabled: true) }
        queue.async {
            self.settings = snapshot
            self.delivery.settingsChanged(suppressOldMessages: snapshot.suppressOldMessages,
                                          maximumAge: TimeInterval(snapshot.maximumMessageAgeMinutes * 60))
            self.contacts.reset()
            self.client.reset()
            self.cancelRetry()
            if sharedChanged { self.setSharedRevision(max(SharedSettings.now(), self.sharedRevision + 1)) }
            self.pumpDelivery()
            self.checkWindowsHealth()
        }
        setStatus("Settings saved. Monitoring Messages.", level: .ok)
        if snapshot.useTLS && !RelayPinnedSessionDelegate.isValidFingerprint(snapshot.tlsCertificateFingerprint)
            && !snapshot.windowsAddress.isEmpty && !snapshot.pairingToken.isEmpty {
            learnWindowsCertificate(RelayEndpointConfiguration(settings: snapshot))
        }
        return true
    }

    /// Fills in a PC found on the network. The pairing token still comes from Windows.
    func useDiscoveredPC(_ name: String) {
        windowsAddress = WindowsDiscovery.address(forName: name)
        useTLS = true
        if !RelayPinnedSessionDelegate.isValidFingerprint(tlsCertificateFingerprint) { tlsCertificateFingerprint = "" }
        setStatus("Selected \(name). Paste the pairing token from Bubblecast on Windows, then click Save.", level: .info)
    }

    /// Trust on first use: learns the certificate Windows presents and fills it in for the user to compare
    /// with the fingerprint Windows shows. It is pinned only when the user saves it.
    private func learnWindowsCertificate(_ configuration: RelayEndpointConfiguration) {
        setStatus("Contacting Windows over TLS to learn its certificate…")
        client.learnFingerprint(configuration: configuration) { [weak self] fingerprint, outcome, reason in
            DispatchQueue.main.async {
                guard let self else { return }
                guard let fingerprint, RelayPinnedSessionDelegate.isValidFingerprint(fingerprint) else {
                    self.setStatus("Couldn't reach Windows over TLS: \(reason)", level: .error)
                    return
                }
                self.tlsCertificateFingerprint = fingerprint
                let suffix = String(fingerprint.suffix(6))
                if outcome == .rejected {
                    self.setStatus("Learned the Windows certificate (ends in \(suffix)), but Windows rejected the pairing token. Fix the token, check the fingerprint, then Save.", level: .warning)
                } else {
                    self.setStatus("Learned the Windows certificate (ends in \(suffix)). Check that it matches the fingerprint shown on Windows, then click Save to trust it.", level: .warning)
                }
            }
        }
    }

    func sendTest() {
        guard saveSettings() else { return }
        queue.async {
            self.client.send(path: "/notify", method: "POST", kind: .test, configuration: RelayEndpointConfiguration(settings: self.settings)) { [weak self] outcome, message in
                self?.setStatus(outcome == .delivered
                    ? "Test notification shown by Windows."
                    : "Test failed: \(message)", level: outcome == .delivered ? .ok : .error)
            }
        }
    }

    func setOpenAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            refreshLoginItemStatus()
        } catch {
            refreshLoginItemStatus()
            setStatus("Can't update Open at login: \(error.localizedDescription)", level: .error)
        }
    }

    func openLoginItemsSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }

    func resetCounters() {
        queue.async {
            self.suppressedOldTotal = 0
            self.duplicateTotal = 0
            self.catchUpTotal = 0
            self.rebaselineTotal = 0
            self.skippedSeenTotal = 0
            for key in [RelayPreferences.suppressedOldMessageCount, RelayPreferences.duplicateMessageCount,
                        RelayPreferences.catchUpSummaryCount, RelayPreferences.cursorRebaselineCount,
                        RelayPreferences.skippedSeenCount] {
                self.defaults.set(0, forKey: key)
            }
            DispatchQueue.main.async {
                self.suppressedOldMessageCount = 0
                self.duplicateMessageCount = 0
                self.catchUpSummaryCount = 0
                self.cursorRebaselineCount = 0
                self.skippedSeenCount = 0
            }
        }
    }

    func copyDiagnostics() {
        let dateText = lastSuccessfulNotification.map { ISO8601DateFormatter().string(from: $0) } ?? "never"
        let summary = [
            "Bubblecast macOS \(RelayBuildInfo.version)",
            "Windows health: \(windowsHealthLevel == .ok ? "reachable" : "not currently confirmed")",
            "Recent-message filtering: \(suppressOldMessages ? "enabled (\(maximumMessageAgeMinutes) minutes)" : "disabled")",
            "Old or undated rows suppressed: \(suppressedOldMessageCount)",
            "Duplicate rows suppressed: \(duplicateMessageCount)",
            "Messages delivered in catch-up summaries: \(catchUpSummaryCount)",
            "Cursor re-baselines: \(cursorRebaselineCount)",
            "Message timestamp units: \(timestampUnitSummary)",
            "Last successful notification: \(dateText)",
            "Sender-name setting: \(includeSenderNames ? "on" : "off")",
            "Message-preview setting: \(includeMessagePreviews ? "on" : "off")",
            "Pinned TLS: \(useTLS ? "on" : "off")",
            "Skip already-read messages: \(skipReadMessages ? "on" : "off")",
            "Quiet while active elsewhere: \(quietWhileActive ? "on (\(activeMinutes) minutes)" : "off")",
            "Messages skipped as already seen: \(skippedSeenCount)",
            "Settings sharing: \(sharingStatus)",
            "Contact lookup: \(contactLookupSummary)"
        ].joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(summary, forType: .string)
        setStatus("Privacy-safe diagnostics copied. They exclude addresses, tokens, sender names, and message text.", level: .ok)
    }

    // MARK: - Draft tracking (main thread)

    private func refreshLoginItemStatus() {
        switch SMAppService.mainApp.status {
        case .enabled:
            openAtLogin = true
            loginItemStatus = "Enabled"
        case .requiresApproval:
            openAtLogin = true
            loginItemStatus = "Approval needed in System Settings → General → Login Items"
        case .notRegistered:
            openAtLogin = false
            loginItemStatus = "Disabled"
        case .notFound:
            openAtLogin = false
            loginItemStatus = "Unavailable for this app location"
        @unknown default:
            openAtLogin = false
            loginItemStatus = "Unknown status"
        }
    }

    private func updateUnsavedChanges() {
        guard let savedSettingsForUI else { return }
        hasUnsavedChanges = makeSettingsFromDraft() != savedSettingsForUI
    }

    private func makeSettingsFromDraft() -> RelaySettings {
        RelaySettings(
            windowsAddress: windowsAddress.trimmingCharacters(in: .whitespacesAndNewlines),
            pairingToken: pairingToken.trimmingCharacters(in: .whitespacesAndNewlines),
            includeSenderNames: includeSenderNames,
            includeMessagePreviews: includeMessagePreviews,
            suppressOldMessages: suppressOldMessages,
            maximumMessageAgeMinutes: max(1, min(1440, maximumMessageAgeMinutes)),
            senderNameOverrides: senderNameOverrides,
            notifyForReactions: notifyForReactions,
            skipReadMessages: skipReadMessages,
            quietWhileActive: quietWhileActive,
            activeMinutes: max(1, min(60, activeMinutes)),
            useTLS: useTLS,
            tlsCertificateFingerprint: tlsCertificateFingerprint.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }

    // MARK: - Polling and delivery (queue)

    private func pollMessages() {
        do {
            let maximumRowID = try database.maximumRowID()
            switch delivery.align(toMaximumRowID: maximumRowID) {
            case .baselined:
                setStatus("Monitoring Messages. Existing conversations were ignored.", level: .ok)
                return
            case .rebaselined:
                addToCounter(&rebaselineTotal, key: RelayPreferences.cursorRebaselineCount, published: \.cursorRebaselineCount)
                setStatus("Messages database position changed. Monitoring from its current end.", level: .warning)
            case .unchanged:
                break
            }
            let rows = try database.incomingMessages(after: delivery.cursor ?? maximumRowID,
                                                     includeAssociatedMessages: settings.notifyForReactions)
            let added = delivery.ingest(rows)
            if !added.isEmpty {
                for row in added {
                    if let unit = row.timestampUnit { timestampUnitCounts[unit, default: 0] += 1 }
                }
                updateTimestampUnitSummary()
            }
            pumpDelivery()
        } catch {
            setStatus(MessagesDatabase.describe(error), level: .error)
        }
    }

    private func pumpDelivery() {
        let step = delivery.next()
        let configuration = RelayEndpointConfiguration(settings: settings)
        switch step {
        case .idle, .busy, .paused:
            return
        case .waitUntil(let date):
            scheduleRetry(at: date)
        case .sendCatchUp(let rows, let summary):
            let identity = rows.map(\.identity).joined(separator: "\n")
            client.send(path: "/notify", method: "POST", preview: summary, kind: .message,
                        messageIdentity: identity, configuration: configuration) { [weak self] outcome, reason in
                self?.queue.async { self?.finishDelivery(step, outcome: outcome, reason: reason) }
            }
        case .send(let queued):
            var message = queued
            if settings.includeSenderNames && !message.senderNameWasResolved {
                let name = contacts.name(for: message.senderAddress, overrides: settings.senderNameOverrides)
                delivery.recordResolvedName(name, forRowID: message.rowID)
                message.resolvedSenderName = name
            }
            let groupName = message.groupName.trimmingCharacters(in: .whitespacesAndNewlines)
            let participant = settings.includeSenderNames ? (message.resolvedSenderName ?? "") : ""
            let sender = groupName.isEmpty ? participant : (participant.isEmpty ? groupName : "\(groupName) · \(participant)")
            let preview = message.isAssociatedMessage
                ? "Associated message event received"
                : (settings.includeMessagePreviews ? message.preview : "")
            client.send(path: "/notify", method: "POST", sender: sender, preview: preview, kind: .message,
                        messageIdentity: message.identity, configuration: configuration) { [weak self] outcome, reason in
                self?.queue.async { self?.finishDelivery(step, outcome: outcome, reason: reason) }
            }
        }
    }

    private func finishDelivery(_ step: DeliveryStep, outcome: DeliveryOutcome, reason: String) {
        let retryAt = delivery.complete(step, outcome: outcome)
        switch outcome {
        case .delivered:
            if case .sendCatchUp(let rows, _) = step {
                addToCounter(&catchUpTotal, rows.count, key: RelayPreferences.catchUpSummaryCount, published: \.catchUpSummaryCount)
                setStatus("Catch-up summary sent to Windows.", level: .ok)
            } else {
                setStatus("New message alert sent to Windows.", level: .ok)
            }
            recordSuccessfulNotification()
            pumpDelivery()
        case .rejected:
            setStatus("Windows rejected the request — check the pairing token and the Mac's clock", level: .error)
            if let retryAt { scheduleRetry(at: retryAt) }
        case .notFound:
            setStatus("Windows does not recognize this request. Update the Windows receiver.", level: .error)
        case .transient:
            let seconds = max(1, Int((retryAt?.timeIntervalSinceNow ?? 1).rounded()))
            setStatus("Message waiting to relay; retrying in about \(seconds) seconds: \(reason)", level: .warning)
            if let retryAt { scheduleRetry(at: retryAt) }
        }
    }

    private func scheduleRetry(at date: Date) {
        retryWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.pumpDelivery() }
        retryWork = work
        queue.asyncAfter(deadline: .now() + max(0, date.timeIntervalSinceNow), execute: work)
    }

    private func cancelRetry() {
        retryWork?.cancel()
        retryWork = nil
    }

    /// Adds to a counter kept on the queue, saves it, and publishes the new total on the main thread.
    private func addToCounter(_ total: inout Int, _ amount: Int = 1, key: String, published: ReferenceWritableKeyPath<RelayModel, Int>) {
        total += amount
        defaults.set(total, forKey: key)
        let value = total
        DispatchQueue.main.async { self[keyPath: published] = value }
    }

    private func recordSuccessfulNotification() {
        let date = Date()
        defaults.set(date, forKey: RelayPreferences.lastSuccessfulNotification)
        DispatchQueue.main.async { self.lastSuccessfulNotification = date }
    }

    private func updateTimestampUnitSummary() {
        let summary = TimestampUnit.allCases
            .map { "\($0.rawValue): \(timestampUnitCounts[$0, default: 0])" }
            .joined(separator: ", ")
        DispatchQueue.main.async { self.timestampUnitSummary = summary }
    }

    // MARK: - Shared settings (queue)

    private func setSharedRevision(_ revision: Int64) {
        sharedRevision = revision
        defaults.set(NSNumber(value: revision), forKey: RelayPreferences.sharedSettingsRevision)
    }

    /// Windows returned its copy of the shared settings. The newer revision wins.
    private func receiveSharedSettings(_ text: String) {
        guard let remote = SharedSettings.parse(text) else { return }
        if remote.revision > sharedRevision {
            let updated = settings.applying(remote)
            updated.saveShared(to: defaults)
            setSharedRevision(remote.revision)
            settings = updated
            delivery.suppressOldMessages = updated.suppressOldMessages
            delivery.maximumAge = TimeInterval(updated.maximumMessageAgeMinutes * 60)
            delivery.clearResolvedNames()
            DispatchQueue.main.async { self.adoptSharedSettings(remote) }
            setSharingStatus(Self.sharedWithWindows)
            setStatus("Notification settings updated from Windows.", level: .ok)
            pumpDelivery()
        } else if remote.revision < sharedRevision {
            pushSharedSettings()
        } else {
            setSharingStatus(Self.sharedWithWindows)
        }
    }

    /// Sends this Mac's shared settings to Windows; one request at a time.
    private func pushSharedSettings() {
        guard !sharingPushInFlight, !settings.windowsAddress.isEmpty else { return }
        sharingPushInFlight = true
        let text = settings.shared(revision: sharedRevision).encoded()
        client.send(path: "/settings", method: "POST", settingsText: text, kind: .settings,
                    configuration: RelayEndpointConfiguration(settings: settings)) { [weak self] outcome, _ in
            self?.queue.async {
                guard let self else { return }
                self.sharingPushInFlight = false
                switch outcome {
                case .delivered: self.setSharingStatus(Self.sharedWithWindows)
                case .notFound: self.setSharingStatus("Update Bubblecast on Windows to 1.2 to change these there")
                case .rejected, .transient: break
                }
            }
        }
    }

    private static let sharedWithWindows = "Shared with Windows"

    /// Publishes the sharing status only when it changes (it's confirmed on every accepted request).
    private func setSharingStatus(_ value: String) {
        DispatchQueue.main.async {
            if self.sharingStatus != value { self.sharingStatus = value }
        }
    }

    /// Main thread: takes Windows' values for every shared setting the user isn't in the middle of editing.
    private func adoptSharedSettings(_ remote: SharedSettings) {
        guard let saved = savedSettingsForUI else { return }
        let updated = saved.applying(remote)
        savedSettingsForUI = updated
        if includeSenderNames == saved.includeSenderNames { includeSenderNames = updated.includeSenderNames }
        if includeMessagePreviews == saved.includeMessagePreviews { includeMessagePreviews = updated.includeMessagePreviews }
        if suppressOldMessages == saved.suppressOldMessages { suppressOldMessages = updated.suppressOldMessages }
        if maximumMessageAgeMinutes == saved.maximumMessageAgeMinutes { maximumMessageAgeMinutes = updated.maximumMessageAgeMinutes }
        if notifyForReactions == saved.notifyForReactions { notifyForReactions = updated.notifyForReactions }
        if skipReadMessages == saved.skipReadMessages { skipReadMessages = updated.skipReadMessages }
        if quietWhileActive == saved.quietWhileActive { quietWhileActive = updated.quietWhileActive }
        if activeMinutes == saved.activeMinutes { activeMinutes = updated.activeMinutes }
        updateUnsavedChanges()
        if updated.includeSenderNames && !saved.includeSenderNames { requestContactsAccessIfNeeded(enabled: true) }
    }

    // MARK: - Contacts and health

    private func requestContactsAccessIfNeeded(enabled: Bool) {
        guard enabled else { return }
        switch CNContactStore.authorizationStatus(for: .contacts) {
        case .authorized:
            setStatus("Monitoring Messages; sender contact names are enabled.", level: .ok)
            setContactLookupSummary("full Contacts access")
        case .notDetermined:
            contactStore.requestAccess(for: .contacts) { [weak self] granted, _ in
                self?.setStatus(granted
                    ? "Contacts access granted. Sender contact names are enabled."
                    : "Contacts access was declined; notifications will be generic.", level: granted ? .ok : .warning)
                self?.setContactLookupSummary(granted ? "Contacts access granted" : "Contacts access declined")
            }
        default:
            setStatus("Contacts access is off; notifications will be generic. Enable it in System Settings → Privacy & Security → Contacts.", level: .warning)
            setContactLookupSummary("Contacts access off or restricted")
        }
    }

    private func setContactLookupSummary(_ value: String) {
        DispatchQueue.main.async { self.contactLookupSummary = value }
    }

    private func checkWindowsHealth() {
        guard !settings.windowsAddress.isEmpty else {
            DispatchQueue.main.async {
                self.windowsHealth = "Enter the Windows PC address to check connectivity"
                self.windowsHealthLevel = .info
            }
            return
        }
        client.send(path: "/health", method: "GET", kind: .health, configuration: RelayEndpointConfiguration(settings: settings)) { [weak self] outcome, reason in
            DispatchQueue.main.async {
                guard let self else { return }
                switch outcome {
                case .delivered:
                    if self.sharingStatus == "Not checked yet" {
                        self.sharingStatus = "Windows doesn't share settings (update it to 1.2)"
                    }
                    self.windowsHealth = "Paired ✓ — Windows accepted the health check"
                    self.windowsHealthLevel = .ok
                case .rejected:
                    self.windowsHealth = "Reachable, but rejected — check the pairing token or Mac clock"
                    self.windowsHealthLevel = .warning
                case .notFound:
                    self.windowsHealth = "Reachable, but the Windows receiver does not recognize this endpoint"
                    self.windowsHealthLevel = .warning
                case .transient:
                    self.windowsHealth = "Unreachable: \(reason)"
                    self.windowsHealthLevel = .error
                }
            }
        }
    }

    private func setStatus(_ value: String, level: RelayStatusLevel = .info) {
        DispatchQueue.main.async {
            self.status = value
            self.statusLevel = level
        }
    }
}
