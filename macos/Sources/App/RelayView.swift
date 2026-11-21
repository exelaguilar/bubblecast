import AppKit
import SwiftUI

// MARK: - Design tokens

/// Neutral light/dark palette modeled on shadcn/ui's zinc theme. Every color
/// resolves against the current NSAppearance, so the window follows the
/// system light/dark setting without a restart.
private enum RelayTheme {
    static let page = dynamic(light: 0xFAFAFA, dark: 0x09090B)
    static let card = dynamic(light: 0xFFFFFF, dark: 0x111113)
    static let muted = dynamic(light: 0xF4F4F5, dark: 0x1C1C1F)
    static let border = dynamic(light: 0xE4E4E7, dark: 0x27272A)
    static let input = dynamic(light: 0xD4D4D8, dark: 0x3F3F46)
    static let ring = dynamic(light: 0xA1A1AA, dark: 0x71717A)
    static let foreground = dynamic(light: 0x09090B, dark: 0xFAFAFA)
    static let mutedForeground = dynamic(light: 0x71717A, dark: 0xA1A1AA)
    static let primary = dynamic(light: 0x18181B, dark: 0xFAFAFA)
    static let primaryForeground = dynamic(light: 0xFAFAFA, dark: 0x18181B)
    static let success = dynamic(light: 0x16A34A, dark: 0x4ADE80)
    static let warning = dynamic(light: 0xD97706, dark: 0xFBBF24)
    static let destructive = dynamic(light: 0xDC2626, dark: 0xF87171)

    static let cardRadius: CGFloat = 10
    static let controlRadius: CGFloat = 6

    private static func dynamic(light: UInt32, dark: UInt32) -> Color {
        let lightColor = rgb(light)
        let darkColor = rgb(dark)
        return Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? darkColor : lightColor
        })
    }

    private static func rgb(_ hex: UInt32) -> NSColor {
        NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255,
                alpha: 1)
    }
}

private extension RelayStatusLevel {
    var tint: Color {
        switch self {
        case .ok: return RelayTheme.success
        case .info: return RelayTheme.mutedForeground
        case .warning: return RelayTheme.warning
        case .error: return RelayTheme.destructive
        }
    }

    var isProblem: Bool {
        switch self {
        case .warning, .error: return true
        case .ok, .info: return false
        }
    }
}

// MARK: - Settings view

struct RelayView: View {
    @ObservedObject var model: RelayModel
    @State private var advancedExpanded = false
    @FocusState private var focusedField: Field?

    private enum Field: Hashable {
        case address, token, fingerprint, overrides
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Hairline()
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    statusCard
                    pairingCard
                    notificationsCard
                    advancedCard
                }
                .padding(16)
            }
            Hairline()
            footer
        }
        .frame(width: 460, height: 680)
        .background(RelayTheme.page)
        .foregroundStyle(RelayTheme.foreground)
    }

    // MARK: Header and footer

    private var header: some View {
        HStack(spacing: 12) {
            // The Bubblecast app icon (macos/Resources/AppIcon-1024.png). Its artwork includes
            // Apple's icon-grid margin, so the frame is a little larger than the visible tile.
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 42, height: 42)
                .padding(-4)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 1) {
                Text("Bubblecast")
                    .font(.system(size: 15, weight: .semibold))
                Text("Mac → Windows notifications")
                    .font(.system(size: 12))
                    .foregroundStyle(RelayTheme.mutedForeground)
            }

            Spacer(minLength: 8)

            StatusBadge(text: connectionBadgeText, level: model.windowsHealthLevel)
                .accessibilityLabel("Windows connection: \(connectionBadgeText)")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
    }

    private var footer: some View {
        HStack(spacing: 8) {
            Button("Quit") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
                .buttonStyle(RelayButtonStyle(kind: .ghost))

            Spacer(minLength: 4)

            if model.hasUnsavedChanges {
                HStack(spacing: 6) {
                    Circle().fill(RelayTheme.warning).frame(width: 6, height: 6)
                    Text("Unsaved changes")
                        .font(.system(size: 12))
                        .foregroundStyle(RelayTheme.mutedForeground)
                        .lineLimit(1)
                }
                .accessibilityElement(children: .combine)
            }

            Button("Send test") { model.sendTest() }
                .buttonStyle(RelayButtonStyle(kind: .secondary))
                .help("Saves settings, then asks Windows to show a test notification")

            Button("Save") { model.saveSettings() }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(RelayButtonStyle(kind: .primary))
                .accessibilityLabel("Save settings")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(RelayTheme.card)
    }

    // MARK: Sections

    private var statusCard: some View {
        SectionCard("Status", systemImage: "waveform.path.ecg") {
            VStack(alignment: .leading, spacing: 8) {
                StatusCallout(title: "Windows",
                              message: model.windowsHealth,
                              level: model.windowsHealthLevel,
                              symbol: healthSymbol)
                    .accessibilityLabel("Windows health: \(model.windowsHealth)")
                StatusCallout(title: "Relay",
                              message: model.status,
                              level: model.statusLevel,
                              symbol: statusSymbol)
                    .accessibilityLabel("Relay status: \(model.status)")
                HStack {
                    Text("Last successful alert")
                        .foregroundStyle(RelayTheme.mutedForeground)
                    Spacer()
                    Text(model.lastSuccessfulNotification.map { $0.formatted(date: .abbreviated, time: .shortened) } ?? "Never")
                        .monospacedDigit()
                }
                .font(.system(size: 12))
                .padding(.top, 2)
                .accessibilityElement(children: .combine)
            }
        }
    }

    private var pairingCard: some View {
        SectionCard("Pairing", subtitle: "Copy these values from Settings on the Windows PC.", systemImage: "network") {
            VStack(alignment: .leading, spacing: 14) {
                FormField(label: "Windows PC address",
                          help: addressHelp,
                          error: addressErrorText) {
                    TextField("e.g. windows-pc.local or 192.168.1.20", text: $model.windowsAddress)
                        .focused($focusedField, equals: .address)
                        .modifier(InputChrome(isFocused: focusedField == .address, isInvalid: addressErrorText != nil))
                        .accessibilityLabel("Windows PC address")
                }

                if !model.discoveredPCs.isEmpty {
                    discoveredPCsList
                }

                FormField(label: "Pairing token",
                          help: "64-character token shown on Windows. Stored in the Mac Keychain.") {
                    SecureField("Paste the pairing token", text: $model.pairingToken)
                        .focused($focusedField, equals: .token)
                        .modifier(InputChrome(isFocused: focusedField == .token))
                        .accessibilityLabel("Pairing token")
                }

                Hairline()

                SwitchRow(title: "Use pinned TLS",
                          detail: "Encrypts alerts on TCP port 8766. HTTP on port 8765 remains available when this is off.",
                          isOn: $model.useTLS)

                FormField(label: "Windows certificate fingerprint (SHA-256)",
                          help: "Leave it blank and click Save to learn it from Windows, then check it matches the fingerprint Windows shows. You can also paste it.") {
                    TextField("64 hexadecimal characters", text: $model.tlsCertificateFingerprint)
                        .font(.system(size: 12, design: .monospaced))
                        .focused($focusedField, equals: .fingerprint)
                        .modifier(InputChrome(isFocused: focusedField == .fingerprint, isDisabled: !model.useTLS, monospaced: true))
                        .disabled(!model.useTLS)
                        .accessibilityLabel("Windows TLS SHA-256 certificate fingerprint")
                }
                .opacity(model.useTLS ? 1 : 0.6)

                Label {
                    Text("Signed requests authenticate the relay. Attachments are never sent.")
                } icon: {
                    Image(systemName: "lock.fill")
                }
                .font(.system(size: 11))
                .foregroundStyle(RelayTheme.mutedForeground)
            }
        }
    }

    private var notificationsCard: some View {
        SectionCard("Notifications", subtitle: "What the Windows alert shows. You can also change these in Bubblecast on Windows.", systemImage: "bell.badge") {
            VStack(alignment: .leading, spacing: 12) {
                SwitchRow(title: "Sender contact name",
                          detail: "Look up the sender in Contacts.",
                          isOn: $model.includeSenderNames)
                Hairline()
                SwitchRow(title: "Message previews",
                          detail: "Include message text. Previews may appear in Windows notification history.",
                          isOn: $model.includeMessagePreviews)
                Hairline()
                SwitchRow(title: "Ignore old or iCloud-synced messages",
                          detail: "Older or unreadable timestamps are skipped. Turn this off only to catch up on the full backlog.",
                          isOn: $model.suppressOldMessages)
                minutesRow(label: "Maximum message age", value: $model.maximumMessageAgeMinutes, range: 1...1440,
                           accessibility: "Maximum message age: \(model.maximumMessageAgeMinutes) minutes")
                    .disabled(!model.suppressOldMessages)
                    .opacity(model.suppressOldMessages ? 1 : 0.55)
                Hairline()
                SwitchRow(title: "Include associated-message rows",
                          detail: "May include reactions. Apple doesn't document these types; rows without text get a generic label.",
                          isOn: $model.notifyForReactions)
                Hairline()
                SwitchRow(title: "Skip messages I've already read",
                          detail: "Checked just before sending, so it mostly catches messages that waited while Windows was unreachable. A conversation left open in Messages on this Mac counts as read.",
                          isOn: $model.skipReadMessages)
                Hairline()
                SwitchRow(title: "Stay quiet while I'm active in a chat",
                          detail: "No alert for a conversation you sent or read a message in recently, on any of your devices.",
                          isOn: $model.quietWhileActive)
                minutesRow(label: "Quiet for", value: $model.activeMinutes, range: 1...60,
                           accessibility: "Stay quiet for \(model.activeMinutes) minutes after activity")
                    .disabled(!model.quietWhileActive)
                    .opacity(model.quietWhileActive ? 1 : 0.55)
                Label {
                    Text(model.sharingStatus)
                } icon: {
                    Image(systemName: "arrow.triangle.2.circlepath")
                }
                .font(.system(size: 11))
                .foregroundStyle(RelayTheme.mutedForeground)
                .accessibilityLabel("Settings sharing: \(model.sharingStatus)")
            }
        }
    }

    /// A compact "label … N min [stepper]" row on a muted background.
    private func minutesRow(label: String, value: Binding<Int>, range: ClosedRange<Int>, accessibility: String) -> some View {
        HStack(spacing: 8) {
            Text(label)
                .font(.system(size: 12))
                .foregroundStyle(RelayTheme.mutedForeground)
            Spacer()
            Text("\(value.wrappedValue) min")
                .font(.system(size: 12, weight: .medium))
                .monospacedDigit()
            Stepper(accessibility, value: value, in: range)
                .labelsHidden()
                .controlSize(.small)
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 10)
        .background(RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous).fill(RelayTheme.muted))
    }

    private var advancedCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button {
                withAnimation(.easeOut(duration: 0.15)) { advancedExpanded.toggle() }
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: "slider.horizontal.3")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(RelayTheme.mutedForeground)
                        .frame(width: 16)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Advanced")
                            .font(.system(size: 13, weight: .semibold))
                        Text("Name overrides, login item, and diagnostics")
                            .font(.system(size: 11))
                            .foregroundStyle(RelayTheme.mutedForeground)
                    }
                    Spacer()
                    Image(systemName: "chevron.right")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(RelayTheme.mutedForeground)
                        .rotationEffect(.degrees(advancedExpanded ? 90 : 0))
                }
                .padding(16)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Advanced")
            .accessibilityValue(advancedExpanded ? "Expanded" : "Collapsed")

            if advancedExpanded {
                Hairline()
                VStack(alignment: .leading, spacing: 14) {
                    FormField(label: "Sender name overrides",
                              help: "One per line: address = name. Used before Contacts.") {
                        TextEditor(text: $model.senderNameOverrides)
                            .font(.system(size: 12, design: .monospaced))
                            .scrollContentBackground(.hidden)
                            .focused($focusedField, equals: .overrides)
                            .frame(height: 76)
                            .modifier(InputChrome(isFocused: focusedField == .overrides, verticalPadding: 4))
                            .accessibilityLabel("Sender name overrides")
                    }

                    Hairline()

                    SwitchRow(title: "Open at login",
                              detail: model.loginItemStatus,
                              isOn: Binding(
                                get: { model.openAtLogin },
                                set: { model.setOpenAtLogin($0) }
                              ))
                    if model.loginItemStatus.contains("Approval needed") {
                        Button("Open Login Items settings…") { model.openLoginItemsSettings() }
                            .buttonStyle(RelayButtonStyle(kind: .secondary))
                    }
                    Text("A headless VM also needs automatic login, or a manual login after reboot, because Messages and privacy permissions belong to the user session.")
                        .font(.system(size: 11))
                        .foregroundStyle(RelayTheme.mutedForeground)
                        .fixedSize(horizontal: false, vertical: true)

                    Hairline()

                    VStack(alignment: .leading, spacing: 8) {
                        Text("Diagnostics")
                            .font(.system(size: 12, weight: .medium))
                        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 5) {
                            diagnosticRow("Suppressed old/undated", "\(model.suppressedOldMessageCount)")
                            diagnosticRow("Duplicates", "\(model.duplicateMessageCount)")
                            diagnosticRow("Skipped as seen", "\(model.skippedSeenCount)")
                            diagnosticRow("Catch-up rows", "\(model.catchUpSummaryCount)")
                            diagnosticRow("Cursor re-baselines", "\(model.cursorRebaselineCount)")
                            diagnosticRow("Timestamp units", model.timestampUnitSummary)
                            diagnosticRow("Contact lookup", model.contactLookupSummary)
                        }
                        HStack(spacing: 8) {
                            Button("Copy privacy-safe diagnostics") { model.copyDiagnostics() }
                                .buttonStyle(RelayButtonStyle(kind: .secondary))
                            Button("Reset counters") { model.resetCounters() }
                                .buttonStyle(RelayButtonStyle(kind: .ghost))
                        }
                        .padding(.top, 4)
                    }
                }
                .padding(16)
            }
        }
        .cardChrome()
    }

    /// Bubblecast receivers announcing on the local network, each with a one-click "Use".
    private var discoveredPCsList: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Found on your network")
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(RelayTheme.mutedForeground)
            ForEach(model.discoveredPCs, id: \.self) { name in
                HStack(spacing: 8) {
                    Image(systemName: "desktopcomputer")
                        .foregroundStyle(RelayTheme.mutedForeground)
                        .accessibilityHidden(true)
                    Text(name)
                        .font(.system(size: 12, weight: .medium))
                        .lineLimit(1)
                    Spacer(minLength: 8)
                    if model.windowsAddress == WindowsDiscovery.address(forName: name) {
                        Label("Selected", systemImage: "checkmark")
                            .font(.system(size: 11, weight: .medium))
                            .foregroundStyle(RelayTheme.success)
                    } else {
                        Button("Use") { model.useDiscoveredPC(name) }
                            .buttonStyle(RelayButtonStyle(kind: .secondary))
                            .accessibilityLabel("Use \(name)")
                    }
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous).fill(RelayTheme.muted))
            }
        }
    }

    /// Returns the GridRow itself (not a wrapper view) so Grid aligns its columns.
    private func diagnosticRow(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label)
                .font(.system(size: 11))
                .foregroundStyle(RelayTheme.mutedForeground)
                .gridColumnAlignment(.leading)
            Text(value)
                .font(.system(size: 11))
                .monospacedDigit()
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
        }
    }

    // MARK: Derived text

    private var connectionBadgeText: String {
        switch model.windowsHealthLevel {
        case .ok: return "Paired"
        case .info: return "Not verified"
        case .warning: return "Needs attention"
        case .error: return "Unreachable"
        }
    }

    /// The model reports an empty address as guidance, not as an error.
    private var addressErrorText: String? {
        guard let addressError = model.addressError, !model.windowsAddress.isEmpty else { return nil }
        return addressError
    }

    private var addressHelp: String {
        if let addressError = model.addressError, model.windowsAddress.isEmpty { return addressError }
        return "Host name, IPv4 address, or bracketed IPv6 address."
    }

    private var statusSymbol: String {
        switch model.statusLevel {
        case .ok: return "checkmark.circle.fill"
        case .info: return "info.circle"
        case .warning: return "exclamationmark.triangle.fill"
        case .error: return "xmark.octagon.fill"
        }
    }

    private var healthSymbol: String {
        switch model.windowsHealthLevel {
        case .ok: return "checkmark.circle.fill"
        case .info: return "network"
        case .warning: return "exclamationmark.triangle.fill"
        case .error: return "xmark.octagon.fill"
        }
    }
}

// MARK: - Components

private struct Hairline: View {
    var body: some View {
        Rectangle()
            .fill(RelayTheme.border)
            .frame(height: 1)
            .accessibilityHidden(true)
    }
}

private struct CardChrome: ViewModifier {
    func body(content: Content) -> some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: RelayTheme.cardRadius, style: .continuous)
                    .fill(RelayTheme.card)
            )
            .overlay(
                RoundedRectangle(cornerRadius: RelayTheme.cardRadius, style: .continuous)
                    .strokeBorder(RelayTheme.border, lineWidth: 1)
            )
    }
}

private extension View {
    func cardChrome() -> some View { modifier(CardChrome()) }
}

private struct SectionCard<Content: View>: View {
    let title: String
    let subtitle: String?
    let systemImage: String
    let content: Content

    init(_ title: String, subtitle: String? = nil, systemImage: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.subtitle = subtitle
        self.systemImage = systemImage
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: systemImage)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(RelayTheme.mutedForeground)
                    .frame(width: 16)
                    .padding(.top, 1)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.system(size: 13, weight: .semibold))
                        .accessibilityAddTraits(.isHeader)
                    if let subtitle {
                        Text(subtitle)
                            .font(.system(size: 11))
                            .foregroundStyle(RelayTheme.mutedForeground)
                    }
                }
            }
            content
        }
        .padding(16)
        .cardChrome()
    }
}

private struct FormField<Content: View>: View {
    let label: String
    let help: String?
    let error: String?
    let content: Content

    init(label: String, help: String? = nil, error: String? = nil, @ViewBuilder content: () -> Content) {
        self.label = label
        self.help = help
        self.error = error
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label)
                .font(.system(size: 12, weight: .medium))
            content
            if let error {
                Label(error, systemImage: "exclamationmark.circle.fill")
                    .font(.system(size: 11))
                    .foregroundStyle(RelayTheme.destructive)
                    .fixedSize(horizontal: false, vertical: true)
            } else if let help {
                Text(help)
                    .font(.system(size: 11))
                    .foregroundStyle(RelayTheme.mutedForeground)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

private struct InputChrome: ViewModifier {
    var isFocused: Bool
    var isInvalid: Bool = false
    var isDisabled: Bool = false
    var monospaced: Bool = false
    var verticalPadding: CGFloat = 7

    func body(content: Content) -> some View {
        content
            .textFieldStyle(.plain)
            .font(monospaced ? .system(size: 12, design: .monospaced) : .system(size: 13))
            .padding(.horizontal, 10)
            .padding(.vertical, verticalPadding)
            .background(
                RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous)
                    .fill(isDisabled ? RelayTheme.muted : RelayTheme.card)
            )
            .overlay(
                RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous)
                    .strokeBorder(borderColor, lineWidth: 1)
            )
            .overlay(
                RoundedRectangle(cornerRadius: RelayTheme.controlRadius + 2, style: .continuous)
                    .stroke(ringColor, lineWidth: 3)
                    .padding(-2)
                    .opacity(isFocused ? 0.35 : 0)
            )
            .animation(.easeOut(duration: 0.12), value: isFocused)
    }

    private var borderColor: Color {
        if isInvalid { return RelayTheme.destructive }
        return isFocused ? RelayTheme.ring : RelayTheme.input
    }

    private var ringColor: Color {
        isInvalid ? RelayTheme.destructive : RelayTheme.ring
    }
}

private struct SwitchRow: View {
    let title: String
    let detail: String?
    @Binding var isOn: Bool

    var body: some View {
        Toggle(isOn: $isOn) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(RelayTheme.foreground)
                if let detail {
                    Text(detail)
                        .font(.system(size: 11))
                        .foregroundStyle(RelayTheme.mutedForeground)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .toggleStyle(.switch)
        .controlSize(.small)
    }
}

private struct StatusBadge: View {
    let text: String
    let level: RelayStatusLevel

    var body: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(level.tint)
                .frame(width: 6, height: 6)
            Text(text)
                .font(.system(size: 11, weight: .medium))
                .lineLimit(1)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 4)
        .background(Capsule().fill(level == .info ? RelayTheme.muted : level.tint.opacity(0.12)))
        .overlay(Capsule().strokeBorder(level == .info ? RelayTheme.border : level.tint.opacity(0.35), lineWidth: 1))
    }
}

private struct StatusCallout: View {
    let title: String
    let message: String
    let level: RelayStatusLevel
    let symbol: String

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(level.tint)
                .frame(width: 16)
                .padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(RelayTheme.mutedForeground)
                Text(message)
                    .font(.system(size: 12))
                    .foregroundStyle(level.isProblem ? level.tint : RelayTheme.foreground)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(level.isProblem ? level.tint.opacity(0.08) : RelayTheme.muted)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(level.isProblem ? level.tint.opacity(0.35) : Color.clear, lineWidth: 1)
        )
        .accessibilityElement(children: .combine)
    }
}

private struct RelayButtonStyle: ButtonStyle {
    enum Kind {
        case primary
        case secondary
        case ghost
    }

    let kind: Kind

    func makeBody(configuration: Configuration) -> some View {
        RelayButtonBody(configuration: configuration, kind: kind)
    }
}

private struct RelayButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let kind: RelayButtonStyle.Kind
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovering = false

    var body: some View {
        configuration.label
            .font(.system(size: 12, weight: .medium))
            .lineLimit(1)
            .padding(.horizontal, kind == .ghost ? 10 : 14)
            .frame(height: 28)
            .foregroundStyle(kind == .primary ? RelayTheme.primaryForeground : RelayTheme.foreground)
            .background(
                RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous)
                    .fill(background)
            )
            .overlay(
                RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous)
                    .strokeBorder(kind == .secondary ? RelayTheme.input : Color.clear, lineWidth: 1)
            )
            .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.5)
            .contentShape(RoundedRectangle(cornerRadius: RelayTheme.controlRadius, style: .continuous))
            .onHover { isHovering = $0 }
    }

    private var background: Color {
        switch kind {
        case .primary: return RelayTheme.primary.opacity(isHovering ? 0.88 : 1)
        case .secondary: return isHovering ? RelayTheme.muted : RelayTheme.card
        case .ghost: return isHovering ? RelayTheme.muted : Color.clear
        }
    }
}
