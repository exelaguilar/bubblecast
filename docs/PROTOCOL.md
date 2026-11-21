# Bubblecast relay protocol (v1 and v2)

This document defines the request format between Bubblecast for Mac and Bubblecast for Windows. Both sides use v2. The v1 format described below is kept only as a reference vector for the shared HMAC; since 1.1.0 Windows rejects requests without `X-Relay-Version: 2`. HTTP v1 remains available on TCP 8765. v2 requests may use the same HTTP endpoint or pinned TLS on TCP 8766. HMAC-SHA256 authenticates and protects request integrity; only TLS hides notification contents on the LAN.

## Endpoints and responses

| Request | Purpose | Successful response |
| --- | --- | --- |
| `POST /notify` | Display one notification | `204 No Content` |
| `GET /health` | Check that the receiver is reachable | `204 No Content` |
| `POST /settings` | Share the Mac's notification settings (1.2+) | `204 No Content` |

All endpoints use TCP port `8765` (and `8766` for TLS). Other method/path pairs receive `404 Not Found`. Invalid or expired signatures and reused nonces receive `403 Forbidden`. Requests have no body; the receiver closes the connection after its response.

## Authentication

The pairing token is the existing 64-character hexadecimal string. The HMAC key is the UTF-8 encoding of those 64 ASCII characters; implementations do **not** hex-decode it into 32 bytes.

For each request, the Mac sends these headers:

- `X-Relay-Time`: decimal Unix time in whole seconds.
- `X-Relay-Nonce`: a lowercase UUID string, used once.
- `X-Relay-Signature`: lowercase hexadecimal HMAC-SHA256.
- `X-Sender-Base64` and `X-Preview-Base64`: optional standard Base64 of the corresponding UTF-8 text. Omit a header when its text is empty. `/health` uses empty sender and preview fields.

For v1, the canonical UTF-8 string is the following five values joined by one LF (`0A`) byte, with no leading or trailing LF:

```text
path
timestamp
nonce
senderBase64
previewBase64
```

The signature is `HMAC-SHA256(UTF8(token), UTF8(canonical))`, rendered as 64 lowercase hex characters. HTTP header line endings are CRLF, but they are not part of the canonical string.

## Synthetic test vectors

These vectors use a synthetic token consisting of 64 lowercase `a` characters. They contain no deployment credentials or message data.
The fixed timestamp makes signatures reproducible; it is intentionally outside the current receiver's ±120-second acceptance window, so these checks validate canonicalization and HMAC only.

### Notification

```text
Token:      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
Path:       /notify
Timestamp:  1700000000
Nonce:      123e4567-e89b-12d3-a456-426614174000
Sender:     SmFuZQ==
Preview:    SGVsbG8=
Canonical:  /notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nSmFuZQ==\nSGVsbG8=
Signature:  8f94d22c2419fb72df278b2238ba5fa017b9d1a05f4ed516056a1715e0f347bc
```

`SmFuZQ==` decodes to synthetic sender `Jane`; `SGVsbG8=` decodes to synthetic preview `Hello`.

### Health check

```text
Token:      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
Path:       /health
Timestamp:  1700000000
Nonce:      123e4567-e89b-12d3-a456-426614174001
Sender:     (empty; header omitted)
Preview:    (empty; header omitted)
Canonical:  /health\n1700000000\n123e4567-e89b-12d3-a456-426614174001\n\n
Signature:  cf00cc8ba632375d1ba7cb70e6605a1e89e6fdee089436330aa83c03afebba1a
```

The self-checks exercise v1 and v2 signature vectors and confirm that changing a canonical field changes the resulting signature:

- Mac: `bash macos/build.command test`
- Windows: `cmd /c windows\build.cmd test`

## Version 2

Windows accepts v1 and v2 requests. v1 canonicalization is unchanged. A v2 request adds these headers:

- `X-Relay-Version: 2`
- `X-Relay-Kind: message|test|health`
- `X-Relay-Message-Id`: empty for `test` and `health`; 32 lowercase hex characters for `message`.

The message ID is the first 32 lowercase hex characters of `HMAC-SHA256(UTF8(token), UTF8("msgid\n" + messageGuid))`. It is opaque to Windows and deduplicated there for ten minutes. For a count-only catch-up alert, the Mac derives one ID from the joined GUIDs in that batch.

The v2 canonical UTF-8 string is these nine values, joined with one LF and no leading or trailing LF:

```text
v2
METHOD
path
timestamp
nonce
kind
messageId
senderBase64
previewBase64
```

All responses include `X-Relay-Server-Version`. A v2 `403` also includes `X-Relay-Error` with `auth`, `clock-skew`, or `replay`. These response headers are ignored by v1 clients.

### Synthetic v2 notification vector

```text
Token:      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
Method:     POST
Path:       /notify
Timestamp:  1700000000
Nonce:      123e4567-e89b-12d3-a456-426614174000
Kind:       message
Message ID: 0123456789abcdef0123456789abcdef
Sender:     SmFuZQ==
Preview:    SGVsbG8=
Canonical:  v2\nPOST\n/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nmessage\n0123456789abcdef0123456789abcdef\nSmFuZQ==\nSGVsbG8=
Signature:  f4182f094163c132a423b6be497efd9d40e79f57503a19b644d2b2bce3c7f876
```

## Optional pinned TLS

Windows generates a self-signed TLS 1.2 certificate in the current user's certificate store and listens on TCP 8766 when that succeeds. Its settings show the SHA-256 fingerprint of the certificate's DER bytes. The Mac uses an ephemeral URL session and accepts the self-signed certificate only when that fingerprint matches. HMAC remains required. Port 8765 remains available for plain HTTP and v1 rollback. The installer does not create firewall rules; allow 8766 manually only if the Windows firewall blocks the trusted LAN connection.

## Compatibility boundary

Bubblecast for Windows 1.1.0 and later accepts only v2 requests, on port 8765 (HTTP) and on port 8766 (TLS) when its certificate is available. Bubblecast for Mac always sends v2.

## Discovery

Windows announces the receiver with DNS-SD over multicast DNS as `<PC name>._bubblecast._tcp.local`, with an SRV record for port 8765, TXT keys `v=2` and `tls=8766` (empty when TLS is unavailable), and address records for `<PC name>.local`. It announces on the primary LAN address, or on the configured listen address. The Mac browses for `_bubblecast._tcp` and addresses a chosen PC as `<PC name>.local`. Discovery only finds the PC; every request is still signed with the pairing token.

## Shared settings (1.2+)

The Mac's notification settings can be changed on either side. They travel as one ASCII string of `key=value` pairs joined by `;`:

```text
rev=1700000000000;names=1;previews=0;old=1;age=10;reactions=0;read=1;active=1;activemin=2
```

| Key | Meaning |
| --- | --- |
| `rev` | Revision: Unix time in milliseconds of the last edit. Required. `0` means "nothing yet". |
| `names`, `previews`, `reactions` | Sender names, message previews, associated-message rows (`1`/`0`). |
| `old`, `age` | Ignore old messages, and the maximum age in minutes (1–1440). |
| `read` | Skip messages already marked read. |
| `active`, `activemin` | Stay quiet in a conversation with recent activity, and for how many minutes (1–60). |

Receivers ignore unknown keys and use defaults for missing ones, so either side can add settings later. The string is at most 512 characters.

- **Mac → Windows:** `POST /settings` with kind `settings`, an empty message ID, and the string in `X-Relay-Settings`. In the v2 canonical string the sender field is empty and the string takes the preview's place. Windows keeps it if its `rev` is newer than what it has, and answers `400` if the string is malformed.
- **Windows → Mac:** every accepted `/health`, `/notify`, and `/settings` response from a 1.2 receiver carries `X-Relay-Settings` (its current string, or `rev=0`) and `X-Relay-Settings-Signature`: lowercase hex `HMAC-SHA256(UTF8(token), UTF8("v2-settings\n" + requestNonce + "\n" + string))`. Binding it to the request's nonce stops an old response from being replayed. The Mac ignores headers whose signature doesn't match.
- The newer `rev` wins. The Mac adopts a newer Windows copy, and pushes its own when Windows has an older one. A Mac upgrading from 1.1 starts at `rev=1`, so its existing choices seed a PC that has none.

### Synthetic settings vectors

```text
Token:      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
Settings:   rev=1700000000000;names=1;previews=0;old=1;age=10;reactions=0;read=1;active=1;activemin=2
Nonce:      123e4567-e89b-12d3-a456-426614174002
Request:    POST /settings at 1700000000, kind settings
Request signature:  284b277e830115a39d9c89a696468820655071f5ec5ae3323dccd14ef9354ca9
Response signature: 467902cb72f63cb24a7df57588fbd9c29a3b7626b3aed8836c51c2f427ef550b
```

## Pinning on first use

With TLS on and no fingerprint saved, the Mac sends one signed `/health` request over TLS that accepts the certificate Windows presents, and fills in that certificate's SHA-256 fingerprint for the user to compare with the fingerprint Windows shows. The fingerprint is pinned when the user saves it; after that the Mac rejects any other certificate.
