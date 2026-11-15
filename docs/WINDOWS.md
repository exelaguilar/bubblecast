# Bubblecast for Windows — full guide

Bubblecast for Windows is a native .NET Framework tray app. It listens on TCP `8765` for signed HTTP requests and, when its per-user certificate is available, on TCP `8766` for pinned TLS. It verifies each request and shows a Windows notification.

## Install

Download **Bubblecast Setup** from the [latest release](https://github.com/exelaguilar/bubblecast/releases/latest) and run it. Windows SmartScreen may warn because the app isn't code-signed; choose **More info → Run anyway**, or build it yourself.

Setup installs to `C:\Program Files\Bubblecast` by default and lets you choose:

- Start menu shortcuts (app and uninstall)
- A desktop shortcut
- Start at sign-in (quietly, in the notification area)
- Open Settings when setup finishes

When updating, Setup asks the running app to close, shows progress, stages the new app beside the old one, and replaces it only after staging succeeds. If the app doesn't close, Setup explains how to close it and offers **Retry**. Cancel or Escape closes Setup whenever an install isn't running. Setup never adds firewall rules.

## Settings

Open **Bubblecast** from the Start menu, the desktop, or by double-clicking the tray icon. The window follows the Windows light/dark theme and opens with everything the Mac needs at the top.

**Pairing**

- **This PC's address**: IPv4 addresses by adapter. Adapters with a default gateway are listed first as likely LAN addresses.
- **Pairing token**: shown masked; **Show** reveals it and **Copy** copies it. Copies are excluded from Clipboard History and cleared after one minute.
- **TLS certificate fingerprint**: paste it into the Mac app to enable pinned TLS.
- **Open Mac desktop**: starts Remote Desktop to the last Mac address on port `3390`, or to the override in Advanced.

**Notifications** (apply when you click **Save settings**)

- **Quiet hours**: suppress notifications between two 24-hour `HH:mm` times on this PC's clock. Notifications aren't queued. Set *From* later than *until* for overnight hours.
- **Muted contacts**: one sender name per line, matched exactly.
- **Start when I sign in to Windows**: applies immediately.

Test notifications from the Mac always appear, even during quiet hours or for muted senders.

**Advanced** (collapsed by default)

- **Mac Remote Desktop address**: optional override such as `mac-vm.local:3390`.
- **Only accept requests from the paired Mac IP**: accepts requests only from the last address that sent a valid signed request. If the Mac's address changes, turn this off, let the Mac connect, then turn it back on.
- **Rotate pairing token**: replaces the token immediately; notifications stop until the Mac has the new token.
- **Diagnostics**: notification and health-check counts, rejections by reason (bad signature, clock skew, replay, malformed, oversize, address not allowed), and the last authenticated clock skew. **Copy privacy-safe diagnostics** excludes the token, addresses, sender names, and message text.

The footer shows the live receiver status and the header shows the pairing state ("Paired · Mac seen 1 min ago"). The tray tooltip shows when the Mac was last seen. Clicking a notification opens Settings.

## Network and security

- Every request must carry a valid HMAC-SHA256 signature, a timestamp within ±120 seconds, and a fresh nonce. Requests from before the app started are rejected as replays.
- HTTP on `8765` authenticates requests but doesn't encrypt them; turn on pinned TLS on the Mac to encrypt notification contents. The TLS certificate is self-signed and stored in your Windows user's certificate store.
- Request lines, headers, and sender/preview sizes are bounded, slow requests time out, and at most four connections are handled at once.
- If the Windows firewall blocks the Mac, add a narrow inbound rule yourself for your private network and the port you use. Never port-forward `8765` or `8766` to the internet.

## Where data is stored

| Data | Location |
| --- | --- |
| App | `C:\Program Files\Bubblecast` |
| Pairing token | `%LOCALAPPDATA%\Bubblecast\token.txt` |
| Settings | `HKCU\Software\Bubblecast` |
| TLS certificate | Current user's personal certificate store, `CN=Bubblecast Local TLS` |
| Sign-in shortcut | `Startup\Bubblecast.lnk` |

If `token.txt` is damaged, Bubblecast stops with a repair message. Delete the file to generate a new token, then update the Mac.

## Uninstall

Use **Settings → Apps → Installed apps → Bubblecast** or the Start menu uninstall shortcut. Uninstall removes only Bubblecast's own files, its Start menu shortcuts, and its sign-in shortcut (delete a desktop shortcut yourself), and keeps your token and settings so a reinstall stays paired. To remove those too, delete `%LOCALAPPDATA%\Bubblecast` and `HKCU\Software\Bubblecast`.

## Build from source

No SDK is needed; the build uses the C# compiler included with Windows.

```bat
cmd /c windows\build.cmd test
```

This builds `windows\build\Bubblecast.exe` and `windows\build\Bubblecast Setup.exe`, then runs the pure-logic checks (token validation, request parsing, text sanitizing, protocol vectors, startup shortcut policy, quiet hours, and status display). `cmd /c windows\build.cmd release` also copies both executables to `windows\release\` and writes `SHA256SUMS` and `RELEASE-NOTES.md` with the source commit and compiler version.

The build embeds `Bubblecast.ico` and `BrandMark.png`, which are generated by `tools/BrandIcon.cs`. See the [protocol notes and test vectors](PROTOCOL.md) for the request format.
