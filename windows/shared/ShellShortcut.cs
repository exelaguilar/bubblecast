using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bubblecast
{
    /// <summary>Reads and writes .lnk shortcuts through Windows Script Host. Used by the app and Setup.</summary>
    internal static class ShellShortcut
    {
        /// <summary>Creates or replaces a shortcut. <paramref name="iconLocation"/> may be null to use the target's icon.</summary>
        public static void Write(string path, string target, string workingDirectory, string description, string arguments, string iconLocation)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("Windows Script Host is unavailable, so a shortcut could not be created.");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? "" });
                if (!String.IsNullOrEmpty(iconLocation))
                    shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconLocation });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                Release(shortcut);
                Release(shell);
            }
        }

        /// <summary>One property of an existing shortcut, such as "TargetPath" or "Arguments"; null if unavailable.</summary>
        public static string ReadProperty(string path, string property)
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
                Release(shortcut);
                Release(shell);
            }
        }

        private static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
    }

    /// <summary>The arguments Setup starts the installed app with.</summary>
    public static class SetupLaunch
    {
        /// <summary>
        /// Always starts the app after installing, because Setup closes a running copy first. It opens
        /// Settings when asked to, and otherwise starts quietly in the notification area.
        /// </summary>
        public static string Arguments(bool startAtSignIn, bool openSettings)
        {
            return "--set-startup=" + (startAtSignIn ? "on" : "off") + (openSettings ? " --show-settings" : " --background");
        }
    }
}
