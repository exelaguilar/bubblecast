import Foundation

enum RelayStatusLevel {
    case ok
    case info
    case warning
    case error
}

enum RelayPreferences {
    static let windowsAddress = "windowsAddress"
    static let includeSenderNames = "includeSenderNames"
    static let includeMessagePreviews = "includeMessagePreviews"
    static let suppressOldMessages = "suppressOldMessages"
    static let maximumMessageAgeMinutes = "maximumMessageAgeMinutes"
    static let senderNameOverrides = "senderNameOverrides"
    static let notifyForReactions = "notifyForReactions"
    static let skipReadMessages = "skipReadMessages"
    static let quietWhileActive = "quietWhileActive"
    static let activeMinutes = "activeMinutes"
    static let sharedSettingsRevision = "sharedSettingsRevision"
    static let useTLS = "useTLS"
    static let tlsCertificateFingerprint = "tlsCertificateFingerprint"
    static let lastDeliveredIncomingMessageRowID = "lastDeliveredIncomingMessageRowID"
    static let recentlyProcessedMessageGUIDs = "recentlyProcessedMessageGUIDs"
    static let suppressedOldMessageCount = "suppressedOldMessageCount"
    static let duplicateMessageCount = "duplicateMessageCount"
    static let catchUpSummaryCount = "catchUpSummaryCount"
    static let cursorRebaselineCount = "cursorRebaselineCount"
    static let skippedSeenCount = "skippedSeenCount"
    static let lastSuccessfulNotification = "lastSuccessfulNotification"
}

struct RelaySettings: Equatable {
    let windowsAddress: String
    let pairingToken: String
    let includeSenderNames: Bool
    let includeMessagePreviews: Bool
    let suppressOldMessages: Bool
    let maximumMessageAgeMinutes: Int
    let senderNameOverrides: String
    let notifyForReactions: Bool
    let skipReadMessages: Bool
    let quietWhileActive: Bool
    let activeMinutes: Int
    let useTLS: Bool
    let tlsCertificateFingerprint: String

    static func load(from defaults: UserDefaults = .standard, tokenStore: PairingTokenStore = KeychainTokenStore()) -> RelaySettings {
        // The token lives only in the Keychain. An unreadable item loads as empty; saving writes a fresh one.
        let pairingToken = ((try? tokenStore.readToken()) ?? nil) ?? ""
        let shared = SharedSettings.defaults
        func clamp(_ value: Int, _ range: ClosedRange<Int>) -> Int { min(range.upperBound, max(range.lowerBound, value)) }
        return RelaySettings(
            windowsAddress: defaults.string(forKey: RelayPreferences.windowsAddress) ?? "",
            pairingToken: pairingToken,
            includeSenderNames: defaults.object(forKey: RelayPreferences.includeSenderNames) as? Bool ?? shared.includeSenderNames,
            includeMessagePreviews: defaults.object(forKey: RelayPreferences.includeMessagePreviews) as? Bool ?? shared.includeMessagePreviews,
            suppressOldMessages: defaults.object(forKey: RelayPreferences.suppressOldMessages) as? Bool ?? shared.suppressOldMessages,
            maximumMessageAgeMinutes: clamp(defaults.object(forKey: RelayPreferences.maximumMessageAgeMinutes) as? Int ?? shared.maximumMessageAgeMinutes,
                                            SharedSettings.ageRange),
            senderNameOverrides: defaults.string(forKey: RelayPreferences.senderNameOverrides) ?? "",
            notifyForReactions: defaults.object(forKey: RelayPreferences.notifyForReactions) as? Bool ?? shared.notifyForReactions,
            skipReadMessages: defaults.object(forKey: RelayPreferences.skipReadMessages) as? Bool ?? shared.skipReadMessages,
            quietWhileActive: defaults.object(forKey: RelayPreferences.quietWhileActive) as? Bool ?? shared.quietWhileActive,
            activeMinutes: clamp(defaults.object(forKey: RelayPreferences.activeMinutes) as? Int ?? shared.activeMinutes,
                                 SharedSettings.activeMinutesRange),
            useTLS: defaults.object(forKey: RelayPreferences.useTLS) as? Bool ?? true,
            tlsCertificateFingerprint: defaults.string(forKey: RelayPreferences.tlsCertificateFingerprint) ?? ""
        )
    }

    func save(to defaults: UserDefaults = .standard, tokenStore: PairingTokenStore = KeychainTokenStore()) throws {
        try tokenStore.writeToken(pairingToken)
        defaults.set(windowsAddress, forKey: RelayPreferences.windowsAddress)
        defaults.set(senderNameOverrides, forKey: RelayPreferences.senderNameOverrides)
        saveShared(to: defaults)
        defaults.set(useTLS, forKey: RelayPreferences.useTLS)
        defaults.set(tlsCertificateFingerprint, forKey: RelayPreferences.tlsCertificateFingerprint)
    }

    /// Writes only the settings shared with Windows; the Keychain token is untouched.
    func saveShared(to defaults: UserDefaults = .standard) {
        defaults.set(includeSenderNames, forKey: RelayPreferences.includeSenderNames)
        defaults.set(includeMessagePreviews, forKey: RelayPreferences.includeMessagePreviews)
        defaults.set(suppressOldMessages, forKey: RelayPreferences.suppressOldMessages)
        defaults.set(maximumMessageAgeMinutes, forKey: RelayPreferences.maximumMessageAgeMinutes)
        defaults.set(notifyForReactions, forKey: RelayPreferences.notifyForReactions)
        defaults.set(skipReadMessages, forKey: RelayPreferences.skipReadMessages)
        defaults.set(quietWhileActive, forKey: RelayPreferences.quietWhileActive)
        defaults.set(activeMinutes, forKey: RelayPreferences.activeMinutes)
    }

    /// The shared portion, stamped with `revision`.
    func shared(revision: Int64) -> SharedSettings {
        SharedSettings(revision: revision,
                       includeSenderNames: includeSenderNames,
                       includeMessagePreviews: includeMessagePreviews,
                       suppressOldMessages: suppressOldMessages,
                       maximumMessageAgeMinutes: maximumMessageAgeMinutes,
                       notifyForReactions: notifyForReactions,
                       skipReadMessages: skipReadMessages,
                       quietWhileActive: quietWhileActive,
                       activeMinutes: activeMinutes)
    }

    /// These settings with the shared portion replaced.
    func applying(_ shared: SharedSettings) -> RelaySettings {
        RelaySettings(windowsAddress: windowsAddress,
                      pairingToken: pairingToken,
                      includeSenderNames: shared.includeSenderNames,
                      includeMessagePreviews: shared.includeMessagePreviews,
                      suppressOldMessages: shared.suppressOldMessages,
                      maximumMessageAgeMinutes: shared.maximumMessageAgeMinutes,
                      senderNameOverrides: senderNameOverrides,
                      notifyForReactions: shared.notifyForReactions,
                      skipReadMessages: shared.skipReadMessages,
                      quietWhileActive: shared.quietWhileActive,
                      activeMinutes: shared.activeMinutes,
                      useTLS: useTLS,
                      tlsCertificateFingerprint: tlsCertificateFingerprint)
    }
}

enum WindowsRelayEndpoint {
    static let port = 8765
    static let tlsPort = 8766

    static func url(address: String, path: String, useTLS: Bool = false) -> URL? {
        var hostText = address.trimmingCharacters(in: .whitespacesAndNewlines)
        hostText = hostText.replacingOccurrences(of: "^https?://", with: "", options: [.regularExpression, .caseInsensitive])
        hostText = hostText.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !hostText.isEmpty,
              let parsed = URLComponents(string: "http://\(hostText)"),
              parsed.user == nil, parsed.password == nil, parsed.port == nil,
              parsed.query == nil, parsed.fragment == nil,
              parsed.path.isEmpty || parsed.path == "/",
              let host = parsed.host, !host.isEmpty else { return nil }

        var endpoint = URLComponents()
        endpoint.scheme = useTLS ? "https" : "http"
        endpoint.host = host
        endpoint.port = useTLS ? tlsPort : port
        endpoint.path = path
        return endpoint.url
    }
}
