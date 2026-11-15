import Foundation

enum TimestampUnit: String, CaseIterable, Equatable {
    case appleSeconds = "Apple seconds"
    case appleMilliseconds = "Apple milliseconds"
    case appleMicroseconds = "Apple microseconds"
    case appleNanoseconds = "Apple nanoseconds"
}

struct DecodedMessageTimestamp: Equatable {
    let date: Date
    let unit: TimestampUnit
}

enum TimestampDecoder {
    private static let appleEpoch = 978_307_200.0

    static func decode(_ raw: Double, now: Date = Date()) -> DecodedMessageTimestamp? {
        guard raw.isFinite, raw > 0 else { return nil }

        let unit: TimestampUnit
        let secondsSinceAppleEpoch: Double
        if raw >= 1e17 {
            unit = .appleNanoseconds
            secondsSinceAppleEpoch = raw / 1e9
        } else if raw >= 1e14 {
            unit = .appleMicroseconds
            secondsSinceAppleEpoch = raw / 1e6
        } else if raw >= 1e11 {
            unit = .appleMilliseconds
            secondsSinceAppleEpoch = raw / 1e3
        } else {
            unit = .appleSeconds
            secondsSinceAppleEpoch = raw
        }

        let timestamp = secondsSinceAppleEpoch + appleEpoch
        let minimum = 946_684_800.0 // 2000-01-01, a fail-closed lower bound.
        let maximum = now.timeIntervalSince1970 + 86_400
        guard timestamp >= minimum, timestamp <= maximum else { return nil }
        return DecodedMessageTimestamp(date: Date(timeIntervalSince1970: timestamp), unit: unit)
    }
}
