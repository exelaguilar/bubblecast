import Foundation

/// Notification settings that both the Mac and Windows can edit. The newest revision wins: the Mac
/// pushes its copy with `POST /settings`, and Windows returns its copy, signed, on every accepted
/// response. Connection settings (address, token, TLS) are never shared. See docs/PROTOCOL.md.
struct SharedSettings: Equatable {
    static let maximumEncodedLength = 512
    static let ageRange = 1...1440
    static let activeMinutesRange = 1...60

    /// Milliseconds since 1970 when these values were last edited; 0 means never shared.
    var revision: Int64
    var includeSenderNames: Bool
    var includeMessagePreviews: Bool
    var suppressOldMessages: Bool
    var maximumMessageAgeMinutes: Int
    var notifyForReactions: Bool
    var skipReadMessages: Bool
    var quietWhileActive: Bool
    var activeMinutes: Int

    static let defaults = SharedSettings(
        revision: 0,
        includeSenderNames: true,
        includeMessagePreviews: true,
        suppressOldMessages: true,
        maximumMessageAgeMinutes: 10,
        notifyForReactions: false,
        skipReadMessages: true,
        quietWhileActive: true,
        activeMinutes: 2
    )

    /// `key=value` pairs joined by `;`, in a fixed order. ASCII only, so it fits in an HTTP header.
    func encoded() -> String {
        [
            "rev=\(revision)",
            "names=\(flag(includeSenderNames))",
            "previews=\(flag(includeMessagePreviews))",
            "old=\(flag(suppressOldMessages))",
            "age=\(Self.clamp(maximumMessageAgeMinutes, Self.ageRange))",
            "reactions=\(flag(notifyForReactions))",
            "read=\(flag(skipReadMessages))",
            "active=\(flag(quietWhileActive))",
            "activemin=\(Self.clamp(activeMinutes, Self.activeMinutesRange))"
        ].joined(separator: ";")
    }

    /// Parses `encoded()` output. Unknown keys are ignored and missing keys keep their defaults, so
    /// either side can add a setting later. Returns nil for malformed text or a missing revision.
    static func parse(_ text: String) -> SharedSettings? {
        guard !text.isEmpty, text.utf8.count <= maximumEncodedLength,
              text.unicodeScalars.allSatisfy({ $0.isASCII && $0.value >= 0x20 && $0.value < 0x7F }) else { return nil }
        var result = SharedSettings.defaults
        var sawRevision = false
        for pair in text.split(separator: ";", omittingEmptySubsequences: true) {
            let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2, let value = Int64(parts[1]), value >= 0 else { return nil }
            switch parts[0] {
            case "rev":
                result.revision = value
                sawRevision = true
            case "names": result.includeSenderNames = value != 0
            case "previews": result.includeMessagePreviews = value != 0
            case "old": result.suppressOldMessages = value != 0
            case "age": result.maximumMessageAgeMinutes = clamp(Int(clamping: value), ageRange)
            case "reactions": result.notifyForReactions = value != 0
            case "read": result.skipReadMessages = value != 0
            case "active": result.quietWhileActive = value != 0
            case "activemin": result.activeMinutes = clamp(Int(clamping: value), activeMinutesRange)
            default: break
            }
        }
        return sawRevision ? result : nil
    }

    /// The same values ignoring the revision, for deciding whether anything actually changed.
    func sameValues(as other: SharedSettings) -> Bool {
        var left = self
        var right = other
        left.revision = 0
        right.revision = 0
        return left == right
    }

    /// Signature Windows puts on the settings it returns, bound to the request's nonce so an old
    /// response can't be replayed.
    static func responseSignature(token: String, nonce: String, encoded: String) -> String {
        RelayHMAC.signature(token: token, canonical: "v2-settings\n\(nonce)\n\(encoded)")
    }

    static func now() -> Int64 { Int64((Date().timeIntervalSince1970 * 1000).rounded()) }

    private func flag(_ value: Bool) -> Int { value ? 1 : 0 }

    private static func clamp(_ value: Int, _ range: ClosedRange<Int>) -> Int {
        min(range.upperBound, max(range.lowerBound, value))
    }
}

extension SharedSettings {
    /// Decides whether a message should be dropped instead of notified. `attention` is nil when the
    /// database couldn't be read, in which case the message is always sent.
    func shouldSkip(_ attention: MessageAttention?, now: Date = Date()) -> Bool {
        guard let attention else { return false }
        if skipReadMessages && attention.isRead { return true }
        if quietWhileActive, let last = attention.lastActivity {
            let elapsed = now.timeIntervalSince(last)
            // A little tolerance for clock differences between the Mac and the device that synced.
            if elapsed <= TimeInterval(activeMinutes * 60) && elapsed >= -300 { return true }
        }
        return false
    }
}

/// Whether the user has already seen a message, and when they last acted in its conversation.
struct MessageAttention: Equatable {
    /// The message itself is marked read (on this Mac or, through iCloud, another device).
    let isRead: Bool
    /// The latest message the user sent, or message they read, in the same conversation.
    let lastActivity: Date?
}
