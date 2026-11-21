using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bubblecast
{
    public static class TokenValidation
    {
        public static bool IsValid(string token)
        {
            if (token == null || token.Length != 64) return false;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }
    }

    /// <summary>HMAC-SHA256 keyed by the UTF-8 bytes of the 64-character pairing token, as lowercase hex.</summary>
    public static class RelayHmac
    {
        public static string Signature(string token, string canonical)
        {
            byte[] bytes;
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token)))
                bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }

    public static class RelayProtocolV2
    {
        public static string Canonical(string method, string path, string timestamp, string nonce, string kind, string messageId, string senderBase64, string previewBase64)
        {
            return String.Join("\n", new string[] { "v2", method, path, timestamp, nonce, kind, messageId, senderBase64, previewBase64 });
        }

        public static string MessageId(string token, string guid)
        {
            return RelayHmac.Signature(token, "msgid\n" + guid).Substring(0, 32);
        }

        public static string Signature(string token, string canonical)
        {
            return RelayHmac.Signature(token, canonical);
        }
    }

    public enum RequestRejectionReason { None = 0, BadSignature = 1, ClockSkew = 2, Replay = 3, Malformed = 4, Oversize = 5, AddressNotAllowed = 6 }

    /// <summary>The verdict on one request.</summary>
    public sealed class RequestVerification
    {
        public RequestRejectionReason Rejection;
        public string Kind = "";
        public string MessageId = "";
        /// <summary>Short code for the X-Relay-Error header, or null when accepted.</summary>
        public string ErrorCode;
        /// <summary>True when an accepted message repeats a message ID seen in the last 10 minutes.</summary>
        public bool DuplicateMessage;

        public bool Accepted { get { return Rejection == RequestRejectionReason.None; } }
    }

    /// <summary>
    /// Verifies signed protocol v2 requests: header shape, request kind, HMAC signature (constant-time),
    /// a ±120-second clock window, timestamps from before this process started, one-time nonces, and
    /// 10-minute message-ID de-duplication. Thread-safe. The clock is injectable for tests.
    /// </summary>
    public sealed class RequestVerifier
    {
        public const int ClockWindowSeconds = 120;
        public const int MaximumSenderBase64Length = 1024;
        public const int MaximumPreviewBase64Length = 4096;
        private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MessageIdLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(30);
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly Func<string> _token;
        private readonly Func<DateTime> _utcNow;
        private readonly long _startedUnixSeconds;
        private readonly Dictionary<string, DateTime> _nonces = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _messageIds = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();
        private DateTime _lastPrune = DateTime.MinValue;
        private long _lastClockSkewSeconds;

        public RequestVerifier(Func<string> token, Func<DateTime> utcNow)
        {
            _token = token;
            _utcNow = utcNow;
            _startedUnixSeconds = ToUnixSeconds(utcNow());
        }

        public RequestVerifier(Func<string> token) : this(token, delegate { return DateTime.UtcNow; }) { }

        /// <summary>Seconds the last out-of-window request was ahead (positive) or behind (negative).</summary>
        public long LastClockSkewSeconds { get { return Interlocked.Read(ref _lastClockSkewSeconds); } }

        public RequestVerification Verify(string method, string path, IDictionary<string, string> headers, string senderBase64, string previewBase64)
        {
            RequestVerification result = new RequestVerification { Rejection = RequestRejectionReason.Malformed, ErrorCode = "auth" };
            senderBase64 = senderBase64 ?? "";
            previewBase64 = previewBase64 ?? "";
            if (senderBase64.Length > MaximumSenderBase64Length || previewBase64.Length > MaximumPreviewBase64Length)
                return Reject(result, RequestRejectionReason.Oversize);

            string version, timestampText, nonce, suppliedSignature, kind, messageId;
            long timestamp;
            Guid parsedNonce;
            if (!headers.TryGetValue("X-Relay-Version", out version) || version != "2" ||
                !headers.TryGetValue("X-Relay-Time", out timestampText) ||
                !headers.TryGetValue("X-Relay-Nonce", out nonce) ||
                !headers.TryGetValue("X-Relay-Signature", out suppliedSignature) ||
                !headers.TryGetValue("X-Relay-Kind", out kind) ||
                !headers.TryGetValue("X-Relay-Message-Id", out messageId) ||
                !Int64.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp) ||
                !Guid.TryParse(nonce, out parsedNonce) || suppliedSignature.Length != 64)
                return Reject(result, RequestRejectionReason.Malformed);
            if ((kind != "message" && kind != "test" && kind != "health" && kind != "settings") ||
                (path == "/health" && kind != "health") ||
                (path == "/notify" && kind != "message" && kind != "test") ||
                (path == "/settings" && kind != "settings") ||
                (kind == "message" && !IsOpaqueMessageId(messageId)) ||
                (kind != "message" && messageId.Length != 0))
                return Reject(result, RequestRejectionReason.Malformed);
            result.Kind = kind;
            result.MessageId = messageId;

            string canonical = RelayProtocolV2.Canonical(method, path, timestampText, nonce, kind, messageId, senderBase64, previewBase64);
            if (!ConstantTimeEquals(RelayHmac.Signature(_token(), canonical), suppliedSignature.ToLowerInvariant()))
                return Reject(result, RequestRejectionReason.BadSignature);

            DateTime now = _utcNow();
            long nowSeconds = ToUnixSeconds(now);
            if (timestamp < nowSeconds - ClockWindowSeconds || timestamp > nowSeconds + ClockWindowSeconds)
            {
                decimal measured = (decimal)timestamp - nowSeconds;
                Interlocked.Exchange(ref _lastClockSkewSeconds, measured < Int64.MinValue ? Int64.MinValue : measured > Int64.MaxValue ? Int64.MaxValue : (long)measured);
                return Reject(result, RequestRejectionReason.ClockSkew);
            }
            if (timestamp < _startedUnixSeconds)
                return Reject(result, RequestRejectionReason.Replay);

            lock (_gate)
            {
                Prune(now);
                if (_nonces.ContainsKey(nonce))
                    return Reject(result, RequestRejectionReason.Replay);
                _nonces[nonce] = now;
                if (kind == "message")
                {
                    result.DuplicateMessage = _messageIds.ContainsKey(messageId);
                    _messageIds[messageId] = now;
                }
            }
            result.Rejection = RequestRejectionReason.None;
            result.ErrorCode = null;
            return result;
        }

        private void Prune(DateTime now)
        {
            if (now - _lastPrune < PruneInterval) return;
            _lastPrune = now;
            foreach (string key in _nonces.Where(delegate(KeyValuePair<string, DateTime> item) { return now - item.Value > NonceLifetime; }).Select(delegate(KeyValuePair<string, DateTime> item) { return item.Key; }).ToArray())
                _nonces.Remove(key);
            foreach (string key in _messageIds.Where(delegate(KeyValuePair<string, DateTime> item) { return now - item.Value > MessageIdLifetime; }).Select(delegate(KeyValuePair<string, DateTime> item) { return item.Key; }).ToArray())
                _messageIds.Remove(key);
        }

        private static RequestVerification Reject(RequestVerification result, RequestRejectionReason reason)
        {
            result.Rejection = reason;
            result.ErrorCode = reason == RequestRejectionReason.ClockSkew ? "clock-skew" : reason == RequestRejectionReason.Replay ? "replay" : "auth";
            return result;
        }

        private static long ToUnixSeconds(DateTime utc)
        {
            return (long)(utc - UnixEpoch).TotalSeconds;
        }

        private static bool IsOpaqueMessageId(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static bool ConstantTimeEquals(string left, string right)
        {
            int difference = left.Length ^ right.Length;
            int count = Math.Max(left.Length, right.Length);
            for (int index = 0; index < count; index++)
            {
                char leftValue = index < left.Length ? left[index] : '\0';
                char rightValue = index < right.Length ? right[index] : '\0';
                difference |= leftValue ^ rightValue;
            }
            return difference == 0;
        }
    }
}
