import Contacts
import Foundation

/// Resolves a Messages sender address to a display name, using local `address = name` overrides first
/// and then Contacts. Caches hits and misses until `reset()`. Not thread-safe: use one serial queue.
final class ContactResolver {
    private let store: CNContactStore
    private var cache: [String: String] = [:]
    private var misses = Set<String>()
    /// Reports a short, privacy-safe description of the last lookup for diagnostics.
    var onSummary: ((String) -> Void)?

    init(store: CNContactStore = CNContactStore()) {
        self.store = store
    }

    var hasContactAccess: Bool {
        CNContactStore.authorizationStatus(for: .contacts) == .authorized
    }

    func reset() {
        cache.removeAll()
        misses.removeAll()
    }

    func name(for address: String, overrides: String) -> String? {
        guard !address.isEmpty else {
            onSummary?("sender address unavailable")
            return nil
        }

        let normalizedAddress = Self.normalizeAddress(address)
        for line in overrides.components(separatedBy: .newlines) {
            guard let separator = line.firstIndex(of: "=") else { continue }
            let key = Self.normalizeAddress(String(line[..<separator]).trimmingCharacters(in: .whitespacesAndNewlines))
            let override = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespacesAndNewlines)
            let sameSender = key == normalizedAddress || (!address.contains("@") && Self.phoneNumbersMatch(key, normalizedAddress))
            if !key.isEmpty, sameSender, !override.isEmpty {
                onSummary?("matched a local name override")
                return String(override.prefix(80))
            }
        }

        if let cached = cache[normalizedAddress] { return cached }
        if misses.contains(normalizedAddress) { return nil }

        guard hasContactAccess else {
            onSummary?("Contacts access off or restricted")
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

        if let matches = try? store.unifiedContacts(matching: predicate, keysToFetch: keys),
           let match = matches.first,
           let name = Self.displayName(for: match) {
            onSummary?("matched using Contacts")
            cache[normalizedAddress] = name
            return name
        }

        // Predicate phone matching can miss equivalent formats (+country code, punctuation, or a local
        // number). Compare normalized values only among contacts the user has allowed this app to see.
        let request = CNContactFetchRequest(keysToFetch: keys)
        var fallback: String?
        do {
            try store.enumerateContacts(with: request) { contact, stop in
                let contactMatches: Bool
                if isEmail {
                    contactMatches = contact.emailAddresses.contains { Self.normalizeAddress(String($0.value)) == normalizedAddress }
                } else {
                    contactMatches = contact.phoneNumbers.contains { Self.phoneNumbersMatch($0.value.stringValue, address) }
                }
                if contactMatches {
                    fallback = Self.displayName(for: contact)
                    stop.pointee = true
                }
            }
        } catch {
            onSummary?("contact lookup failed; check Contacts permission")
            return nil
        }
        if let fallback {
            onSummary?("matched after normalizing the sender address")
            cache[normalizedAddress] = fallback
        } else {
            onSummary?("no matching saved contact")
            misses.insert(normalizedAddress)
        }
        return fallback
    }

    static func normalizeAddress(_ address: String) -> String {
        var value = address.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if value.hasPrefix("mailto:") { value.removeFirst("mailto:".count) }
        if value.hasPrefix("tel:") { value.removeFirst("tel:".count) }
        return value.contains("@") ? value : String(value.filter(\.isNumber))
    }

    static func phoneNumbersMatch(_ lhs: String, _ rhs: String) -> Bool {
        let leftDigits = lhs.filter(\.isNumber)
        let rightDigits = rhs.filter(\.isNumber)
        guard !leftDigits.isEmpty, !rightDigits.isEmpty else { return false }
        if leftDigits == rightDigits { return true }
        return leftDigits.count >= 10 && rightDigits.count >= 10 && leftDigits.suffix(10) == rightDigits.suffix(10)
    }

    private static func displayName(for contact: CNContact) -> String? {
        let personalName = [contact.givenName, contact.familyName]
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .joined(separator: " ")
        let name = personalName.isEmpty ? contact.organizationName : personalName
        return name.isEmpty ? nil : String(name.prefix(80))
    }
}
