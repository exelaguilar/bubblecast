using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("Windows Script Host is unavailable; create a Startup shortcut manually.");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Bubblecast: iMessage notifications from your Mac" });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--background" });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        private static string ReadArguments(string path)
        {
            return ReadShortcutProperty(path, "Arguments");
        }

        private static string ReadTarget(string path)
        {
            return ReadShortcutProperty(path, "TargetPath");
        }

        private static string ReadShortcutProperty(string path, string property)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                return Convert.ToString(shortcut.GetType().InvokeMember(property, BindingFlags.GetProperty, null, shortcut, null));
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
    }
}
