using System;
using System.Collections.Generic;
using System.Globalization;

namespace Bubblecast
{
    /// <summary>
    /// The Mac's notification settings, editable on either side. The newest revision wins: the Mac
    /// pushes its copy with POST /settings, and Windows returns its own copy, signed, on every accepted
    /// response. Connection settings never travel. Matches macos/Sources/Core/SharedSettings.swift.
    /// </summary>
    public sealed class SharedSettings
    {
        public const int MaximumEncodedLength = 512;

        /// <summary>Milliseconds since 1970 when these values were last edited; 0 means never shared.</summary>
        public long Revision;
        public bool IncludeSenderNames = true;
        public bool IncludeMessagePreviews = true;
        public bool SuppressOldMessages = true;
        public int MaximumMessageAgeMinutes = 10;
        public bool NotifyForReactions;
        public bool SkipReadMessages = true;
        public bool QuietWhileActive = true;
        public int ActiveMinutes = 2;

        public SharedSettings Clone()
        {
            return (SharedSettings)MemberwiseClone();
        }

        public bool SameValues(SharedSettings other)
        {
            return other != null &&
                IncludeSenderNames == other.IncludeSenderNames && IncludeMessagePreviews == other.IncludeMessagePreviews &&
                SuppressOldMessages == other.SuppressOldMessages && MaximumMessageAgeMinutes == other.MaximumMessageAgeMinutes &&
                NotifyForReactions == other.NotifyForReactions && SkipReadMessages == other.SkipReadMessages &&
                QuietWhileActive == other.QuietWhileActive && ActiveMinutes == other.ActiveMinutes;
        }

        /// <summary>key=value pairs joined by ';' in a fixed order; ASCII only, so it fits in an HTTP header.</summary>
        public string Encode()
        {
            return String.Join(";", new string[]
            {
                "rev=" + Revision.ToString(CultureInfo.InvariantCulture),
                "names=" + Flag(IncludeSenderNames),
                "previews=" + Flag(IncludeMessagePreviews),
                "old=" + Flag(SuppressOldMessages),
                "age=" + Clamp(MaximumMessageAgeMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture),
                "reactions=" + Flag(NotifyForReactions),
                "read=" + Flag(SkipReadMessages),
                "active=" + Flag(QuietWhileActive),
                "activemin=" + Clamp(ActiveMinutes, 1, 60).ToString(CultureInfo.InvariantCulture)
            });
        }

        /// <summary>Parses Encode() output. Unknown keys are ignored; missing keys keep defaults; a revision is required.</summary>
        public static bool TryParse(string text, out SharedSettings settings)
        {
            settings = null;
            if (String.IsNullOrEmpty(text) || text.Length > MaximumEncodedLength) return false;
            foreach (char c in text)
                if (c < 0x20 || c >= 0x7F) return false;
            SharedSettings result = new SharedSettings();
            bool sawRevision = false;
            foreach (string pair in text.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                long value;
                if (equals <= 0 || !Int64.TryParse(pair.Substring(equals + 1), NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    return false;
                switch (pair.Substring(0, equals))
                {
                    case "rev": result.Revision = value; sawRevision = true; break;
                    case "names": result.IncludeSenderNames = value != 0; break;
                    case "previews": result.IncludeMessagePreviews = value != 0; break;
                    case "old": result.SuppressOldMessages = value != 0; break;
                    case "age": result.MaximumMessageAgeMinutes = (int)Math.Min(1440, Math.Max(1, value)); break;
                    case "reactions": result.NotifyForReactions = value != 0; break;
                    case "read": result.SkipReadMessages = value != 0; break;
                    case "active": result.QuietWhileActive = value != 0; break;
                    case "activemin": result.ActiveMinutes = (int)Math.Min(60, Math.Max(1, value)); break;
                }
            }
            if (!sawRevision) return false;
            settings = result;
            return true;
        }

        /// <summary>Signs the settings Windows returns, bound to the request's nonce so old responses can't be replayed.</summary>
        public static string ResponseSignature(string token, string nonce, string encoded)
        {
            return RelayHmac.Signature(token, "v2-settings\n" + nonce + "\n" + encoded);
        }

        public static long NowRevision()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static string Flag(bool value) { return value ? "1" : "0"; }

        private static int Clamp(int value, int minimum, int maximum) { return Math.Min(maximum, Math.Max(minimum, value)); }
    }
}
