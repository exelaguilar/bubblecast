using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Bubblecast;

[assembly: AssemblyTitle("Bubblecast Setup")]
[assembly: AssemblyProduct("Bubblecast Setup")]

internal static class BubblecastSetup
{
    private const string AppName = "Bubblecast";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bubblecast";
    private const string ExitEventName = @"Local\Bubblecast.Receiver.Exit";
    private const int MoveFileDelayUntilReboot = 0x4;
    private static readonly string[] OwnedFileNames = { "Bubblecast.exe", "Uninstall Bubblecast.exe", "Bubblecast.ico" };

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool isUninstaller = String.Equals(Path.GetFileName(Application.ExecutablePath), "Uninstall Bubblecast.exe", StringComparison.OrdinalIgnoreCase);
        if (isUninstaller && (args.Length == 0 || String.Equals(args[0], "/uninstall", StringComparison.OrdinalIgnoreCase)))
            Uninstall();
        else if (args.Length > 0 && String.Equals(args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            MessageBox.Show("Run the installed Uninstall Bubblecast shortcut from the Start menu or Windows Settings.", AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            Application.Run(new InstallerForm());
    }

    private static string DefaultInstallDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    }

    private static void Install(string destination, bool createStartMenu, bool createDesktop, bool startAtSignIn, bool launchAfterInstall, Action<string, int> reportProgress)
    {
        destination = Path.GetFullPath(destination.Trim());
        ValidateInstallDestination(destination);
        Directory.CreateDirectory(destination);
        string appPath = Path.Combine(destination, "Bubblecast.exe");
        reportProgress("Checking for a running receiver…", 10);
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("Bubblecast"))
        {
            try
            {
                if (String.Equals(Path.GetFullPath(process.MainModule.FileName), appPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using (EventWaitHandle exit = EventWaitHandle.OpenExisting(ExitEventName)) exit.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    for (int attempt = 0; attempt < 30 && !process.HasExited; attempt++)
                    {
                        process.WaitForExit(500);
                        reportProgress("Asking Bubblecast to close before updating…", 10 + attempt * 2);
                    }
                    if (!process.HasExited)
                        throw new InvalidOperationException("Bubblecast is still running. Close it from the notification area, then choose Retry to continue the update.");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Setup could not verify the running receiver. Close any running Bubblecast app and retry so the installed file is not replaced while it is in use.", ex);
            }
            finally { process.Dispose(); }
        }
        reportProgress("Staging the new app safely…", 75);
        string stagedAppPath = appPath + ".new";
        string backupPath = appPath + ".previous";
        try
        {
            if (File.Exists(stagedAppPath)) File.Delete(stagedAppPath);
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("BubblecastSetup.Payload.exe"))
            {
                if (payload == null) throw new InvalidOperationException("The embedded app payload is missing.");
                using (FileStream output = File.Create(stagedAppPath)) payload.CopyTo(output);
            }
            if (File.Exists(appPath))
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Replace(stagedAppPath, appPath, backupPath);
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            else File.Move(stagedAppPath, appPath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(stagedAppPath)) File.Delete(stagedAppPath); } catch { }
            throw new IOException("The app could not be replaced safely. Close Bubblecast and retry. " + ex.Message, ex);
        }

        reportProgress("Creating shortcuts and registration…", 85);
        string uninstallPath = Path.Combine(destination, "Uninstall Bubblecast.exe");
        File.Copy(Application.ExecutablePath, uninstallPath, true);
        string iconPath = Path.Combine(destination, "Bubblecast.ico");
        using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bubblecast.AppIcon.ico"))
        {
            if (icon != null)
                using (FileStream output = File.Create(iconPath)) icon.CopyTo(output);
        }

        string menuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
        if (createStartMenu)
        {
            Directory.CreateDirectory(menuDirectory);
            CreateShortcut(Path.Combine(menuDirectory, AppName + ".lnk"), appPath, destination, "Open Bubblecast");
            CreateShortcut(Path.Combine(menuDirectory, "Uninstall Bubblecast.lnk"), uninstallPath, destination, "Uninstall Bubblecast", "/uninstall");
        }
        else
        {
            DeleteShortcut(Path.Combine(menuDirectory, AppName + ".lnk"));
            DeleteShortcut(Path.Combine(menuDirectory, "Uninstall Bubblecast.lnk"));
            RemoveDirectoryIfEmpty(menuDirectory);
        }
        string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");
        if (createDesktop) CreateShortcut(desktopPath, appPath, destination, "Open Bubblecast");
        else DeleteShortcut(desktopPath);

        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            key.SetValue("Publisher", AppName);
            key.SetValue("InstallLocation", destination);
            key.SetValue("DisplayIcon", appPath + ",0");
            key.SetValue("UninstallString", "\"" + uninstallPath + "\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        reportProgress("Finishing setup…", 95);
        string launchArguments = "--set-startup=" + (startAtSignIn ? "on" : "off") + (launchAfterInstall ? " --show-settings" : String.Empty);
        LaunchAsInteractiveUser(appPath, launchArguments);
        reportProgress("Installation complete.", 100);
    }

    private static void DeleteShortcut(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void RemoveDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path);
    }

    private static void LaunchAsInteractiveUser(string target, string arguments)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) throw new InvalidOperationException("Windows Explorer could not be reached to start the app in the signed-in user's session.");
        object shell = Activator.CreateInstance(shellType);
        try { shellType.InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell, new object[] { target, arguments, Path.GetDirectoryName(target), "open", 1 }); }
        finally { if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
    }

    private static void ValidateInstallDestination(string destination)
    {
        string full = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(Path.GetFullPath(destination)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (String.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a folder below the drive root for Bubblecast.");

        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a dedicated installation folder under Program Files. Elevated uninstallers must not run from a user-writable location.");

        Environment.SpecialFolder[] protectedFolders = {
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.Windows
        };
        foreach (Environment.SpecialFolder folder in protectedFolders)
        {
            string protectedPath = Environment.GetFolderPath(folder);
            if (!String.IsNullOrEmpty(protectedPath) && String.Equals(full, Path.GetFullPath(protectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a dedicated folder for Bubblecast instead of a Windows or profile folder.");
        }

        if (!Directory.Exists(full)) return;
        string[] entries = Directory.GetFileSystemEntries(full);
        bool containsOwnedFile = OwnedFileNames.Any(name => File.Exists(Path.Combine(full, name)));
        if (entries.Length > 0 && !containsOwnedFile)
            throw new InvalidOperationException("The selected folder is not empty and does not contain an existing Bubblecast installation. Choose an empty dedicated folder.");
    }

    private static void CreateShortcut(string path, string target, string workingDirectory, string description, string arguments = "")
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("Windows Script Host is unavailable; a shortcut could not be created.");
        object shell = Activator.CreateInstance(shellType);
        object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        Type shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
        shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        Marshal.ReleaseComObject(shortcut);
        Marshal.ReleaseComObject(shell);
    }

    private static void Uninstall()
    {
        string installDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        string registeredDirectory;
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UninstallKey))
            registeredDirectory = key == null ? null : key.GetValue("InstallLocation") as string;
        if (String.IsNullOrEmpty(registeredDirectory) ||
            !String.Equals(Path.GetFullPath(registeredDirectory), Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The installed location could not be verified. No files were removed.", AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string appPath = Path.Combine(installDirectory, "Bubblecast.exe");
        DialogResult answer = MessageBox.Show("Remove Bubblecast from this PC?\r\n\r\nYour pairing token and notification settings in your Windows profile will be kept.", AppName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("Bubblecast"))
        {
            try
            {
                if (String.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(appPath), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Exit Bubblecast from its notification-area icon, then run Uninstall again.", AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        try { LaunchAsInteractiveUser(appPath, "--set-startup=off"); }
        catch (Exception ex)
        {
            MessageBox.Show("The signed-in user's startup shortcut could not be removed safely. No files were removed. " + ex.Message, AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string menuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
        DeleteShortcut(Path.Combine(menuDirectory, AppName + ".lnk"));
        DeleteShortcut(Path.Combine(menuDirectory, "Uninstall Bubblecast.lnk"));
        RemoveDirectoryIfEmpty(menuDirectory);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);

        foreach (string name in OwnedFileNames)
        {
            string file = Path.Combine(installDirectory, name);
            if (!File.Exists(file)) continue;
            if (String.Equals(file, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                MoveFileEx(file, null, MoveFileDelayUntilReboot);
            else
                try { File.Delete(file); } catch { MoveFileEx(file, null, MoveFileDelayUntilReboot); }
        }
        try { Directory.Delete(installDirectory, false); } catch { }
        MessageBox.Show("Bubblecast has been removed. Windows will clear the running uninstaller file the next time the PC restarts.", AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingName, string newName, int flags);

    // ───────────────────────────── Setup window ─────────────────────────────
    // Built from the same controls and layout helpers as the receiver's Settings window
    // (windows/shared/BubblecastUi.cs), compiled into both executables.

    private sealed class InstallerForm : Form
    {
        private readonly TableLayoutPanel _root;
        private readonly TextBox _folder;
        private readonly Label _progressLabel;
        private readonly ProgressLine _progress;
        private readonly RelayButton _installButton;
        private readonly RelayButton _cancelButton;
        private readonly RelayButton _browseButton;
        private readonly RelaySwitch _startMenu;
        private readonly RelaySwitch _desktop;
        private readonly RelaySwitch _startup;
        private readonly RelaySwitch _launch;
        private RelayPalette _palette;
        private bool _installing;
        private bool _failed;

        public InstallerForm()
        {
            Font baseFont = new Font("Segoe UI", 9F);
            Font titleFont = new Font("Segoe UI Semibold", 14F);
            Font sectionFont = new Font("Segoe UI Semibold", 11F);
            Font labelFont = new Font("Segoe UI Semibold", 9F);
            Font smallFont = new Font("Segoe UI", 8.25F);

            Text = AppName + " Setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = baseFont;
            Icon = BrandAssets.AppIcon(Size.Empty) ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            ClientSize = new Size(600, 640);

            _root = UiLayout.NewStack();
            _root.Dock = DockStyle.Top;

            // Header: identity, matching the receiver's header.
            TableLayoutPanel header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(24, 18, 24, 18), Margin = Padding.Empty, Tag = UiRole.Surface };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            PictureBox appIcon = new PictureBox { Image = BrandAssets.Mark(UiPaint.Px(36)) ?? Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0), AccessibleRole = AccessibleRole.Graphic, AccessibleName = AppName };
            TableLayoutPanel titleStack = UiLayout.NewStack();
            titleStack.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            UiLayout.AddRow(titleStack, UiLayout.MakeText(AppName, UiRole.Body, titleFont, Padding.Empty));
            UiLayout.AddRow(titleStack, UiLayout.MakeText("Private LAN notifications from your Mac VM", UiRole.Muted, baseFont, Padding.Empty));
            BadgeLabel badge = new BadgeLabel { Text = "Setup · " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3), Font = labelFont, Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0), Tone = StatusTone.Neutral };
            header.Controls.Add(appIcon, 0, 0);
            header.Controls.Add(titleStack, 1, 0);
            header.Controls.Add(badge, 2, 0);
            UiLayout.AddRow(_root, header);
            UiLayout.AddRow(_root, new Hairline { Margin = Padding.Empty });

            // Body: two cards and a note.
            TableLayoutPanel body = UiLayout.NewStack();
            body.Padding = new Padding(24, 20, 24, 0);
            body.Tag = UiRole.Page;

            CardPanel location = UiLayout.NewCard("Install location", "Choose where " + AppName + " will be installed.", sectionFont, smallFont);
            _folder = new TextBox { Text = DefaultInstallDirectory(), AccessibleName = "Install folder" };
            _browseButton = new RelayButton("Browse…", ButtonKind.Secondary) { AccessibleName = "Choose the install folder" };
            _browseButton.Click += delegate { BrowseForFolder(); };
            UiLayout.AddRow(location, UiLayout.InlineRow(new FieldHost(_folder, 1), _browseButton));
            UiLayout.AddHelp(location, "Use a dedicated folder under Program Files. Updates replace the app in place.", smallFont);
            UiLayout.AddRow(body, location);

            CardPanel options = UiLayout.NewCard("Shortcuts and startup", "Choose shortcuts and what happens when you sign in.", sectionFont, smallFont);
            _startMenu = new RelaySwitch { Checked = true };
            _desktop = new RelaySwitch { Checked = false };
            _startup = new RelaySwitch { Checked = true };
            _launch = new RelaySwitch { Checked = true };
            UiLayout.AddRow(options, UiLayout.ActionRow("Start menu shortcuts", "Add app and uninstall links to Start.", _startMenu, labelFont, smallFont));
            UiLayout.AddSeparator(options);
            UiLayout.AddRow(options, UiLayout.ActionRow("Desktop shortcut", "Add an app shortcut to the desktop.", _desktop, labelFont, smallFont));
            UiLayout.AddSeparator(options);
            UiLayout.AddRow(options, UiLayout.ActionRow("Start at sign-in", "Start quietly in the notification area.", _startup, labelFont, smallFont));
            UiLayout.AddSeparator(options);
            UiLayout.AddRow(options, UiLayout.ActionRow("Open Settings after setup", "Show the pairing details when setup finishes.", _launch, labelFont, smallFont));
            UiLayout.AddRow(body, options);
            UiLayout.AddRow(body, UiLayout.MakeText("Setup asks a running " + AppName + " to close before replacing it. No firewall rule is added.", UiRole.Muted, smallFont, new Padding(2, 0, 2, 20)));
            UiLayout.AddRow(_root, body);

            // Footer: progress on the left, Cancel and Install on the right.
            UiLayout.AddRow(_root, new Hairline { Margin = Padding.Empty });
            TableLayoutPanel footer = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(24, 16, 24, 16), Margin = Padding.Empty, Tag = UiRole.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            TableLayoutPanel status = UiLayout.NewStack();
            status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            status.Margin = new Padding(0, 0, 16, 0);
            _progressLabel = new Label { Text = "Ready to install", AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Height = baseFont.Height + 2, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty, Tag = UiRole.Body, AccessibleName = "Setup status" };
            _progress = new ProgressLine { Height = 6, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 8, 0, 0), AccessibleName = "Setup progress" };
            UiLayout.AddRow(status, _progressLabel);
            UiLayout.AddRow(status, _progress);
            _cancelButton = new RelayButton("Cancel", ButtonKind.Secondary) { Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 8, 0) };
            _installButton = new RelayButton("Install", ButtonKind.Primary) { Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(_cancelButton, 1, 0);
            footer.Controls.Add(_installButton, 2, 0);
            UiLayout.AddRow(_root, footer);

            Controls.Add(_root);
            AcceptButton = _installButton;
            CancelButton = _cancelButton;

            // Setup runs modelessly (Application.Run), where a button's DialogResult does not close the
            // window. Close explicitly. The FormClosing guard still blocks closing during an install.
            _cancelButton.Click += delegate { Close(); };
            _installButton.Click += delegate { StartInstall(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args) { if (_installing) args.Cancel = true; };

            // Fit the window to its content (after DPI scaling).
            Load += delegate { ClientSize = new Size(ClientSize.Width, _root.Height); };
            Shown += delegate
            {
                ApplyTheme(RelayPalette.Current());
                _folder.Select(_folder.TextLength, 0);
                _installButton.Focus();
            };

            ApplyTheme(RelayPalette.Current());
            UserPreferenceChangedEventHandler themeChanged = delegate(object sender, UserPreferenceChangedEventArgs args)
            {
                if (args.Category != UserPreferenceCategory.General && args.Category != UserPreferenceCategory.Color && args.Category != UserPreferenceCategory.Accessibility && args.Category != UserPreferenceCategory.VisualStyle) return;
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) ApplyTheme(RelayPalette.Current()); });
            };
            SystemEvents.UserPreferenceChanged += themeChanged;
            FormClosed += delegate { SystemEvents.UserPreferenceChanged -= themeChanged; };
        }

        private void BrowseForFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where to install " + AppName;
                string selected = Path.GetDirectoryName(_folder.Text);
                dialog.SelectedPath = Directory.Exists(selected) ? selected : DefaultInstallDirectory();
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _folder.Text = String.Equals(Path.GetFileName(dialog.SelectedPath), AppName, StringComparison.OrdinalIgnoreCase)
                        ? dialog.SelectedPath
                        : Path.Combine(dialog.SelectedPath, AppName);
            }
        }

        private void StartInstall()
        {
            if (_installing) return;
            _installing = true;
            _failed = false;
            SetInputsEnabled(false);
            _progress.Value = 0;
            _progressLabel.Text = "Starting…";
            UpdateStatusColor();
            string destination = _folder.Text;
            bool createStartMenu = _startMenu.Checked;
            bool createDesktop = _desktop.Checked;
            bool startAtSignIn = _startup.Checked;
            bool launchAfterInstall = _launch.Checked;
            Task.Run(delegate
            {
                try
                {
                    Install(destination, createStartMenu, createDesktop, startAtSignIn, launchAfterInstall,
                        delegate(string message, int value)
                        {
                            PostToUi(delegate { _progressLabel.Text = message; _progress.Value = value; });
                        });
                    PostToUi(delegate
                    {
                        // Clear the in-progress guard first, or FormClosing cancels this Close().
                        _installing = false;
                        _progress.Value = 100;
                        _progressLabel.Text = "Installation complete.";
                        Close();
                    });
                }
                catch (Exception ex)
                {
                    PostToUi(delegate
                    {
                        _installing = false;
                        _failed = true;
                        SetInputsEnabled(true);
                        _installButton.Text = "Retry";
                        _progressLabel.Text = "Setup needs attention. Fix the issue below, then choose Retry.";
                        UpdateStatusColor();
                        MessageBox.Show(this, "Installation could not finish: " + ex.Message, AppName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        private void SetInputsEnabled(bool enabled)
        {
            foreach (Control control in new Control[] { _folder, _browseButton, _startMenu, _desktop, _startup, _launch, _installButton, _cancelButton })
                control.Enabled = enabled;
        }

        private void UpdateStatusColor()
        {
            if (_palette != null)
                _progressLabel.ForeColor = _failed ? _palette.Destructive : _palette.Foreground;
        }

        private void ApplyTheme(RelayPalette palette)
        {
            _palette = palette;
            UiLayout.ApplyTheme(this, palette);
            UpdateStatusColor();
        }
    }

}
