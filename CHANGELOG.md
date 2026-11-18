# Changelog

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
