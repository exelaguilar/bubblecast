using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Bubblecast
{
    public static class LaunchModePolicy
    {
        public static bool ShouldShowSettings(string[] launchArgs)
        {
            return launchArgs == null || !launchArgs.Any(argument => String.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static class Program
    {
        private const string MutexName = @"Local\Bubblecast.Receiver";
        private const string CommandEventName = @"Local\Bubblecast.Receiver.Command";
        private const string ExitEventName = @"Local\Bubblecast.Receiver.Exit";

        [STAThread]
        private static void Main(string[] launchArgs)
        {
            bool showSettings = LaunchModePolicy.ShouldShowSettings(launchArgs);
            string startupOption = launchArgs.FirstOrDefault(argument => argument.StartsWith("--set-startup=", StringComparison.OrdinalIgnoreCase));
            if (startupOption != null)
            {
                StartupShortcut.SetEnabled(startupOption.EndsWith("=on", StringComparison.OrdinalIgnoreCase));
                if (launchArgs.Length == 1) return;
            }
            bool createdNew;
            using (Mutex instance = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (EventWaitHandle command = EventWaitHandle.OpenExisting(CommandEventName)) command.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return;
                }
                using (EventWaitHandle command = new EventWaitHandle(false, EventResetMode.AutoReset, CommandEventName))
                using (EventWaitHandle exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
                {
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
                    {
                        MessageBox.Show("Bubblecast encountered a Windows error: " + args.Exception.Message, "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
                    {
                        Exception error = args.ExceptionObject as Exception;
                        MessageBox.Show("Bubblecast encountered a Windows error: " + (error == null ? "Unknown error." : error.Message), "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    };
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.Run(new RelayContext(command, exit, showSettings)); }
                    catch (InvalidDataException ex)
                    {
                        MessageBox.Show(ex.Message, "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                if (createdNew) instance.ReleaseMutex();
            }
        }
    }
}
