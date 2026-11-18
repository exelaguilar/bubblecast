using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bubblecast;

internal static class RelayChecks
{
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        _checks++;
    }

    private sealed class TestClock
    {
        public DateTime Now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    }

    private static long UnixSeconds(DateTime utc)
    {
        return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    private static Dictionary<string, string> Signed(string token, string method, string path, string kind, string messageId, long timestamp, string nonce, string sender, string preview)
    {
        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string timestampText = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        headers["X-Relay-Version"] = "2";
        headers["X-Relay-Kind"] = kind;
        headers["X-Relay-Message-Id"] = messageId;
        headers["X-Relay-Time"] = timestampText;
        headers["X-Relay-Nonce"] = nonce;
        headers["X-Relay-Signature"] = RelayProtocolV2.Signature(token, RelayProtocolV2.Canonical(method, path, timestampText, nonce, kind, messageId, sender, preview));
        return headers;
    }

    private static void VerifierChecks(string token)
    {
        TestClock clock = new TestClock();
        RequestVerifier verifier = new RequestVerifier(delegate { return token; }, delegate { return clock.Now; });
        clock.Now = clock.Now.AddSeconds(30);
        long now = UnixSeconds(clock.Now);
        string id = RelayProtocolV2.MessageId(token, "guid-one");

        RequestVerification ok = verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, now, Guid.NewGuid().ToString(), "SmFuZQ==", "SGVsbG8="), "SmFuZQ==", "SGVsbG8=");
        Check(ok.Accepted && ok.Kind == "message" && !ok.DuplicateMessage && ok.ErrorCode == null, "verifier accepts a correctly signed v2 message");

        Dictionary<string, string> tampered = Signed(token, "POST", "/notify", "message", id, now, Guid.NewGuid().ToString(), "SmFuZQ==", "SGVsbG8=");
        Check(verifier.Verify("POST", "/notify", tampered, "SmFuZQ==", "SGVsbG8gdGhlcmU=").Rejection == RequestRejectionReason.BadSignature, "verifier rejects a changed preview as a bad signature");
        Check(new RequestVerifier(delegate { return new string('b', 64); }, delegate { return clock.Now; })
            .Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, now, Guid.NewGuid().ToString(), "", ""), "", "").Rejection == RequestRejectionReason.BadSignature,
            "verifier rejects a request signed with a different token");

        Dictionary<string, string> v1 = Signed(token, "POST", "/notify", "message", id, now, Guid.NewGuid().ToString(), "", "");
        v1.Remove("X-Relay-Version");
        Check(verifier.Verify("POST", "/notify", v1, "", "").Rejection == RequestRejectionReason.Malformed, "verifier rejects requests without protocol v2");

        RequestVerification skewed = verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, now - 121, Guid.NewGuid().ToString(), "", ""), "", "");
        Check(skewed.Rejection == RequestRejectionReason.ClockSkew && skewed.ErrorCode == "clock-skew" && verifier.LastClockSkewSeconds == -121, "verifier rejects a timestamp outside ±120 seconds and records the skew");

        Check(verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, now - 60, Guid.NewGuid().ToString(), "", ""), "", "").Rejection == RequestRejectionReason.Replay,
              "verifier rejects a timestamp from before the receiver started");

        string nonce = Guid.NewGuid().ToString();
        verifier.Verify("GET", "/health", Signed(token, "GET", "/health", "health", "", now, nonce, "", ""), "", "");
        RequestVerification replay = verifier.Verify("GET", "/health", Signed(token, "GET", "/health", "health", "", now, nonce, "", ""), "", "");
        Check(replay.Rejection == RequestRejectionReason.Replay && replay.ErrorCode == "replay", "verifier rejects a reused nonce");

        RequestVerification again = verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, now, Guid.NewGuid().ToString(), "", ""), "", "");
        Check(again.Accepted && again.DuplicateMessage, "verifier flags a repeated message ID as a duplicate");
        clock.Now = clock.Now.AddMinutes(11);
        RequestVerification later = verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, UnixSeconds(clock.Now), Guid.NewGuid().ToString(), "", ""), "", "");
        Check(later.Accepted && !later.DuplicateMessage, "message-ID de-duplication expires after 10 minutes");

        long current = UnixSeconds(clock.Now);
        Check(verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "health", "", current, Guid.NewGuid().ToString(), "", ""), "", "").Rejection == RequestRejectionReason.Malformed,
              "verifier rejects a health kind on the notify endpoint");
        Check(verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", "NOT-OPAQUE", current, Guid.NewGuid().ToString(), "", ""), "", "").Rejection == RequestRejectionReason.Malformed,
              "verifier rejects a malformed message ID");
        string bigPreview = new string('A', RequestVerifier.MaximumPreviewBase64Length + 4);
        Check(verifier.Verify("POST", "/notify", Signed(token, "POST", "/notify", "message", id, current, Guid.NewGuid().ToString(), "", bigPreview), "", bigPreview).Rejection == RequestRejectionReason.Oversize,
              "verifier rejects an oversized preview");
    }

    private static int Main()
    {
        Check(TokenValidation.IsValid(new string('a', 64)), "64 lowercase hex characters accepted");
        Check(TokenValidation.IsValid(new string('F', 64)), "64 uppercase hex characters accepted");
        Check(!TokenValidation.IsValid(null), "null rejected");
        Check(!TokenValidation.IsValid(String.Empty), "empty token rejected");
        Check(!TokenValidation.IsValid(new string('0', 63)), "short token rejected");
        Check(!TokenValidation.IsValid(new string('0', 65)), "long token rejected");
        Check(!TokenValidation.IsValid(new string('g', 64)), "non-hex token rejected");
        Check(!TokenValidation.IsValid(new string('0', 63) + " "), "whitespace rejected");
        string boundaryEmoji = new string('a', 179) + "\ud83d\ude00" + "z";
        string truncated = DisplayTextSanitizer.SanitizeAndTruncate(boundaryEmoji, 180);
        Check(truncated.EndsWith("\ud83d\ude00", StringComparison.Ordinal), "truncation preserves a complete surrogate pair at the boundary");
        Check(!truncated.Contains("z"), "truncation stops at the configured text-element count");
        Check(DisplayTextSanitizer.SanitizeAndTruncate("a\u202Eb", 10) == "ab", "bidi format controls are stripped");
        Check(DisplayTextSanitizer.SanitizeAndTruncate("👨‍👩‍👧‍👦", 10).Contains("\u200D"), "emoji joiners are preserved");
        string token = new string('a', 64);
        string v2MessageId = RelayProtocolV2.MessageId(token, "guid-demo");
        string fixedMessageId = "0123456789abcdef0123456789abcdef";
        string v2Canonical = RelayProtocolV2.Canonical("POST", "/notify", "1700000000", "123e4567-e89b-12d3-a456-426614174000", "message", fixedMessageId, "SmFuZQ==", "SGVsbG8=");
        Check(v2Canonical == "v2\nPOST\n/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nmessage\n" + fixedMessageId + "\nSmFuZQ==\nSGVsbG8=", "v2 canonical string covers every signed field in order");
        Check(v2MessageId.Length == 32 && v2MessageId == RelayProtocolV2.MessageId(token, "guid-demo"), "v2 message ID is stable and opaque-sized");
        Check(RelayProtocolV2.Signature(token, v2Canonical).Length == 64, "v2 signature is an HMAC-SHA256 hex digest");
        Check(RelayProtocolV2.Signature(token, v2Canonical) == "f4182f094163c132a423b6be497efd9d40e79f57503a19b644d2b2bce3c7f876", "v2 notify HMAC matches the protocol vector");
        Check(RelayHmac.Signature(token, v2Canonical + " ") != RelayHmac.Signature(token, v2Canonical), "changing a canonical field changes the HMAC");
        Check(RelayHmac.Signature(token, "/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nSmFuZQ==\nSGVsbG8=") == "8f94d22c2419fb72df278b2238ba5fa017b9d1a05f4ed516056a1715e0f347bc", "HMAC-SHA256 keyed by the token's UTF-8 bytes matches the reference vector");
        VerifierChecks(token);
        Check(!StartupShortcutPolicy.ShouldRetarget(true, true), "valid Startup shortcut is left unchanged");
        Check(StartupShortcutPolicy.ShouldRetarget(true, false), "Startup shortcut with a missing target is repaired");
        Check(!StartupShortcutPolicy.ShouldRetarget(false, false), "portable launch does not create a Startup shortcut");
        Check(LaunchModePolicy.ShouldShowSettings(new string[0]), "explicit app launch opens Settings");
        Check(!LaunchModePolicy.ShouldShowSettings(new string[] { "--background" }), "sign-in launch stays in the notification area");
        Check(!LaunchModePolicy.ShouldShowSettings(new string[] { "--BACKGROUND" }), "background launch argument is case-insensitive");
        ParsedHttpRequest parsed;
        string validRequest = "GET /health HTTP/1.1\r\nX-Test: value\r\n\r\n";
        Check(BoundedHttpRequestReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(validRequest)), null, out parsed) == HttpRequestReadStatus.Success && parsed.Headers["X-Test"] == "value",
              "bounded reader accepts a well-formed request");
        string oversizedLine = new string('A', 255) + "\r\n\r\n";
        Check(BoundedHttpRequestReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(oversizedLine)), null, out parsed) == HttpRequestReadStatus.HeadersTooLarge,
              "bounded reader rejects a request line over 256 bytes");
        StringBuilder manyHeaders = new StringBuilder("GET /health HTTP/1.1\r\n");
        for (int i = 0; i < 33; i++) manyHeaders.Append("X-").Append(i).Append(": a\r\n");
        manyHeaders.Append("\r\n");
        Check(BoundedHttpRequestReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(manyHeaders.ToString())), null, out parsed) == HttpRequestReadStatus.HeadersTooLarge,
              "bounded reader rejects more than 32 headers");
        Check(BoundedHttpRequestReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("GET /health HTTP/1.1\n\n")), null, out parsed) == HttpRequestReadStatus.Malformed,
              "bounded reader requires CRLF line endings");
        string oversizedBlock = "GET /health HTTP/1.1\r\nX-Large: " + new string('a', 8200) + "\r\n\r\n";
        Check(BoundedHttpRequestReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(oversizedBlock)), null, out parsed) == HttpRequestReadStatus.HeadersTooLarge,
              "bounded reader rejects header blocks over 8 KB");
        TimeSpan quietTime;
        Check(QuietHoursText.TryParse("22:00", out quietTime) && quietTime == new TimeSpan(22, 0, 0), "quiet hours accept 24-hour HH:mm");
        Check(QuietHoursText.TryParse(" 7:05 ", out quietTime) && QuietHoursText.Format(quietTime) == "07:05", "quiet hours accept H:mm and format as HH:mm");
        Check(!QuietHoursText.TryParse("24:00", out quietTime) && !QuietHoursText.TryParse("7pm", out quietTime), "quiet hours reject out-of-range or non-24-hour text");
        Check(StatusToneClassifier.Classify("Listening on TCP 8765") == StatusTone.Success, "listener status is shown as healthy");
        Check(StatusToneClassifier.Classify("Rejected a request from the Mac: bad signature.") == StatusTone.Destructive, "rejections are shown as errors");
        Check(StatusToneClassifier.Classify("TLS listener unavailable; HTTP remains available.") == StatusTone.Warning, "degraded TLS is shown as a warning");
        string protectedToken = TokenFile.Protect(token);
        string roundTrip;
        bool upgrade;
        Check(TokenFile.TryRead(protectedToken, out roundTrip, out upgrade) && roundTrip == token && !upgrade && !protectedToken.Contains(token),
              "the token file is DPAPI-encrypted and round-trips");
        Check(TokenFile.TryRead(token, out roundTrip, out upgrade) && roundTrip == token && upgrade, "a plain-text token is accepted once and marked for encryption");
        Check(!TokenFile.TryRead("dpapi:AAAA", out roundTrip, out upgrade) && !TokenFile.TryRead("not-a-token", out roundTrip, out upgrade), "damaged token files are rejected");
        List<string> localAddresses = new List<string> { "192.0.2.10" };
        Check(NetworkAddresses.IsUsableListenAddress("", localAddresses) && NetworkAddresses.IsUsableListenAddress(" 192.0.2.10 ", localAddresses) &&
              !NetworkAddresses.IsUsableListenAddress("198.51.100.1", localAddresses) && !NetworkAddresses.IsUsableListenAddress("not-an-ip", localAddresses),
              "the listen address must be blank or one of this PC's IPv4 addresses");
        Console.WriteLine("PASS: " + _checks + " Windows pure-logic checks");
        return 0;
    }
}
