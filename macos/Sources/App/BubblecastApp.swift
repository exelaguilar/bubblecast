import AppKit
import SwiftUI

@main
struct BubblecastApp: App {
    @NSApplicationDelegateAdaptor(RelayAppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra("Bubblecast", systemImage: "message.badge") {
            RelayView(model: RelayModel.shared)
        }
        .menuBarExtraStyle(.window)
    }
}

final class RelayAppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        RelayModel.shared.start()
    }
}
