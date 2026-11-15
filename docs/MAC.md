# Bubblecast for Mac — full guide

Bubblecast for Mac is a SwiftUI menu-bar app for macOS 15 Sequoia on Intel (a Mac or a macOS VM). It reads the Messages database read-only, filters out old and duplicate rows, optionally resolves contact names, and sends signed notifications to Bubblecast for Windows on your LAN.

## Build and install

Clone the repository on the Mac (or copy the `macos/` folder and the root `VERSION` file side by side), then run from the repository root:

```sh
bash macos/build.command
```

The script builds the icon set from `macos/Resources/AppIcon-1024.png`, compiles the app for Intel macOS 15, signs it ad hoc, asks a running copy to quit, installs it to `~/Applications/Bubblecast.app`, and opens it. It needs the Xcode Command Line Tools. Terminal is only needed for building.

To sign with a stable identity instead of ad hoc, set `RELAY_SIGN_IDENTITY` to the name of a signing identity in your Keychain before building. A new identity may require granting Full Disk Access and Contacts again.

## Permissions

- **Full Disk Access** (required): **System Settings → Privacy & Security → Full Disk Access**, then add `~/Applications/Bubblecast.app`. This lets the app read `~/Library/Messages/chat.db`.
- **Contacts** (optional): allow it to show sender names. Without it, notifications are generic unless you add name overrides.
- **Local Network**: allow it if macOS asks.

## Pairing

1. Open Bubblecast on Windows and note **This PC's address** and the **Pairing token**.
2. Open Bubblecast from the Mac menu bar and enter the Windows host name, IPv4 address, or bracketed IPv6 address, plus the token.
3. Click **Save**, then **Send test**. A test notification appears on Windows.

The token is stored in the Mac Keychain. Requests go to Windows on TCP `8765` over HTTP and are signed with HMAC-SHA256.

### Pinned TLS (optional)

To encrypt notifications on the LAN, copy the **TLS certificate fingerprint** from Windows, turn on **Use pinned TLS**, paste the 64-character SHA-256 fingerprint, and save. The Mac then connects on TCP `8766` and only trusts that exact certificate. HTTP on `8765` remains available if you turn TLS off.

## Settings

- **Sender contact name** looks up the sender in Contacts. **Sender name overrides** (under Advanced) take priority: one `address = name` per line.
- **Message previews** include the message text (up to 180 characters). Previews may appear in Windows notification history.
- **Ignore old or iCloud-synced messages** skips rows older than **Maximum message age** (default 10 minutes) and rows with unreadable timestamps. Turn it off only to deliver a backlog.
- **Include associated-message rows** is off by default. These rows can include reactions; Apple doesn't document their types, so they get a generic label when they have no text.
- Group chats show the group's name when Messages provides one. System rows are ignored.
- Settings take effect when you click **Save**. Save, Send test, and an unsaved-changes indicator stay in the footer. The window follows the system light/dark appearance.

## Delivery behavior

- New messages are picked up within about three seconds.
- If Windows is unreachable, messages wait and retry with backoff. Messages that were fresh when first read but wait longer than the maximum age are delivered as one count-only catch-up alert once Windows is reachable again.
- If Windows rejects a request (wrong token or clock skew), delivery pauses and the reason is shown in the status card.
- Duplicate rows are suppressed by message ID on both sides.

## Start at login on a headless VM

Turn on **Open at login** under Advanced. If macOS asks for approval, use **Open Login Items settings** and approve Bubblecast. Messages and the app's permissions belong to your user session, so a headless VM also needs automatic login, or a manual login after each reboot.

## Self-checks

From the repository root:

```sh
bash macos/build.command test
```

The checks use a fake clock and transport, synthetic protocol and timestamp values, and a fake token store. They never touch Messages or Contacts. To create a synthetic SQLite fixture (never derived from `chat.db`):

```sh
swiftc -O -lsqlite3 -o /tmp/MakeFixtureDB macos/Tests/MakeFixtureDB.swift
/tmp/MakeFixtureDB /tmp/relay-fixture.sqlite
```

See the [protocol notes and test vectors](PROTOCOL.md) for the request format shared with Windows.

## Troubleshooting

- **"Can't read Messages"**: grant Full Disk Access to `~/Applications/Bubblecast.app` and relaunch it.
- **Rejected — check the pairing token or Mac clock**: re-copy the token from Windows, and make sure both clocks are within two minutes of each other.
- **Unreachable**: check the Windows address, that Bubblecast is running on Windows, and that the Windows firewall allows the port on your private network.
- **Copy privacy-safe diagnostics** (under Advanced) copies counts and status only. It excludes addresses, tokens, sender names, and message text.
- Never share `chat.db`, logs, or screenshots that show your token.
