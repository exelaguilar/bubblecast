import Foundation
import Network

/// Finds Bubblecast receivers that announce themselves on the local network as `_bubblecast._tcp`.
/// Reports PC names; the relay addresses a PC as `<name>.local`, which keeps working if its IP changes.
/// Needs Local Network permission (macOS asks the first time).
final class WindowsDiscovery {
    static let serviceType = "_bubblecast._tcp"

    /// Called on the main queue with the sorted names of PCs currently announcing.
    var onChange: (([String]) -> Void)?
    private let queue = DispatchQueue(label: "local.bubblecast.discovery", qos: .utility)
    private var browser: NWBrowser?

    func start() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: Self.serviceType, domain: "local."), using: .tcp)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            let names = Set(results.compactMap { result -> String? in
                if case let .service(name, _, _, _) = result.endpoint { return name }
                return nil
            })
            let sorted = names.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
            DispatchQueue.main.async { self?.onChange?(sorted) }
        }
        browser.stateUpdateHandler = { [weak self] state in
            if case .failed = state {
                // Restart after a network change or a transient failure (start/stop run on main).
                DispatchQueue.main.asyncAfter(deadline: .now() + 10) {
                    self?.stop()
                    self?.start()
                }
            }
        }
        self.browser = browser
        browser.start(queue: queue)
    }

    func stop() {
        browser?.cancel()
        browser = nil
    }

    /// The relay address for a discovered PC name.
    static func address(forName name: String) -> String {
        name.hasSuffix(".local") ? name : "\(name).local"
    }
}
