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
    static let useTLS = "useTLS"
    static let tlsCertificateFingerprint = "tlsCertificateFingerprint"
    static let lastDeliveredIncomingMessageRowID = "lastDeliveredIncomingMessageRowID"
    static let recentlyProcessedMessageGUIDs = "recentlyProcessedMessageGUIDs"
    static let suppressedOldMessageCount = "suppressedOldMessageCount"
    static let duplicateMessageCount = "duplicateMessageCount"
    static let catchUpSummaryCount = "catchUpSummaryCount"
    static let cursorRebaselineCount = "cursorRebaselineCount"
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
    let useTLS: Bool
    let tlsCertificateFingerprint: String

    static func load(from defaults: UserDefaults = .standard, tokenStore: PairingTokenStore = KeychainTokenStore()) -> RelaySettings {
        // The token lives only in the Keychain. An unreadable item loads as empty; saving writes a fresh one.
        let pairingToken = ((try? tokenStore.readToken()) ?? nil) ?? ""
        return RelaySettings(
            windowsAddress: defaults.string(forKey: RelayPreferences.windowsAddress) ?? "",
            pairingToken: pairingToken,
            includeSenderNames: defaults.object(forKey: RelayPreferences.includeSenderNames) as? Bool ?? true,
            includeMessagePreviews: defaults.object(forKey: RelayPreferences.includeMessagePreviews) as? Bool ?? true,
            suppressOldMessages: defaults.object(forKey: RelayPreferences.suppressOldMessages) as? Bool ?? true,
            maximumMessageAgeMinutes: max(1, min(1440, defaults.object(forKey: RelayPreferences.maximumMessageAgeMinutes) as? Int ?? 10)),
            senderNameOverrides: defaults.string(forKey: RelayPreferences.senderNameOverrides) ?? "",
            notifyForReactions: defaults.object(forKey: RelayPreferences.notifyForReactions) as? Bool ?? false,
            useTLS: defaults.object(forKey: RelayPreferences.useTLS) as? Bool ?? true,
            tlsCertificateFingerprint: defaults.string(forKey: RelayPreferences.tlsCertificateFingerprint) ?? ""
        )
    }

    func save(to defaults: UserDefaults = .standard, tokenStore: PairingTokenStore = KeychainTokenStore()) throws {
        try tokenStore.writeToken(pairingToken)
        defaults.set(windowsAddress, forKey: RelayPreferences.windowsAddress)
        defaults.set(includeSenderNames, forKey: RelayPreferences.includeSenderNames)
        defaults.set(includeMessagePreviews, forKey: RelayPreferences.includeMessagePreviews)
        defaults.set(suppressOldMessages, forKey: RelayPreferences.suppressOldMessages)
        defaults.set(maximumMessageAgeMinutes, forKey: RelayPreferences.maximumMessageAgeMinutes)
        defaults.set(senderNameOverrides, forKey: RelayPreferences.senderNameOverrides)
        defaults.set(notifyForReactions, forKey: RelayPreferences.notifyForReactions)
        defaults.set(useTLS, forKey: RelayPreferences.useTLS)
        defaults.set(tlsCertificateFingerprint, forKey: RelayPreferences.tlsCertificateFingerprint)
    }
}

enum WindowsRelayEndpoint {
    static let port = 8765

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
        endpoint.port = useTLS ? 8766 : port
        endpoint.path = path
        return endpoint.url
    }
}
