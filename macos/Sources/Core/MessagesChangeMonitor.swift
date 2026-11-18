import Foundation

/// Calls `onChange` (debounced) when the Messages database or its write-ahead log changes, so new
/// messages are read right away instead of waiting for the next poll. The relay still polls on a slower
/// timer as a fallback. Needs the same Full Disk Access the database read needs.
final class MessagesChangeMonitor {
    private let databasePath: String
    private let queue: DispatchQueue
    private let onChange: () -> Void
    private var sources: [DispatchSourceFileSystemObject] = []
    private var firePending = false
    private var rearmPending = false
    private var running = false

    init(databasePath: String, queue: DispatchQueue, onChange: @escaping () -> Void) {
        self.databasePath = databasePath
        self.queue = queue
        self.onChange = onChange
    }

    deinit {
        cancelSources()
    }

    /// Starts watching. Call on `queue`.
    func start() {
        running = true
        arm()
    }

    /// Stops watching. Call on `queue`.
    func stop() {
        running = false
        cancelSources()
    }

    private func arm() {
        cancelSources()
        let directory = (databasePath as NSString).deletingLastPathComponent
        // The directory source notices the WAL file being created, deleted, or replaced.
        for path in [databasePath, databasePath + "-wal", directory] {
            let descriptor = open(path, O_EVTONLY)
            guard descriptor >= 0 else { continue }
            let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: descriptor,
                                                                   eventMask: [.write, .extend, .delete, .rename, .link],
                                                                   queue: queue)
            source.setEventHandler { [weak self, weak source] in
                guard let self, let source else { return }
                let events = source.data
                if events.contains(.delete) || events.contains(.rename) || path == directory {
                    self.scheduleRearm()
                }
                self.scheduleFire()
            }
            source.setCancelHandler { close(descriptor) }
            source.resume()
            sources.append(source)
        }
    }

    private func cancelSources() {
        for source in sources { source.cancel() }
        sources.removeAll()
    }

    private func scheduleFire() {
        guard !firePending else { return }
        firePending = true
        queue.asyncAfter(deadline: .now() + .milliseconds(250)) { [weak self] in
            guard let self else { return }
            self.firePending = false
            if self.running { self.onChange() }
        }
    }

    private func scheduleRearm() {
        guard !rearmPending else { return }
        rearmPending = true
        queue.asyncAfter(deadline: .now() + .milliseconds(500)) { [weak self] in
            guard let self else { return }
            self.rearmPending = false
            if self.running { self.arm() }
        }
    }
}
