using System;
using System.IO;
using System.Windows.Forms;

namespace Bubblecast
{
    public static class StartupShortcutPolicy
    {
        /// <summary>Repair the sign-in shortcut when it exists but no longer points at an existing app.</summary>
        public static bool ShouldRetarget(bool shortcutExists, bool targetExists)
        {
            return shortcutExists && !targetExists;
        }
    }

    internal static class StartupShortcut
    {
        private static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Bubblecast.lnk"); }
        }

        internal static bool Exists()
        {
            return File.Exists(ShortcutPath);
        }

        internal static void EnsureCurrentTarget()
        {
            if (!File.Exists(ShortcutPath)) return;
            string target = ReadTarget(ShortcutPath);
            if (StartupShortcutPolicy.ShouldRetarget(true, !String.IsNullOrWhiteSpace(target) && File.Exists(target)) ||
                !String.Equals(ReadArguments(ShortcutPath), "--background", StringComparison.OrdinalIgnoreCase))
                SetEnabled(true);
        }

        internal static void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return;
            }

            ShellShortcut.Write(ShortcutPath, Application.ExecutablePath, AppDomain.CurrentDomain.BaseDirectory,
                "Bubblecast: iMessage notifications from your Mac", "--background", null);
        }

        private static string ReadArguments(string path)
        {
            return ShellShortcut.ReadProperty(path, "Arguments");
        }

        private static string ReadTarget(string path)
        {
            return ShellShortcut.ReadProperty(path, "TargetPath");
        }
    }
}

