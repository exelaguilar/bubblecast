# Bubblecast for Mac — full guide

Bubblecast for Mac is a SwiftUI menu-bar app for macOS 13 Ventura and later on Intel or Apple Silicon (a Mac or a macOS VM). It reads the Messages database read-only, filters out old and duplicate rows, optionally resolves contact names, and sends signed notifications to Bubblecast for Windows on your LAN.

## Install

1. Download **Bubblecast-<version>.dmg** from the [latest release](https://github.com/exelaguilar/bubblecast/releases/latest).
2. Open it and drag **Bubblecast** to **Applications**.
3. Open Bubblecast. Bubblecast isn't signed with an Apple Developer ID, so the first launch is blocked. Open **System Settings → Privacy & Security**, scroll to the message about Bubblecast, and click **Open Anyway**. You only need to do this once per version.

Bubblecast appears in the menu bar, not the Dock. The disk image contains a universal app for Intel and Apple Silicon, built by GitHub Actions from the release's source.

Because the app isn't Developer ID–signed, each new version has a different code signature, and macOS may ask you to grant Full Disk Access again after updating.

## Build from source

With the Xcode Command Line Tools installed, run from the repository root:

```sh
bash macos/build.command           # build for this Mac, install to ~/Applications/Bubblecast.app, and launch
bash macos/build.command package   # universal app and dist/Bubblecast-<version>.dmg (installs nothing)
```

The script builds the icon set from `macos/Resources/AppIcon-1024.png`, compiles for macOS 13 and later, signs ad hoc, and (in install mode) asks a running copy to quit before replacing it.

To sign with a stable identity instead of ad hoc, set `RELAY_SIGN_IDENTITY` to the name of a signing identity in your Keychain before building. A stable identity keeps Full Disk Access and Contacts permissions across updates.

## Permissions

- **Full Disk Access** (required): **System Settings → Privacy & Security → Full Disk Access**, then add Bubblecast (`/Applications/Bubblecast.app`, or `~/Applications/Bubblecast.app` if you built it yourself). This lets the app read `~/Library/Messages/chat.db`.
- **Contacts** (optional): allow it to show sender names. Without it, notifications are generic unless you add name overrides.
- **Local Network**: allow it when macOS asks. Bubblecast uses it to find your PC and to send it notifications.

## Pairing

1. Open Bubblecast on Windows and copy the **Pairing token**.
2. Open Bubblecast from the Mac menu bar. Under **Windows PC address**, your PC appears in *Found on your network*; click **Use**. (Or type its host name, IPv4 address, or bracketed IPv6 address.)
3. Paste the token and click **Save**.
4. With **Use pinned TLS** on (the default), the Mac contacts Windows and fills in the certificate fingerprint it learned. Check that it matches the **TLS certificate fingerprint** shown on Windows, then click **Save** again to pin it.
5. Click **Send test**. A test notification appears on Windows.

The token is stored in the Mac Keychain, and every request is signed with HMAC-SHA256.

### TLS and the certificate fingerprint

With pinned TLS on, the Mac connects to Windows on TCP `8766` and only trusts the exact certificate whose SHA-256 fingerprint you saved. Leaving the fingerprint blank and saving lets the Mac learn it from Windows the first time (trust on first use); you can also paste it from Windows yourself. If you turn TLS off, requests use HTTP on TCP `8765`; they are still signed, but not encrypted.

### Discovery

Bubblecast on Windows announces itself as `_bubblecast._tcp` on the local network, and the Mac lists every receiver it finds. A chosen PC is addressed as `<PC name>.local`, so it keeps working when the PC's IP address changes. Discovery only finds the PC; pairing still needs the token. If your PC doesn't appear, check that both machines are on the same network, that **Let the Mac find this PC** is on in Windows Advanced settings, and that Bubblecast has Local Network permission in **System Settings → Privacy & Security → Local Network**.

## Settings

Notification settings are in two cards, the same two groups as the **Mac** page in Bubblecast for Windows: **What alerts show** (sender names, previews, associated-message rows) and **When to stay quiet** (skip read messages, quiet while active, ignore old messages).

- **Sender contact name** looks up the sender in Contacts. **Sender name overrides** (under Advanced) take priority: one `address = name` per line.
- **Message previews** include the message text (up to 180 characters). They're off on a new install; turning them on sends message text to the PC, where it may appear in Windows notification history. Previews made of unusually long characters (such as some emoji) are shortened to fit the protocol's size limit.
- **Ignore old or iCloud-synced messages** skips rows older than **Maximum message age** (default 10 minutes) and rows with unreadable timestamps. Turn it off only to deliver a backlog.
- **Include associated-message rows** is off by default. These rows can include reactions; Apple doesn't document their types, so they get a generic label when they have no text.
- **Skip messages I've already read** (on by default) checks just before sending whether the message is marked read — on the Mac, or on another device through iCloud — and drops it. Because new messages are sent within seconds, this mostly catches messages that waited while Windows was unreachable. A conversation left open in Messages on the Mac counts as read, so keep Messages on no conversation (or closed) in a headless VM.
- **Stay quiet while I'm active in a chat** (on by default) drops alerts for a conversation you sent a message in, or read a message in, during the last few minutes (**Quiet for**, 2 by default, 1–60). It looks at the conversation's 50 most recent messages.
- Group chats show the group's name when Messages provides one. System rows are ignored.
- The notification settings above (everything except the overrides) can also be changed on Windows, on the **Mac** page of its Settings. The most recent change wins: the Mac picks up a change made on Windows within about a minute (its regular check-in), and Windows gets a change saved on the Mac right away. The line at the bottom of the **What alerts show** card shows whether sharing is working. It needs Bubblecast 1.2 or later on Windows.
- Settings take effect when you click **Save**. Save, Send test, and an unsaved-changes indicator stay in the footer. The window follows the system light/dark appearance.

## Delivery behavior

- New messages are picked up as soon as Messages writes them (the app watches the database files, with a 5-second fallback check).
- If Windows is unreachable, messages wait and retry with backoff. Messages that were fresh when first read but wait longer than the maximum age are delivered as one count-only catch-up alert once Windows is reachable again.
- If Windows rejects a request (wrong token or clock skew), delivery pauses and the reason is shown in the status card.
- Duplicate rows are suppressed by message ID on both sides.
- Messages skipped as already read or active elsewhere are counted under Advanced → Diagnostics (**Skipped as seen**). The read check needs Messages' read state to have synced to the Mac; when the database can't be read, the message is sent.

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

- **"Can't read Messages"**: grant Full Disk Access to Bubblecast (remove and re-add it after an update) and relaunch it.
- **"Bubblecast can't be opened"**: use **Open Anyway** in **System Settings → Privacy & Security**.
- **Rejected — check the pairing token or Mac clock**: re-copy the token from Windows, and make sure both clocks are within two minutes of each other.
- **Unreachable**: check the Windows address, that Bubblecast is running on Windows, and that the Windows firewall allows the port on your private network.
- **Couldn't reach Windows over TLS**: check that Windows shows a TLS certificate fingerprint (port `8766`) and that the firewall allows it; or turn TLS off to use HTTP.
- **Copy privacy-safe diagnostics** (under Advanced) copies counts and status only. It excludes addresses, tokens, sender names, and message text.
- Never share `chat.db`, logs, or screenshots that show your token.
