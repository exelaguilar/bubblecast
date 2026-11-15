using System;
using System.Security.Cryptography;
using System.Text;

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

    public static class RelayProtocolV1
    {
        public static string Canonical(string path, string timestamp, string nonce, string senderBase64, string previewBase64)
        {
            return String.Join("\n", new string[] { path, timestamp, nonce, senderBase64, previewBase64 });
        }

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
            return RelayProtocolV1.Signature(token, "msgid\n" + guid).Substring(0, 32);
        }

        public static string Signature(string token, string canonical)
        {
            return RelayProtocolV1.Signature(token, canonical);
        }
    }
}
