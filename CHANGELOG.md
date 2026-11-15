# Changelog

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
