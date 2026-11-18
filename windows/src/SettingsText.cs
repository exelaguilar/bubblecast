using System;
using System.Globalization;

namespace Bubblecast
{
    /// <summary>Maps receiver status text to a visual tone. Display only; it never affects relay behavior.</summary>
    public static class StatusToneClassifier
    {
        public static StatusTone Classify(string status)
        {
            if (String.IsNullOrEmpty(status)) return StatusTone.Neutral;
            string value = status.ToLowerInvariant();
            if (value.Contains("remains available") || value.Contains("still running") || value.Contains("busy") || value.Contains("update the mac"))
                return StatusTone.Warning;
            if (value.Contains("rejected") || value.Contains("unavailable") || value.Contains("could not") || value.Contains("couldn't") || value.Contains("failed"))
                return StatusTone.Destructive;
            if (value.StartsWith("listening", StringComparison.Ordinal) || value.Contains("saved") || value.Contains("copied"))
                return StatusTone.Success;
            return StatusTone.Neutral;
        }
    }

    /// <summary>Quiet-hours text entry in 24-hour H:mm or HH:mm form.</summary>
    public static class QuietHoursText
    {
        public static string Format(TimeSpan value)
        {
            return value.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (text == null) return false;
            TimeSpan parsed;
            if (!TimeSpan.TryParseExact(text.Trim(), new string[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out parsed))
                return false;
            if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1)) return false;
            value = parsed;
            return true;
        }
    }
}
