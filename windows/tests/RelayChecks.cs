using System;
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
        string notifyCanonical = RelayProtocolV1.Canonical("/notify", "1700000000", "123e4567-e89b-12d3-a456-426614174000", "SmFuZQ==", "SGVsbG8=");
        Check(RelayProtocolV1.Signature(token, notifyCanonical) == "8f94d22c2419fb72df278b2238ba5fa017b9d1a05f4ed516056a1715e0f347bc", "v1 notify HMAC matches the protocol vector");
        string healthCanonical = RelayProtocolV1.Canonical("/health", "1700000000", "123e4567-e89b-12d3-a456-426614174001", "", "");
        Check(RelayProtocolV1.Signature(token, healthCanonical) == "cf00cc8ba632375d1ba7cb70e6605a1e89e6fdee089436330aa83c03afebba1a", "v1 health HMAC matches the protocol vector");
        string v2MessageId = RelayProtocolV2.MessageId(token, "guid-demo");
        string fixedMessageId = "0123456789abcdef0123456789abcdef";
        string v2Canonical = RelayProtocolV2.Canonical("POST", "/notify", "1700000000", "123e4567-e89b-12d3-a456-426614174000", "message", fixedMessageId, "SmFuZQ==", "SGVsbG8=");
        Check(v2Canonical == "v2\nPOST\n/notify\n1700000000\n123e4567-e89b-12d3-a456-426614174000\nmessage\n" + fixedMessageId + "\nSmFuZQ==\nSGVsbG8=", "v2 canonical string covers every signed field in order");
        Check(v2MessageId.Length == 32 && v2MessageId == RelayProtocolV2.MessageId(token, "guid-demo"), "v2 message ID is stable and opaque-sized");
        Check(RelayProtocolV2.Signature(token, v2Canonical).Length == 64, "v2 signature is an HMAC-SHA256 hex digest");
        Check(RelayProtocolV2.Signature(token, v2Canonical) == "f4182f094163c132a423b6be497efd9d40e79f57503a19b644d2b2bce3c7f876", "v2 notify HMAC matches the protocol vector");
        Check(RelayProtocolV1.Signature(token, notifyCanonical + " ") != "8f94d22c2419fb72df278b2238ba5fa017b9d1a05f4ed516056a1715e0f347bc", "changing a canonical field changes the HMAC");
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
        Console.WriteLine("PASS: " + _checks + " Windows pure-logic checks");
        return 0;
    }
}
