import AppKit
import Combine
import Contacts
import CryptoKit
import Foundation
import SQLite3
import ServiceManagement
import SwiftUI

final class RelayModel: ObservableObject {
    static let shared = RelayModel()

    @Published var windowsAddress: String { didSet { updateUnsavedChanges() } }
    @Published var pairingToken: String { didSet { updateUnsavedChanges() } }
    @Published var includeSenderNames: Bool { didSet { updateUnsavedChanges() } }
    @Published var includeMessagePreviews: Bool { didSet { updateUnsavedChanges() } }
    @Published var suppressOldMessages: Bool { didSet { updateUnsavedChanges() } }
    @Published var maximumMessageAgeMinutes: Int { didSet { updateUnsavedChanges() } }
    @Published var senderNameOverrides: String { didSet { updateUnsavedChanges() } }
    @Published var notifyForReactions: Bool { didSet { updateUnsavedChanges() } }
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
    @Published private(set) var lastSuccessfulNotification: Date?
    @Published private(set) var contactLookupSummary = "Not checked yet"
    @Published private(set) var timestampUnitSummary = "not observed"

    private let databasePath = (NSHomeDirectory() as NSString).appendingPathComponent("Library/Messages/chat.db")
    private let queue = DispatchQueue(label: "local.bubblecast.database", qos: .utility)
    private let contacts = CNContactStore()
    private var settings: RelaySettings
    private var savedSettingsForUI: RelaySettings?
    private var contactObserver: NSObjectProtocol?
    private var contactNameCache: [String: String] = [:]
    private var contactNameCacheMisses = Set<String>()
    private var timestampUnitCounts: [TimestampUnit: Int] = [:]
    private var timer: DispatchSourceTimer?
    private var healthTimer: DispatchSourceTimer?
    private var lastIncomingRowID: Int64?
    private var pendingMessages: [IncomingMessage] = []
    private var pendingRowIDs = Set<Int64>()
    private var processedGUIDs: Set<String>
    private var processedGUIDOrder: [String]
    private var suppressedOldMessageTotal = 0
    private var duplicateMessageTotal = 0
    private var relayInProgress = false
    private var retryAttempt = 0
    private var nextRetryAt: Date?
    private var deliveryPaused = false

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
        useTLS = loadedSettings.useTLS
        tlsCertificateFingerprint = loadedSettings.tlsCertificateFingerprint
        let savedSuppressedOldCount = UserDefaults.standard.integer(forKey: RelayPreferences.suppressedOldMessageCount)
        let savedDuplicateCount = UserDefaults.standard.integer(forKey: RelayPreferences.duplicateMessageCount)
        catchUpSummaryCount = UserDefaults.standard.integer(forKey: RelayPreferences.catchUpSummaryCount)
        cursorRebaselineCount = UserDefaults.standard.integer(forKey: RelayPreferences.cursorRebaselineCount)
        suppressedOldMessageCount = savedSuppressedOldCount
        duplicateMessageCount = savedDuplicateCount
        suppressedOldMessageTotal = savedSuppressedOldCount
        duplicateMessageTotal = savedDuplicateCount
        lastSuccessfulNotification = UserDefaults.standard.object(forKey: RelayPreferences.lastSuccessfulNotification) as? Date
        processedGUIDOrder = UserDefaults.standard.stringArray(forKey: RelayPreferences.recentlyProcessedMessageGUIDs) ?? []
        processedGUIDs = Set(processedGUIDOrder)
    }

    func start() {
        guard timer == nil else { return }
        refreshLoginItemStatus()
        let source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(deadline: .now(), repeating: .seconds(3), leeway: .milliseconds(400))
        source.setEventHandler { [weak self] in self?.pollMessages() }
        timer = source
        source.resume()

        let healthSource = DispatchSource.makeTimerSource(queue: queue)
        healthSource.schedule(deadline: .now() + .seconds(2), repeating: .seconds(60), leeway: .seconds(3))
        healthSource.setEventHandler { [weak self] in self?.checkWindowsHealth() }
        healthTimer = healthSource
        healthSource.resume()
        requestContactsAccessIfNeeded(enabled: savedSettingsForUI?.includeSenderNames ?? false)
        contactObserver = NotificationCenter.default.addObserver(forName: .CNContactStoreDidChange, object: nil, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.queue.async {
                self.contactNameCache.removeAll()
                self.contactNameCacheMisses.removeAll()
                for index in self.pendingMessages.indices {
                    self.pendingMessages[index].resolvedSenderName = nil
                    self.pendingMessages[index].senderNameWasResolved = false
                }
            }
        }
    }

    @discardableResult
    func saveSettings() -> Bool {
        windowsAddress = windowsAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        pairingToken = pairingToken.trimmingCharacters(in: .whitespacesAndNewlines)
        maximumMessageAgeMinutes = max(1, min(1440, maximumMessageAgeMinutes))
        guard windowsAddress.isEmpty || WindowsRelayEndpoint.url(address: windowsAddress, path: "/health") != nil else {
            addressError = "Enter a host name, IPv4 address, or bracketed IPv6 address. The relay uses port 8765."
            setStatus("Windows address is invalid.", level: .error)
            return false
        }
        addressError = windowsAddress.isEmpty ? "Enter the Windows PC address to check its status or send alerts." : nil
        let snapshot = RelaySettings(
            windowsAddress: windowsAddress,
            pairingToken: pairingToken,
            includeSenderNames: includeSenderNames,
            includeMessagePreviews: includeMessagePreviews,
            suppressOldMessages: suppressOldMessages,
            maximumMessageAgeMinutes: maximumMessageAgeMinutes,
            senderNameOverrides: senderNameOverrides,
            notifyForReactions: notifyForReactions,
            useTLS: useTLS,
            tlsCertificateFingerprint: tlsCertificateFingerprint.trimmingCharacters(in: .whitespacesAndNewlines)
        )
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
            for index in self.pendingMessages.indices {
                self.pendingMessages[index].resolvedSenderName = nil
                self.pendingMessages[index].senderNameWasResolved = false
            }
            self.contactNameCache.removeAll()
            self.contactNameCacheMisses.removeAll()
            self.retryAttempt = 0
            self.nextRetryAt = nil
            self.deliveryPaused = false
            self.pumpDelivery()
            self.checkWindowsHealth()
        }
        setStatus("Settings saved. Monitoring Messages.", level: .ok)
        return true
    }

    func sendTest() {
        guard saveSettings() else { return }
        queue.async {
            self.sendPing(kind: .test) { [weak self] outcome, message in
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
            self.suppressedOldMessageTotal = 0
            self.duplicateMessageTotal = 0
            UserDefaults.standard.set(0, forKey: RelayPreferences.suppressedOldMessageCount)
            UserDefaults.standard.set(0, forKey: RelayPreferences.duplicateMessageCount)
            UserDefaults.standard.set(0, forKey: RelayPreferences.catchUpSummaryCount)
            UserDefaults.standard.set(0, forKey: RelayPreferences.cursorRebaselineCount)
            DispatchQueue.main.async {
                self.suppressedOldMessageCount = 0
                self.duplicateMessageCount = 0
                self.catchUpSummaryCount = 0
                self.cursorRebaselineCount = 0
            }
        }
    }

    private func refreshLoginItemStatus() {
        let serviceStatus = SMAppService.mainApp.status
        switch serviceStatus {
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
            useTLS: useTLS,
            tlsCertificateFingerprint: tlsCertificateFingerprint.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }

    func copyDiagnostics() {
        let dateText = lastSuccessfulNotification.map {
            ISO8601DateFormatter().string(from: $0)
        } ?? "never"
        let summary = [
            "Bubblecast macOS \(RelayBuildInfo.version)",
            "Windows health: \(windowsHealth == "Windows receiver is reachable" ? "reachable" : "not currently confirmed")",
            "Recent-message filtering: \(suppressOldMessages ? "enabled (\(maximumMessageAgeMinutes) minutes)" : "disabled")",
            "Old or undated rows suppressed: \(suppressedOldMessageCount)",
            "Duplicate rows suppressed: \(duplicateMessageCount)",
            "Messages delivered in catch-up summaries: \(catchUpSummaryCount)",
            "Cursor re-baselines: \(cursorRebaselineCount)",
            "Message timestamp units: \(timestampUnitSummary)",
            "Last successful notification: \(dateText)",
            "Sender-name setting: \(includeSenderNames ? "on" : "off")",
            "Message-preview setting: \(includeMessagePreviews ? "on" : "off")",
            "Contact lookup: \(contactLookupSummary)"
        ].joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(summary, forType: .string)
        setStatus("Privacy-safe diagnostics copied. They exclude addresses, tokens, sender names, and message text.", level: .ok)
    }

    private func pollMessages() {
        var database: OpaquePointer?
        let openResult = sqlite3_open_v2(databasePath, &database, SQLITE_OPEN_READONLY, nil)
        guard openResult == SQLITE_OK, let db = database else {
            let code = database.map { sqlite3_errcode($0) } ?? openResult
            if let db = database { sqlite3_close(db) }
            setStatus(sqliteErrorDescription(code), level: .error)
            return
        }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 500)

        guard let maximumRowID = maximumMessageRowID(in: db) else {
            setStatus(sqliteErrorDescription(sqlite3_errcode(db)), level: .error)
            return
        }

        if lastIncomingRowID == nil {
            if UserDefaults.standard.object(forKey: RelayPreferences.lastDeliveredIncomingMessageRowID) != nil {
                // Resume from the last successfully delivered row after app/VM downtime.
                lastIncomingRowID = Int64(UserDefaults.standard.integer(forKey: RelayPreferences.lastDeliveredIncomingMessageRowID))
            } else {
                // Establish a baseline on first launch so old conversations do not trigger alerts.
                lastIncomingRowID = maximumRowID
                UserDefaults.standard.set(maximumRowID, forKey: RelayPreferences.lastDeliveredIncomingMessageRowID)
                setStatus("Monitoring Messages. Existing conversations were ignored.", level: .ok)
                return
            }
        }

        if let cursor = lastIncomingRowID, cursor > maximumRowID {
            lastIncomingRowID = maximumRowID
            UserDefaults.standard.set(maximumRowID, forKey: RelayPreferences.lastDeliveredIncomingMessageRowID)
            cursorRebaselineCount += 1
            UserDefaults.standard.set(cursorRebaselineCount, forKey: RelayPreferences.cursorRebaselineCount)
            let count = cursorRebaselineCount
            DispatchQueue.main.async { self.cursorRebaselineCount = count }
            setStatus("Messages database position changed. Monitoring from its current end.", level: .warning)
        }

        guard let incoming = incomingMessages(in: db, after: lastIncomingRowID!) else {
            setStatus(sqliteErrorDescription(sqlite3_errcode(db)), level: .error)
            return
        }
        for var message in incoming where pendingRowIDs.insert(message.rowID).inserted {
            message.firstReadAt = Date()
            message.freshWhenRead = isFreshWhenFirstRead(message)
            if let unit = message.timestampUnit { timestampUnitCounts[unit, default: 0] += 1 }
            pendingMessages.append(message)
        }
        updateTimestampUnitSummary()
        pumpDelivery()
    }

    private struct IncomingMessage {
        let rowID: Int64
        let senderAddress: String
        let preview: String
        let messageDate: Date?
        let guid: String
        let groupName: String
        let isAssociatedMessage: Bool
        let timestampUnit: TimestampUnit?
        var firstReadAt = Date()
        var freshWhenRead = true
        var resolvedSenderName: String?
        var senderNameWasResolved = false
    }

    private func isFreshWhenFirstRead(_ message: IncomingMessage) -> Bool {
        guard settings.suppressOldMessages else { return true }
        guard let date = message.messageDate else { return false }
        let age = Date().timeIntervalSince(date)
        return age <= TimeInterval(settings.maximumMessageAgeMinutes * 60) && age >= -300
    }

    private func maximumMessageRowID(in db: OpaquePointer) -> Int64? {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT COALESCE(MAX(ROWID),0) FROM message", -1, &statement, nil) == SQLITE_OK,
              let stmt = statement else { return nil }
        defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        return sqlite3_column_int64(stmt, 0)
    }

    private func incomingMessages(in db: OpaquePointer, after rowID: Int64) -> [IncomingMessage]? {
        let hasItemType = tableColumnExists("message", column: "item_type", in: db)
        let hasReactionType = tableColumnExists("message", column: "associated_message_type", in: db)
        let hasChatJoin = tableExists("chat_message_join", in: db) && tableExists("chat", in: db) &&
            tableColumnExists("chat_message_join", column: "message_id", in: db) && tableColumnExists("chat_message_join", column: "chat_id", in: db)
        let hasChatDisplayName = hasChatJoin && tableColumnExists("chat", column: "display_name", in: db)
        let hasChatRoomName = hasChatJoin && tableColumnExists("chat", column: "room_name", in: db)
        let reactionExpression = hasReactionType ? "COALESCE(m.associated_message_type, 0)" : "0"
        let displayNameExpression = hasChatDisplayName ? "COALESCE(c.display_name, '')" : "''"
        let roomNameExpression = hasChatRoomName ? "COALESCE(c.room_name, '')" : "''"
        let joins = hasChatJoin ? " LEFT JOIN chat_message_join AS cmj ON cmj.message_id = m.ROWID LEFT JOIN chat AS c ON c.ROWID = cmj.chat_id" : ""
        let itemTypeFilter = hasItemType ? " AND COALESCE(m.item_type, 0) = 0" : ""
        let reactionFilter = hasReactionType ? " AND (?2 = 1 OR COALESCE(m.associated_message_type, 0) = 0)" : ""
        let sql = "SELECT m.ROWID, COALESCE(h.id, ''), COALESCE(m.text, ''), COALESCE(m.date, 0), COALESCE(m.guid, ''), \(reactionExpression), \(displayNameExpression), \(roomNameExpression) FROM message AS m LEFT JOIN handle AS h ON h.ROWID = m.handle_id\(joins) WHERE m.is_from_me = 0\(itemTypeFilter)\(reactionFilter) AND m.ROWID > ?1 ORDER BY m.ROWID ASC LIMIT 100"
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let stmt = statement else {
            return nil
        }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_int64(stmt, 1, rowID)
        if hasReactionType { sqlite3_bind_int(stmt, 2, settings.notifyForReactions ? 1 : 0) }
        var messages: [IncomingMessage] = []
        var result = sqlite3_step(stmt)
        while result == SQLITE_ROW {
            let address = sqlite3_column_text(stmt, 1).map { String(cString: $0) } ?? ""
            let rawPreview = sqlite3_column_text(stmt, 2).map { String(cString: $0) } ?? ""
            let rawDate = sqlite3_column_double(stmt, 3)
            let guid = sqlite3_column_text(stmt, 4).map { String(cString: $0) } ?? ""
            let isAssociatedMessage = sqlite3_column_int(stmt, 5) != 0
            let displayName = sqlite3_column_text(stmt, 6).map { String(cString: $0) } ?? ""
            let roomName = sqlite3_column_text(stmt, 7).map { String(cString: $0) } ?? ""
            let preview = rawPreview
                .components(separatedBy: .newlines)
                .joined(separator: " ")
                .components(separatedBy: .whitespaces)
                .filter { !$0.isEmpty }
                .joined(separator: " ")
            let decodedDate = TimestampDecoder.decode(rawDate)
            messages.append(IncomingMessage(
                rowID: sqlite3_column_int64(stmt, 0),
                senderAddress: address,
                preview: String((preview.isEmpty && isAssociatedMessage ? "Associated message event received" : preview).prefix(180)),
                messageDate: decodedDate?.date,
                guid: guid.trimmingCharacters(in: .whitespacesAndNewlines),
                groupName: displayName.isEmpty ? roomName : displayName,
                isAssociatedMessage: isAssociatedMessage,
                timestampUnit: decodedDate?.unit
            ))
            result = sqlite3_step(stmt)
        }
        guard result == SQLITE_DONE else { return nil }
        return messages
    }

    private func tableExists(_ name: String, in db: OpaquePointer) -> Bool {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?1 LIMIT 1", -1, &statement, nil) == SQLITE_OK,
              let stmt = statement else { return false }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, name, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self))
        return sqlite3_step(stmt) == SQLITE_ROW
    }

    private func tableColumnExists(_ table: String, column: String, in db: OpaquePointer) -> Bool {
        guard ["message", "chat", "chat_message_join"].contains(table) else { return false }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "PRAGMA table_info(\(table))", -1, &statement, nil) == SQLITE_OK,
              let stmt = statement else { return false }
        defer { sqlite3_finalize(stmt) }
        while sqlite3_step(stmt) == SQLITE_ROW {
            if let value = sqlite3_column_text(stmt, 1), String(cString: value).caseInsensitiveCompare(column) == .orderedSame {
                return true
            }
        }
        return false
    }

    private func pumpDelivery() {
        guard !relayInProgress, !deliveryPaused else { return }
        if let retryAt = nextRetryAt, Date() < retryAt { return }
        nextRetryAt = nil

        while let message = pendingMessages.first {
            if !message.guid.isEmpty && processedGUIDs.contains(message.guid) {
                recordDuplicateSuppression()
                finishProcessing(message, rememberGUID: false)
                continue
            }
            if !message.freshWhenRead {
                recordOldMessageSuppression()
                finishProcessing(message, rememberGUID: true)
                setStatus("Ignored an old, undated, or unexpected future message.")
                continue
            }
            break
        }

        guard let queuedMessage = pendingMessages.first else { return }
        var message = queuedMessage
        let ageLimit = TimeInterval(settings.maximumMessageAgeMinutes * 60)
        let expiredRows = Array(pendingMessages.prefix { Date().timeIntervalSince($0.firstReadAt) > ageLimit })
        if !expiredRows.isEmpty {
            let count = expiredRows.count
            let summary = count == 1
                ? "1 message arrived while this PC was unreachable"
                : "\(count) messages arrived while this PC was unreachable"
            relayInProgress = true
            let summaryIdentity = expiredRows.map { $0.guid.isEmpty ? "rowid:\($0.rowID)" : $0.guid }.joined(separator: "\n")
            sendPing(sender: "", preview: summary, kind: .message, messageIdentity: summaryIdentity) { [weak self] outcome, reason in
                guard let self else { return }
                self.queue.async {
                    self.relayInProgress = false
                    if outcome == .delivered {
                        for row in expiredRows { self.finishProcessing(row, rememberGUID: true) }
                        self.catchUpSummaryCount += count
                        UserDefaults.standard.set(self.catchUpSummaryCount, forKey: RelayPreferences.catchUpSummaryCount)
                        let total = self.catchUpSummaryCount
                        DispatchQueue.main.async { self.catchUpSummaryCount = total }
                        self.recordSuccessfulNotification()
                        self.setStatus("Catch-up summary sent to Windows.", level: .ok)
                        self.resetRetryState()
                        self.pumpDelivery()
                    } else {
                        self.handleDeliveryFailure(outcome: outcome, reason: reason)
                    }
                }
            }
            return
        }

        relayInProgress = true
        if settings.includeSenderNames && !message.senderNameWasResolved {
            message.resolvedSenderName = contactName(for: message.senderAddress)
            message.senderNameWasResolved = true
            pendingMessages[0] = message
        }
        let groupName = message.groupName.trimmingCharacters(in: .whitespacesAndNewlines)
        let participant = settings.includeSenderNames ? (message.resolvedSenderName ?? "") : ""
        let sender = groupName.isEmpty ? participant : (participant.isEmpty ? groupName : "\(groupName) · \(participant)")
        let preview = message.isAssociatedMessage ? "Associated message event received" : (settings.includeMessagePreviews ? message.preview : "")
        let messageIdentity = message.guid.isEmpty ? "rowid:\(message.rowID)" : message.guid
        sendPing(sender: sender, preview: preview, kind: .message, messageIdentity: messageIdentity) { [weak self] outcome, reason in
            guard let self else { return }
            self.queue.async {
                self.relayInProgress = false
                if outcome == .delivered {
                    self.finishProcessing(message, rememberGUID: true)
                    self.recordSuccessfulNotification()
                    self.setStatus("New message alert sent to Windows.", level: .ok)
                    self.resetRetryState()
                    self.pumpDelivery()
                } else {
                    self.handleDeliveryFailure(outcome: outcome, reason: reason)
                }
            }
        }
    }

    private func handleDeliveryFailure(outcome: DeliveryOutcome, reason: String) {
        switch outcome {
        case .rejected:
            deliveryPaused = true
            nextRetryAt = Date().addingTimeInterval(60)
            setStatus("Windows rejected the request — check the pairing token and the Mac's clock", level: .error)
            queue.asyncAfter(deadline: .now() + .seconds(60)) { [weak self] in
                guard let self else { return }
                self.deliveryPaused = false
                self.pumpDelivery()
            }
        case .notFound:
            deliveryPaused = true
            setStatus("Windows does not recognize this request. Update the Windows receiver.", level: .error)
        case .transient:
            let base = min(60, 2 << min(retryAttempt, 5))
            retryAttempt += 1
            let delay = min(60, base + Int.random(in: 0...min(2, 60 - base)))
            nextRetryAt = Date().addingTimeInterval(TimeInterval(delay))
            setStatus("Message waiting to relay; retrying in about \(delay) seconds: \(reason)", level: .warning)
        case .delivered:
            break
        }
    }

    private func resetRetryState() {
        retryAttempt = 0
        nextRetryAt = nil
        deliveryPaused = false
    }

    private func finishProcessing(_ message: IncomingMessage, rememberGUID: Bool) {
        guard !pendingMessages.isEmpty, pendingMessages[0].rowID == message.rowID else { return }
        pendingMessages.removeFirst()
        pendingRowIDs.remove(message.rowID)
        lastIncomingRowID = message.rowID
        UserDefaults.standard.set(message.rowID, forKey: RelayPreferences.lastDeliveredIncomingMessageRowID)
        if rememberGUID && !message.guid.isEmpty {
            if processedGUIDs.insert(message.guid).inserted {
                processedGUIDOrder.append(message.guid)
                if processedGUIDOrder.count > 500 {
                    processedGUIDs.remove(processedGUIDOrder.removeFirst())
                }
                UserDefaults.standard.set(processedGUIDOrder, forKey: RelayPreferences.recentlyProcessedMessageGUIDs)
            }
        }
    }

    private func recordOldMessageSuppression() {
        suppressedOldMessageTotal += 1
        UserDefaults.standard.set(suppressedOldMessageTotal, forKey: RelayPreferences.suppressedOldMessageCount)
        let count = suppressedOldMessageTotal
        DispatchQueue.main.async { self.suppressedOldMessageCount = count }
    }

    private func recordDuplicateSuppression() {
        duplicateMessageTotal += 1
        UserDefaults.standard.set(duplicateMessageTotal, forKey: RelayPreferences.duplicateMessageCount)
        let count = duplicateMessageTotal
        DispatchQueue.main.async { self.duplicateMessageCount = count }
    }

    private func recordSuccessfulNotification() {
        let date = Date()
        UserDefaults.standard.set(date, forKey: RelayPreferences.lastSuccessfulNotification)
        DispatchQueue.main.async { self.lastSuccessfulNotification = date }
    }

    private func sqliteErrorDescription(_ code: Int32) -> String {
        if code == SQLITE_CANTOPEN || code == SQLITE_AUTH || code == SQLITE_PERM {
            return "Can't read Messages. Give the built app Full Disk Access in System Settings → Privacy & Security."
        }
        return "Messages database is temporarily unavailable (SQLite error \(code))."
    }

    private func updateTimestampUnitSummary() {
        let summary = TimestampUnit.allCases
            .map { "\($0.rawValue): \(timestampUnitCounts[$0, default: 0])" }
            .joined(separator: ", ")
        DispatchQueue.main.async { self.timestampUnitSummary = summary }
    }

    private func requestContactsAccessIfNeeded(enabled: Bool) {
        guard enabled else { return }
        switch CNContactStore.authorizationStatus(for: .contacts) {
        case .authorized:
            setStatus("Monitoring Messages; sender contact names are enabled.", level: .ok)
            setContactLookupSummary("full Contacts access")
        case .notDetermined:
            contacts.requestAccess(for: .contacts) { [weak self] granted, _ in
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

    private func contactName(for address: String) -> String? {
        guard !address.isEmpty else {
            setContactLookupSummary("sender address unavailable")
            return nil
        }

        let normalizedAddress = normalizeAddress(address)
        for line in settings.senderNameOverrides.components(separatedBy: .newlines) {
            guard let separator = line.firstIndex(of: "=") else { continue }
            let key = normalizeAddress(String(line[..<separator]).trimmingCharacters(in: .whitespacesAndNewlines))
            let override = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespacesAndNewlines)
            let sameSender = key == normalizedAddress || (!address.contains("@") && phoneNumbersMatch(key, normalizedAddress))
            if !key.isEmpty, sameSender, !override.isEmpty {
                setContactLookupSummary("matched a local name override")
                return String(override.prefix(80))
            }
        }

        if let cached = contactNameCache[normalizedAddress] { return cached }
        if contactNameCacheMisses.contains(normalizedAddress) { return nil }

        guard hasContactAccess else {
            setContactLookupSummary("Contacts access off or restricted")
            return nil
        }

        let keys: [CNKeyDescriptor] = [
            CNContactGivenNameKey as CNKeyDescriptor,
            CNContactFamilyNameKey as CNKeyDescriptor,
            CNContactOrganizationNameKey as CNKeyDescriptor,
            CNContactPhoneNumbersKey as CNKeyDescriptor,
            CNContactEmailAddressesKey as CNKeyDescriptor
        ]
        let isEmail = normalizedAddress.contains("@")
        let predicate = isEmail
            ? CNContact.predicateForContacts(matchingEmailAddress: normalizedAddress)
            : CNContact.predicateForContacts(matching: CNPhoneNumber(stringValue: address.replacingOccurrences(of: "^tel:", with: "", options: [.regularExpression, .caseInsensitive])))

        if let matches = try? contacts.unifiedContacts(matching: predicate, keysToFetch: keys),
           let match = matches.first,
           let name = displayName(for: match) {
            setContactLookupSummary("matched using Contacts")
            contactNameCache[normalizedAddress] = name
            return name
        }

        // Predicate phone matching can miss equivalent formats (+country code,
        // punctuation, or a local number). Compare normalized values only among
        // contacts the user has authorized this app to see.
        let request = CNContactFetchRequest(keysToFetch: keys)
        var fallback: String?
        do {
            try contacts.enumerateContacts(with: request) { contact, stop in
                let contactMatches: Bool
                if isEmail {
                    contactMatches = contact.emailAddresses.contains {
                        normalizeAddress(String($0.value)) == normalizedAddress
                    }
                } else {
                    contactMatches = contact.phoneNumbers.contains {
                        phoneNumbersMatch($0.value.stringValue, address)
                    }
                }
                if contactMatches {
                    fallback = displayName(for: contact)
                    stop.pointee = true
                }
            }
        } catch {
            setContactLookupSummary("contact lookup failed; check Contacts permission")
            return nil
        }
        if fallback != nil {
            setContactLookupSummary("matched after normalizing the sender address")
            contactNameCache[normalizedAddress] = fallback!
        } else {
            setContactLookupSummary("no matching saved contact")
            contactNameCacheMisses.insert(normalizedAddress)
        }
        return fallback
    }

    private var hasContactAccess: Bool {
        let authorization = CNContactStore.authorizationStatus(for: .contacts)
        return authorization == .authorized
    }

    private func normalizeAddress(_ address: String) -> String {
        var value = address.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if value.hasPrefix("mailto:") { value.removeFirst("mailto:".count) }
        if value.hasPrefix("tel:") { value.removeFirst("tel:".count) }
        return value.contains("@") ? value : String(value.filter(\.isNumber))
    }

    private func phoneNumbersMatch(_ lhs: String, _ rhs: String) -> Bool {
        let leftDigits = lhs.filter(\.isNumber)
        let rightDigits = rhs.filter(\.isNumber)
        guard !leftDigits.isEmpty, !rightDigits.isEmpty else { return false }
        if leftDigits == rightDigits { return true }
        return leftDigits.count >= 10 && rightDigits.count >= 10 && leftDigits.suffix(10) == rightDigits.suffix(10)
    }

    private func displayName(for contact: CNContact) -> String? {
        let personalName = [contact.givenName, contact.familyName]
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .joined(separator: " ")
        let name = personalName.isEmpty ? contact.organizationName : personalName
        return name.isEmpty ? nil : String(name.prefix(80))
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
        sendSignedRequest(path: "/health", method: "GET", sender: "", preview: "", kind: .health, messageIdentity: "") { [weak self] outcome, reason in
            DispatchQueue.main.async {
                guard let self else { return }
                switch outcome {
                case .delivered:
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

    private enum DeliveryOutcome: Equatable {
        case delivered, rejected, notFound, transient
    }

    private func sendPing(sender: String = "", preview: String = "", kind: RelayRequestKind = .message, messageIdentity: String = UUID().uuidString, completion: ((DeliveryOutcome, String) -> Void)? = nil) {
        sendSignedRequest(path: "/notify", method: "POST", sender: sender, preview: preview, kind: kind, messageIdentity: messageIdentity, completion: completion)
    }

    private func sendSignedRequest(path: String, method: String, sender: String, preview: String, kind: RelayRequestKind, messageIdentity: String, completion: ((DeliveryOutcome, String) -> Void)?) {
        let address = settings.windowsAddress
        let token = settings.pairingToken
        let useTLSForRequest = settings.useTLS
        let tlsFingerprintForRequest = settings.tlsCertificateFingerprint
        guard !token.isEmpty,
              let url = WindowsRelayEndpoint.url(address: address, path: path, useTLS: useTLSForRequest) else {
            completion?(.transient, "enter a valid Windows host name/IP and pairing token")
            return
        }
        if useTLSForRequest && !RelayPinnedSessionDelegate.isValidFingerprint(tlsFingerprintForRequest) {
            completion?(.transient, "enter the 64-character SHA-256 TLS fingerprint shown by Windows")
            return
        }

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 5
        let senderBase64 = sender.isEmpty ? "" : Data(sender.utf8).base64EncodedString()
        let previewBase64 = preview.isEmpty ? "" : Data(preview.utf8).base64EncodedString()
        let timestamp = String(Int(Date().timeIntervalSince1970))
        let nonce = UUID().uuidString.lowercased()
        let messageID = kind == .message ? RelayProtocolV2.messageID(token: token, guid: messageIdentity) : ""
        let canonical = RelayProtocolV2.canonical(method: method, path: path, timestamp: timestamp, nonce: nonce, kind: kind, messageID: messageID, senderBase64: senderBase64, previewBase64: previewBase64)
        let signature = RelayProtocolV1.signature(token: token, canonical: canonical)
        request.setValue("2", forHTTPHeaderField: "X-Relay-Version")
        request.setValue(kind.rawValue, forHTTPHeaderField: "X-Relay-Kind")
        request.setValue(messageID, forHTTPHeaderField: "X-Relay-Message-Id")
        request.setValue(timestamp, forHTTPHeaderField: "X-Relay-Time")
        request.setValue(nonce, forHTTPHeaderField: "X-Relay-Nonce")
        request.setValue(signature, forHTTPHeaderField: "X-Relay-Signature")
        if !senderBase64.isEmpty { request.setValue(senderBase64, forHTTPHeaderField: "X-Sender-Base64") }
        if !previewBase64.isEmpty { request.setValue(previewBase64, forHTTPHeaderField: "X-Preview-Base64") }
        let session: URLSession
        if useTLSForRequest {
            let delegate = RelayPinnedSessionDelegate(expectedFingerprint: tlsFingerprintForRequest)
            session = URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil)
        } else {
            session = .shared
        }
        session.dataTask(with: request) { _, response, error in
            if useTLSForRequest { session.finishTasksAndInvalidate() }
            if let error = error {
                completion?(.transient, error.localizedDescription)
                return
            }
            guard let http = response as? HTTPURLResponse else {
                completion?(.transient, "no HTTP response")
                return
            }
            let outcome: DeliveryOutcome
            switch http.statusCode {
            case 204:
                outcome = .delivered
                self.retireLegacyTokenIfMigrated(token: token)
            case 403:
                outcome = .rejected
                let code = http.value(forHTTPHeaderField: "X-Relay-Error") ?? "auth"
                completion?(outcome, "Windows rejected the request (\(code))")
                return
            case 404: outcome = .notFound
            default: outcome = .transient
            }
            completion?(outcome, "Windows returned HTTP \(http.statusCode)")
        }.resume()
    }

    private func retireLegacyTokenIfMigrated(token: String) {
        do {
            // Only remove the old preference after a signed request using this token
            // has been accepted by Windows.
            try KeychainTokenStore().writeToken(token)
            RelaySettings.retireLegacyToken()
        } catch {
            setStatus("Windows accepted a request, but the old token preference could not be removed.", level: .warning)
        }
    }

    private func setStatus(_ value: String, level: RelayStatusLevel = .info) {
        DispatchQueue.main.async {
            self.status = value
            self.statusLevel = level
        }
    }
}
