# Changelog

## Unreleased

### Changed

- **Message previews are off on a new install**, as the README always promised. An existing Mac keeps the choice it already has. A missing `previews` value in shared settings now means off on both sides.

### Fixed

- **Mac:** an alert whose sender or preview was too large for Windows (for example, a preview full of multi-part emoji) was rejected and retried every minute, holding up every alert behind it. The Mac now shortens the text to fit, at a whole character.
- **Mac:** after a Messages database read error, the database connection could stay open and leak instead of closing.

## 1.2.1 — November 2026

### Fixed

- **Windows:** a device that connected to the receiver and then went silent (for example, stalled in the TLS handshake) could tie up its connections and block the Mac's alerts. Silent connections now close after 3 seconds.
- **Windows Setup:** with **Open Settings after setup** off, an update left Bubblecast stopped until the next sign-in. Setup now always starts it, quietly in the notification area when Settings shouldn't open.
- **Mac:** after the Mac sent newer settings to Windows, the sharing line could say Windows doesn't share settings for up to a minute.

### Changed

- **Mac:** the notification settings are split into **What alerts show** and **When to stay quiet**, the same two groups as the **Mac** page on Windows. Success, warning, and error text in light mode is darker for readability.

### Under the hood

- Windows: one accept loop for HTTP and TLS, one shortcut writer shared with Setup, the adapter list moved into `NetworkAddresses`, Settings fonts disposed on close, and the command thread no longer wakes twice a second.
- Mac: the settings checks now run the app's Messages database queries (incoming rows, associated rows, read state, last activity) against a synthetic database, and contact-address matching.
- CI skips docs-only changes and cancels superseded runs.

## 1.2.0 — November 2026

### New

- **Skip messages you've already read.** Just before sending, the Mac checks whether the message is already marked read (on the Mac, or on your iPhone through iCloud) and drops it. This mostly catches messages that waited while Windows was unreachable. On by default.
- **Stay quiet while you're active in a chat.** No alert for a conversation you sent or read a message in during the last few minutes (2 by default, up to 60), on any of your devices. On by default.
- **Change the Mac's notification settings from Windows.** Settings on Windows has a new **Mac** page with the Mac's notification choices: sender names, previews, reaction rows, the old-message filter, and the two options above. They stay editable on the Mac too; the most recent change wins and reaches the other side within about a minute. Connection settings (address, token, TLS) stay on the Mac.

### Fixed

- Setup could fail with "Access is denied" when updating if the previous Setup was read-only when downloaded; it now clears the read-only flag on the files it replaces or removes.

### Compatibility

- Settings sharing needs 1.2 on both sides. A 1.2 Mac keeps working with a 1.1 PC (settings just stay on the Mac), and a 1.2 PC keeps working with a 1.1 Mac (its **Mac** page waits for the Mac to update).
- Protocol: a new signed `POST /settings` request, and signed `X-Relay-Settings` response headers. Notification requests are unchanged.

### Under the hood

- Checks: 51 on macOS, 61 on Windows.

## 1.1.0 — November 2026

### New

- **Pairing without typing addresses.** Windows announces itself on the local network; the Mac lists it under *Found on your network* with a **Use** button (addressed as `<PC name>.local`, so it keeps working if the PC's IP changes). Turn it off with **Let the Mac find this PC** in Advanced.
- **TLS on by default, pinned on first use.** With TLS on and no fingerprint, saving on the Mac learns the certificate Windows presents and fills in its fingerprint to compare; saving again pins it.
- **"Mac went quiet" notice** on Windows after 5 minutes without a check-in, and a notice when the Mac is back (on by default).
- **Recent notifications** submenu on the Windows tray icon (memory only).
- **Listen address** (Advanced): optionally accept connections on just one of the PC's IPv4 addresses. Listeners restart in place when it changes.
- Option for clicking a notification to open Remote Desktop to the Mac.
- The Mac app now runs on **macOS 13 Ventura and later**.

### Faster and safer

- The Mac reads new messages as soon as Messages writes them (file-change events, with a 5-second fallback poll) and keeps one read-only database connection open instead of reopening it every 3 seconds. Pinned-TLS requests reuse one session per fingerprint.
- The Windows pairing token is encrypted at rest with DPAPI; an existing plain-text token is upgraded on first run.
- Windows accepts only protocol v2; the unused v1 format is no longer accepted.
- Fixed Mac counters being published from a background thread.

### Under the hood

- Mac: the relay core is split into `DeliveryQueue`, `MessagesDatabase`, `RelayClient`, `ContactResolver`, `MessagesChangeMonitor`, and `WindowsDiscovery`; the self-checks now test the real delivery code instead of a copy.
- Windows: `RelayContext` is split into partial files, request verification is a tested `RequestVerifier`, and Setup shares the receiver's UI code (and is now DPI-aware).
- GitHub Actions builds and checks both apps on every push and attaches Setup, the portable app, and the Mac disk image to each release. Dependabot keeps the actions current.
- Checks: 36 on macOS, 51 on Windows.

### Tried and not shipped

- Real WinRT toast notifications: an unpackaged .NET Framework app can't create a toast notifier on current Windows without app packaging or the Windows App SDK, so Bubblecast keeps notification-area balloons, which Windows also lists in the notification center.

## 1.0.0 — November 2026

First public release of Bubblecast.

### Mac (menu-bar app)

- Reads the Messages database read-only and relays new incoming messages to Windows within about three seconds.
- Optional sender names from Contacts or `address = name` overrides; optional message previews (up to 180 characters); group chat names.
- Skips old, undated, and iCloud-synced rows; de-duplicates; retries with backoff and sends one count-only catch-up alert after Windows was unreachable.
- Pairing token in the Keychain; HMAC-SHA256 signed requests; optional TLS pinned to the Windows certificate's SHA-256 fingerprint.
- SwiftUI settings with status, pairing, notifications, and an Advanced section (name overrides, open at login, privacy-safe diagnostics); light and dark appearance.
- Universal (Intel and Apple Silicon) disk image built by GitHub Actions and attached to each release.

### Windows (tray app and Setup)

- Native notifications with sender, group name, and preview; quiet hours and muted senders; test notifications always shown.
- Verifies signature, timestamp (±120 s), nonce, and message ID; bounded request parsing, connection cap, optional paired-Mac IP allowlist; optional TLS listener with a per-user self-signed certificate.
- Settings window with pairing details up front, live pairing and receiver status, quiet hours, mutes, sign-in startup, and an Advanced section (Remote Desktop override, IP allowlist, token rotation, diagnostics); follows the Windows light/dark theme.
- Setup with Start menu, desktop, sign-in, and open-settings options; safe staged updates with Retry; uninstall that keeps your token and settings.
