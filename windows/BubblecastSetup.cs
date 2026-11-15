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
        using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("BubblecastSetup.Icon.ico"))
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
    // Same design system as the receiver's Settings window (shadcn/ui zinc light/dark, rounded cards,
    // owner-drawn fields, switches, and buttons). Setup is compiled on its own, so these controls are
    // compact copies of the receiver's rather than shared code.

    private sealed class InstallerForm : Form
    {
        private const int DwmUseImmersiveDarkMode = 20;
        private readonly TableLayoutPanel _root;
        private readonly TextBox _folder;
        private readonly Label _progressLabel;
        private readonly SetupProgress _progress;
        private readonly SetupButton _installButton;
        private readonly SetupButton _cancelButton;
        private readonly SetupButton _browseButton;
        private readonly SetupSwitch _startMenu;
        private readonly SetupSwitch _desktop;
        private readonly SetupSwitch _startup;
        private readonly SetupSwitch _launch;
        private SetupPalette _palette;
        private bool _installing;
        private bool _failed;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

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
            Font = baseFont;
            Icon = LoadEmbeddedIcon() ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            ClientSize = new Size(600, 640);

            _root = Stack();
            _root.Dock = DockStyle.Top;

            // Header: identity, matching the receiver's header.
            TableLayoutPanel header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(24, 18, 24, 18), Margin = Padding.Empty, Tag = SetupRole.Surface };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            PictureBox appIcon = new PictureBox { Image = LoadBrandMark(36) ?? Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0), AccessibleRole = AccessibleRole.Graphic, AccessibleName = AppName };
            TableLayoutPanel titleStack = Stack();
            titleStack.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            AddRow(titleStack, MakeText(AppName, SetupRole.Body, titleFont, Padding.Empty));
            AddRow(titleStack, MakeText("Private LAN notifications from your Mac VM", SetupRole.Muted, baseFont, Padding.Empty));
            SetupBadge badge = new SetupBadge { Text = "Setup · " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3), Font = labelFont, Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
            header.Controls.Add(appIcon, 0, 0);
            header.Controls.Add(titleStack, 1, 0);
            header.Controls.Add(badge, 2, 0);
            AddRow(_root, header);
            AddRow(_root, new SetupHairline { Margin = Padding.Empty });

            // Body: two cards and a note.
            TableLayoutPanel body = Stack();
            body.Padding = new Padding(24, 20, 24, 0);
            body.Tag = SetupRole.Page;

            SetupCard location = NewCard("Install location", "Choose where Bubblecast will be installed.", sectionFont, smallFont);
            _folder = new TextBox { Text = DefaultInstallDirectory(), AccessibleName = "Install folder" };
            _browseButton = new SetupButton("Browse…", false) { AccessibleName = "Choose the install folder" };
            _browseButton.Click += delegate { BrowseForFolder(); };
            AddRow(location, InlineRow(new SetupField(_folder), _browseButton));
            AddRow(location, MakeText("Use a dedicated folder under Program Files. Updates replace the app in place.", SetupRole.Muted, smallFont, new Padding(0, 6, 0, 16)));
            AddRow(body, location);

            SetupCard options = NewCard("Shortcuts and startup", "Choose shortcuts and what happens when you sign in.", sectionFont, smallFont);
            _startMenu = new SetupSwitch { Checked = true };
            _desktop = new SetupSwitch { Checked = false };
            _startup = new SetupSwitch { Checked = true };
            _launch = new SetupSwitch { Checked = true };
            AddRow(options, ActionRow("Start menu shortcuts", "Add app and uninstall links to Start.", _startMenu, labelFont, smallFont));
            AddRow(options, new SetupHairline());
            AddRow(options, ActionRow("Desktop shortcut", "Add an app shortcut to the desktop.", _desktop, labelFont, smallFont));
            AddRow(options, new SetupHairline());
            AddRow(options, ActionRow("Start at sign-in", "Start quietly in the notification area.", _startup, labelFont, smallFont));
            AddRow(options, new SetupHairline());
            AddRow(options, ActionRow("Open Settings after setup", "Show the pairing details when setup finishes.", _launch, labelFont, smallFont));
            AddRow(body, options);
            AddRow(body, MakeText("Setup asks a running Bubblecast to close before replacing it. No firewall rule is added.", SetupRole.Muted, smallFont, new Padding(2, 0, 2, 20)));
            AddRow(_root, body);

            // Footer: progress on the left, Cancel and Install on the right.
            AddRow(_root, new SetupHairline { Margin = Padding.Empty });
            TableLayoutPanel footer = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(24, 16, 24, 16), Margin = Padding.Empty, Tag = SetupRole.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            TableLayoutPanel status = Stack();
            status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            status.Margin = new Padding(0, 0, 16, 0);
            _progressLabel = new Label { Text = "Ready to install", AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Height = baseFont.Height + 2, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty, Tag = SetupRole.Status, AccessibleName = "Setup status" };
            _progress = new SetupProgress { Height = 6, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 8, 0, 0), AccessibleName = "Setup progress" };
            AddRow(status, _progressLabel);
            AddRow(status, _progress);
            _cancelButton = new SetupButton("Cancel", false) { Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 8, 0) };
            _installButton = new SetupButton("Install", true) { Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(_cancelButton, 1, 0);
            footer.Controls.Add(_installButton, 2, 0);
            AddRow(_root, footer);

            Controls.Add(_root);
            AcceptButton = _installButton;
            CancelButton = _cancelButton;

            // Setup runs modelessly (Application.Run), where a button's DialogResult does not close the
            // window. Close explicitly. The FormClosing guard still blocks closing during an install.
            _cancelButton.Click += delegate { Close(); };
            _installButton.Click += delegate { StartInstall(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args) { if (_installing) args.Cancel = true; };

            // Fit the window to its content.
            Load += delegate { ClientSize = new Size(ClientSize.Width, _root.Height); };
            Shown += delegate
            {
                ApplyTheme(SetupPalette.Current());
                _folder.Select(_folder.TextLength, 0);
                _installButton.Focus();
            };

            ApplyTheme(SetupPalette.Current());
            UserPreferenceChangedEventHandler themeChanged = delegate(object sender, UserPreferenceChangedEventArgs args)
            {
                if (args.Category != UserPreferenceCategory.General && args.Category != UserPreferenceCategory.Color && args.Category != UserPreferenceCategory.Accessibility && args.Category != UserPreferenceCategory.VisualStyle) return;
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) ApplyTheme(SetupPalette.Current()); });
            };
            SystemEvents.UserPreferenceChanged += themeChanged;
            FormClosed += delegate { SystemEvents.UserPreferenceChanged -= themeChanged; };
        }

        /// <summary>The embedded multi-size icon, so the title bar uses native frames.</summary>
        private static Icon LoadEmbeddedIcon()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BubblecastSetup.Icon.ico"))
                    return stream == null ? null : new Icon(stream);
            }
            catch (Exception) { return null; }
        }

        /// <summary>The 256 px brand mark, downscaled with high-quality resampling (never upscaled).</summary>
        private static Image LoadBrandMark(int pixels)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BubblecastSetup.BrandMark.png"))
                {
                    if (stream == null) return null;
                    using (Bitmap source = new Bitmap(stream))
                    {
                        Bitmap result = new Bitmap(pixels, pixels);
                        using (Graphics graphics = Graphics.FromImage(result))
                        {
                            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            graphics.DrawImage(source, 0, 0, pixels, pixels);
                        }
                        return result;
                    }
                }
            }
            catch (Exception) { return null; }
        }

        private void BrowseForFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where to install Bubblecast";
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

        private void ApplyTheme(SetupPalette palette)
        {
            _palette = palette;
            SuspendLayout();
            BackColor = palette.Page;
            ForeColor = palette.Foreground;
            foreach (Control child in Controls)
                ApplyPalette(child, palette, palette.Page);
            UpdateStatusColor();
            ResumeLayout(true);
            if (IsHandleCreated)
            {
                int dark = palette.UseDarkChrome ? 1 : 0;
                try { DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)); }
                catch (Exception) { }
            }
            Invalidate(true);
        }

        private static void ApplyPalette(Control control, SetupPalette palette, Color inherited)
        {
            Color childBackground;
            ISetupThemed themed = control as ISetupThemed;
            if (themed != null)
                childBackground = themed.ApplyPalette(palette, inherited);
            else
            {
                SetupRole role = control.Tag is SetupRole ? (SetupRole)control.Tag : SetupRole.None;
                childBackground = role == SetupRole.Page ? palette.Page : role == SetupRole.Surface ? palette.Card : inherited;
                control.BackColor = childBackground;
                control.ForeColor = role == SetupRole.Muted ? palette.MutedForeground : palette.Foreground;
            }
            foreach (Control child in control.Controls)
                ApplyPalette(child, palette, childBackground);
        }

        private static TableLayoutPanel Stack()
        {
            TableLayoutPanel stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Padding = Padding.Empty, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return stack;
        }

        private static void AddRow(TableLayoutPanel table, Control control)
        {
            int row = table.RowStyles.Count;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowCount = row + 1;
            table.Controls.Add(control, 0, row);
        }

        private static Label MakeText(string text, SetupRole role, Font font, Padding margin)
        {
            return new Label { Text = text, AutoSize = true, Font = font, Tag = role, UseMnemonic = false, Margin = margin, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        }

        private static SetupCard NewCard(string title, string description, Font titleFont, Font descriptionFont)
        {
            SetupCard card = new SetupCard();
            AddRow(card, MakeText(title, SetupRole.Body, titleFont, new Padding(0, 0, 0, 2)));
            AddRow(card, MakeText(description, SetupRole.Muted, descriptionFont, new Padding(0, 0, 0, 16)));
            return card;
        }

        private static TableLayoutPanel InlineRow(Control fill, Control trailing)
        {
            TableLayoutPanel row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fill.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            fill.Margin = Padding.Empty;
            trailing.Anchor = AnchorStyles.None;
            trailing.Margin = new Padding(8, 0, 0, 0);
            row.Controls.Add(fill, 0, 0);
            row.Controls.Add(trailing, 1, 0);
            return row;
        }

        /// <summary>Title and description on the left, the switch on the right; clicking the text toggles it.</summary>
        private static TableLayoutPanel ActionRow(string title, string description, SetupSwitch toggle, Font titleFont, Font descriptionFont)
        {
            TableLayoutPanel row = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 12), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label titleLabel = MakeText(title, SetupRole.Body, titleFont, new Padding(0, 0, 0, 2));
            Label descriptionLabel = MakeText(description, SetupRole.Muted, descriptionFont, Padding.Empty);
            toggle.Anchor = AnchorStyles.Right;
            toggle.Margin = new Padding(16, 0, 0, 0);
            toggle.AccessibleName = title;
            toggle.AccessibleDescription = description;
            row.Controls.Add(titleLabel, 0, 0);
            row.Controls.Add(descriptionLabel, 0, 1);
            row.Controls.Add(toggle, 1, 0);
            row.SetRowSpan(toggle, 2);
            EventHandler flip = delegate
            {
                if (!toggle.Enabled) return;
                toggle.Checked = !toggle.Checked;
                toggle.Focus();
            };
            titleLabel.Click += flip;
            descriptionLabel.Click += flip;
            return row;
        }
    }

    private enum SetupRole { None, Page, Surface, Body, Muted, Status }

    private interface ISetupThemed
    {
        /// <summary>Applies the palette and returns the background its children sit on.</summary>
        Color ApplyPalette(SetupPalette palette, Color inherited);
    }

    private sealed class SetupPalette
    {
        public bool IsDark;
        public bool IsHighContrast;
        public Color Page, Card, Muted, Border, Input, Ring, Foreground, MutedForeground, Primary, PrimaryForeground, Destructive;

        public bool UseDarkChrome { get { return IsDark && !IsHighContrast; } }

        public static SetupPalette Current()
        {
            if (SystemInformation.HighContrast)
            {
                Color window = SystemColors.Window;
                Color text = SystemColors.WindowText;
                return new SetupPalette { IsHighContrast = true, Page = window, Card = window, Muted = window, Border = text, Input = text, Ring = SystemColors.Highlight, Foreground = text, MutedForeground = text, Primary = SystemColors.Highlight, PrimaryForeground = SystemColors.HighlightText, Destructive = text };
            }
            bool dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    dark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { }
            return dark ? Dark() : Light();
        }

        public static SetupPalette Light()
        {
            return new SetupPalette { Page = Hex(0xFAFAFA), Card = Hex(0xFFFFFF), Muted = Hex(0xF4F4F5), Border = Hex(0xE4E4E7), Input = Hex(0xD4D4D8), Ring = Hex(0xA1A1AA), Foreground = Hex(0x09090B), MutedForeground = Hex(0x71717A), Primary = Hex(0x18181B), PrimaryForeground = Hex(0xFAFAFA), Destructive = Hex(0xB91C1C) };
        }

        public static SetupPalette Dark()
        {
            return new SetupPalette { IsDark = true, Page = Hex(0x09090B), Card = Hex(0x111113), Muted = Hex(0x1C1C1F), Border = Hex(0x27272A), Input = Hex(0x3F3F46), Ring = Hex(0x71717A), Foreground = Hex(0xFAFAFA), MutedForeground = Hex(0xA1A1AA), Primary = Hex(0xFAFAFA), PrimaryForeground = Hex(0x18181B), Destructive = Hex(0xF87171) };
        }

        private static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
    }

    private static class SetupPaint
    {
        public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 1f) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        public static Color Outer(Control control)
        {
            return control.Parent != null ? control.Parent.BackColor : control.BackColor;
        }

        /// <summary>Every owner-drawn control fills its whole rectangle first, so stale pixels never show through.</summary>
        public static void FillOuter(Graphics graphics, Control control)
        {
            using (SolidBrush brush = new SolidBrush(Outer(control)))
                graphics.FillRectangle(brush, control.ClientRectangle);
        }

        public static void FocusRing(Graphics graphics, RectangleF bounds, float radius, Color color)
        {
            using (System.Drawing.Drawing2D.GraphicsPath ring = RoundedRect(RectangleF.Inflate(bounds, 1.5f, 1.5f), radius + 1.5f))
            using (Pen pen = new Pen(color, 2f))
                graphics.DrawPath(pen, ring);
        }
    }

    private sealed class SetupHairline : Control, ISetupThemed
    {
        public SetupHairline()
        {
            Height = 1;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Margin = new Padding(0, 0, 0, 12);
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            BackColor = palette.Border;
            return palette.Border;
        }
    }

    /// <summary>A single-column layout drawn as a rounded, bordered card. Children sit on the card color.</summary>
    private sealed class SetupCard : TableLayoutPanel, ISetupThemed
    {
        private SetupPalette _palette;

        public SetupCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Padding = new Padding(20, 18, 20, 4);
            Margin = new Padding(0, 0, 0, 16);
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = palette.Card;
            Invalidate();
            return palette.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            SetupPaint.FillOuter(e.Graphics, this);
            if (_palette == null) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10f))
            using (SolidBrush fill = new SolidBrush(_palette.Card))
            using (Pen border = new Pen(_palette.Border))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    /// <summary>Hosts a borderless TextBox inside a rounded input frame with a focus ring.</summary>
    private sealed class SetupField : Panel, ISetupThemed
    {
        private readonly TextBox _box;
        private SetupPalette _palette;

        public SetupField(TextBox box)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            _box = box;
            box.BorderStyle = BorderStyle.None;
            Controls.Add(box);
            Height = box.Font.Height + 20;
            Cursor = Cursors.IBeam;
            box.GotFocus += delegate { Invalidate(); };
            box.LostFocus += delegate { Invalidate(); };
            box.EnabledChanged += delegate { UpdateBoxColors(); Invalidate(); };
            Click += delegate { if (_box.CanFocus) _box.Focus(); };
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            UpdateBoxColors();
            Invalidate();
            return FieldBackground;
        }

        private Color FieldBackground
        {
            get { return _palette == null ? SystemColors.Window : (_box.Enabled ? _palette.Card : _palette.Muted); }
        }

        private void UpdateBoxColors()
        {
            if (_palette == null) return;
            _box.BackColor = FieldBackground;
            _box.ForeColor = _palette.Foreground;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _box.Width = Math.Max(1, Width - 20);
            _box.Location = new Point(10, Math.Max(0, (Height - _box.Height) / 2));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            SetupPaint.FillOuter(e.Graphics, this);
            if (_palette == null) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(2f, 2f, Width - 5f, Height - 5f);
            bool focused = _box.Focused;
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(bounds, 6f))
            using (SolidBrush fill = new SolidBrush(FieldBackground))
            using (Pen border = new Pen(focused ? _palette.Ring : _palette.Input))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            if (focused)
                SetupPaint.FocusRing(e.Graphics, bounds, 6f, SetupPaint.Blend(_palette.Ring, SetupPaint.Outer(this), 0.45f));
        }
    }

    private sealed class SetupButton : Button, ISetupThemed
    {
        private readonly bool _primary;
        private SetupPalette _palette;
        private bool _hover;
        private bool _pressed;

        public SetupButton(string text, bool primary)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            _primary = primary;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return new Size(Math.Max(text.Width + 32, 88), Math.Max(text.Height + 16, 36));
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            ForeColor = _primary ? palette.PrimaryForeground : palette.Foreground;
            Invalidate();
            return inherited;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics graphics = pevent.Graphics;
            SetupPaint.FillOuter(graphics, this);
            SetupPalette palette = _palette ?? SetupPalette.Light();
            Color outer = SetupPaint.Outer(this);
            Color fill;
            Color text;
            Color border = Color.Empty;
            if (_primary)
            {
                fill = _pressed ? SetupPaint.Blend(palette.Primary, outer, 0.25f) : _hover ? SetupPaint.Blend(palette.Primary, outer, 0.12f) : palette.Primary;
                text = palette.PrimaryForeground;
                if (palette.IsHighContrast) border = palette.Border;
            }
            else
            {
                fill = _pressed ? palette.Border : _hover ? palette.Muted : palette.Card;
                text = palette.Foreground;
                border = palette.Input;
            }
            if (!Enabled)
            {
                fill = SetupPaint.Blend(fill, outer, 0.5f);
                text = SetupPaint.Blend(text, outer, 0.5f);
                if (border != Color.Empty) border = SetupPaint.Blend(border, outer, 0.5f);
            }
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(2f, 2f, Width - 5f, Height - 5f);
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(bounds, 6f))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                graphics.FillPath(brush, path);
                if (border != Color.Empty)
                    using (Pen pen = new Pen(border)) graphics.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
                SetupPaint.FocusRing(graphics, bounds, 6f, palette.Ring);
            TextRenderer.DrawText(graphics, Text, Font, Rectangle.Round(bounds), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>A CheckBox drawn as a switch; keyboard, accessibility, and Checked behavior are unchanged.</summary>
    private sealed class SetupSwitch : CheckBox, ISetupThemed
    {
        private SetupPalette _palette;
        private bool _hover;

        public SetupSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Text = String.Empty;
            Size = new Size(44, 28);
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            Invalidate();
            return inherited;
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnMouseEnter(EventArgs eventargs) { _hover = true; Invalidate(); base.OnMouseEnter(eventargs); }
        protected override void OnMouseLeave(EventArgs eventargs) { _hover = false; Invalidate(); base.OnMouseLeave(eventargs); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics graphics = pevent.Graphics;
            SetupPaint.FillOuter(graphics, this);
            SetupPalette palette = _palette ?? SetupPalette.Light();
            Color outer = SetupPaint.Outer(this);
            float trackWidth = Math.Min(36f, Width - 6f);
            float trackHeight = trackWidth * 20f / 36f;
            RectangleF track = new RectangleF((Width - trackWidth) / 2f, (Height - trackHeight) / 2f, trackWidth, trackHeight);
            Color trackColor = Checked ? palette.Primary : (_hover ? SetupPaint.Blend(palette.Input, palette.Ring, 0.35f) : palette.Input);
            Color knobColor = Checked ? palette.PrimaryForeground : palette.Card;
            if (!Enabled)
            {
                trackColor = SetupPaint.Blend(trackColor, outer, 0.5f);
                knobColor = SetupPaint.Blend(knobColor, outer, 0.5f);
            }
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(track, trackHeight / 2f))
            using (SolidBrush brush = new SolidBrush(trackColor))
            {
                graphics.FillPath(brush, path);
                if (palette.IsHighContrast)
                    using (Pen pen = new Pen(palette.Border)) graphics.DrawPath(pen, path);
            }
            float knob = trackHeight - 4f;
            float knobX = Checked ? track.Right - 2f - knob : track.X + 2f;
            using (SolidBrush brush = new SolidBrush(knobColor))
                graphics.FillEllipse(brush, knobX, track.Y + 2f, knob, knob);
            if (Focused && ShowFocusCues)
                SetupPaint.FocusRing(graphics, track, trackHeight / 2f, palette.Ring);
        }
    }

    /// <summary>A neutral pill label, matching the receiver's pairing badge.</summary>
    private sealed class SetupBadge : Label, ISetupThemed
    {
        private SetupPalette _palette;

        public SetupBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = true;
            UseMnemonic = false;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return new Size(text.Width + 22, text.Height + 10);
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            Invalidate();
            return inherited;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            SetupPaint.FillOuter(e.Graphics, this);
            SetupPalette palette = _palette ?? SetupPalette.Light();
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(bounds, bounds.Height / 2f))
            using (SolidBrush fill = new SolidBrush(palette.Muted))
            using (Pen border = new Pen(palette.Border))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, palette.Foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    private sealed class SetupProgress : Control, ISetupThemed
    {
        private SetupPalette _palette;
        private int _value;

        public SetupProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            TabStop = false;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                _value = Math.Max(0, Math.Min(100, value));
                AccessibleDescription = _value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
                Invalidate();
            }
        }

        public Color ApplyPalette(SetupPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            Invalidate();
            return inherited;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            SetupPaint.FillOuter(e.Graphics, this);
            if (_palette == null) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            RectangleF track = new RectangleF(0f, 0f, Width - 1f, Height - 1f);
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(track, track.Height / 2f))
            using (SolidBrush brush = new SolidBrush(_palette.Muted))
                e.Graphics.FillPath(brush, path);
            float fillWidth = track.Width * _value / 100f;
            if (fillWidth <= 0f) return;
            RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, fillWidth), track.Height);
            using (System.Drawing.Drawing2D.GraphicsPath path = SetupPaint.RoundedRect(fill, track.Height / 2f))
            using (SolidBrush brush = new SolidBrush(_palette.Primary))
                e.Graphics.FillPath(brush, path);
        }
    }
}
