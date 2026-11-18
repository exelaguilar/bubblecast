using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Bubblecast")]
[assembly: AssemblyProduct("Bubblecast")]
[assembly: AssemblyDescription("Private LAN notifications from your Mac's Messages app")]
[assembly: AssemblyCompany("Bubblecast")]

namespace Bubblecast
{
    // The tray receiver. Split across partial files: this file holds state, startup, settings storage,
    // status text, diagnostics, and shutdown; see RelayContext.Listener.cs, RelayContext.Notifications.cs,
    // and RelayContext.SettingsWindow.cs.
    internal sealed partial class RelayContext : ApplicationContext
    {
        private const int Port = 8765;
        private const string SettingsRegistryPath = @"Software\Bubblecast";
        private const int TlsPort = 8766;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _toastBatchTimer;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private readonly SemaphoreSlim _clientSlots = new SemaphoreSlim(4, 4);
        private readonly List<PendingNotification> _pendingNotifications = new List<PendingNotification>();
        private readonly SynchronizationContext _uiContext;
        private string _token;
        private readonly object _tokenLock = new object();
        private readonly EventWaitHandle _commandEvent;
        private readonly EventWaitHandle _exitEvent;
        private readonly string _tokenPath;
        private volatile TcpListener _listener;
        private volatile TcpListener _tlsListener;
        private X509Certificate2 _tlsCertificate;
        private string _tlsFingerprint = "TLS certificate unavailable";
        private volatile bool _running = true;
        private Form _settings;
        private string _listenerStatus = "Starting listener…";
        private Label _statusLabel;
        private Label _metricsLabel;
        private Label _pairingLabel;
        private bool _quietHoursEnabled;
        private TimeSpan _quietHoursStart = new TimeSpan(22, 0, 0);
        private TimeSpan _quietHoursEnd = new TimeSpan(7, 0, 0);
        private string _mutedSenders = "";
        private string _macRdpTarget = "";
        private string _lastMacAddress = "";
        private volatile bool _onlyAcceptPairedMacAddress;
        private volatile string _pairedMacAddress = "";
        private readonly long[] _rejectionCounts = new long[6];
        private readonly RequestVerifier _verifier;
        private long _notificationsReceived;
        private long _healthChecksReceived;
        private long _quietSuppressedCount;
        private long _senderSuppressedCount;
        private long _lastNotificationUtcTicks;
        private long _lastHealthCheckUtcTicks;
        private long _lastSignedRequestUtcTicks;
        private bool _macQuietAlertEnabled = true;
        private bool _macQuietAlerted;
        private bool _clickOpensMacDesktop;
        private bool _lastNotificationWasNotice;
        private volatile string _listenAddress = "";
        private int _listenerGeneration;
        private bool _discoverable = true;
        private readonly ServiceAnnouncer _announcer = new ServiceAnnouncer();
        private readonly List<RecentNotification> _recentNotifications = new List<RecentNotification>();
        private static readonly TimeSpan MacQuietThreshold = TimeSpan.FromMinutes(5);
        private const int RecentNotificationLimit = 8;

        private sealed class PendingNotification
        {
            public string Sender;
            public string Preview;
            public bool IsTest;
        }

        private sealed class RecentNotification
        {
            public DateTime Time;
            public string Title;
            public string Text;
        }

        public RelayContext(EventWaitHandle commandEvent, EventWaitHandle exitEvent, bool showSettingsOnLaunch)
        {
            _commandEvent = commandEvent;
            _exitEvent = exitEvent;
            _uiContext = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_uiContext);
            string dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bubblecast");
            Directory.CreateDirectory(dataDirectory);
            _tokenPath = Path.Combine(dataDirectory, "token.txt");
            _token = TokenFile.LoadOrCreate(_tokenPath);
            _verifier = new RequestVerifier(delegate { lock (_tokenLock) return _token; });
            InitializeTlsCertificate();
            LoadSettings();

            // Keep an existing sign-in shortcut but retarget it to this updated executable name.
            if (StartupShortcut.Exists())
            {
                try { StartupShortcut.EnsureCurrentTarget(); }
                catch { }
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Settings", null, delegate { ShowSettings(); });
            menu.Items.Add("Open Mac Remote Desktop", null, delegate { OpenMacDesktop(); });
            ToolStripMenuItem recent = new ToolStripMenuItem("Recent notifications");
            recent.DropDownItems.Add(new ToolStripMenuItem("No notifications yet") { Enabled = false });
            menu.Items.Add(recent);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            menu.Opening += delegate { FillRecentNotificationsMenu(recent); };

            _tray = new NotifyIcon();
            _tray.Icon = BrandAssets.AppIcon(SystemInformation.SmallIconSize) ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application;
            _tray.Text = "Bubblecast";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowSettings(); };
            _tray.BalloonTipClicked += delegate { OnNotificationClicked(); };

            Task.Factory.StartNew(delegate
            {
                while (_running)
                {
                    try
                    {
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { _exitEvent, _commandEvent }, 500);
                        if (signaled == 0)
                        {
                            QueueUiAction(delegate { ExitThread(); });
                            return;
                        }
                        if (signaled == 1)
                            QueueUiAction(ShowSettings);
                    }
                    catch (ObjectDisposedException) { return; }
                }
            }, TaskCreationOptions.LongRunning);

            _toastBatchTimer = new System.Windows.Forms.Timer();
            _toastBatchTimer.Interval = 900;
            _toastBatchTimer.Tick += delegate { FlushPendingNotifications(); };
            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 60000;
            _statusTimer.Tick += delegate { RefreshSettingsStatusLabels(); };
            _statusTimer.Start();

            Task.Factory.StartNew(ListenLoop, TaskCreationOptions.LongRunning);
            if (_tlsCertificate != null)
                Task.Factory.StartNew(ListenTlsLoop, TaskCreationOptions.LongRunning);
            UpdateAnnouncement();
            if (showSettingsOnLaunch)
                ShowSettings();
        }

        private void InitializeTlsCertificate()
        {
            const string subject = "CN=Bubblecast Local TLS";
            try
            {
                using (X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    X509Certificate2 existing = store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, false)
                        .OfType<X509Certificate2>()
                        .FirstOrDefault(delegate(X509Certificate2 candidate) { return candidate.HasPrivateKey && candidate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7); });
                    if (existing != null)
                        _tlsCertificate = existing;
                    else
                    {
                        using (RSA key = RSA.Create(2048))
                        {
                            CertificateRequest request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                            using (X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5)))
                            {
                                string temporaryPassword = Guid.NewGuid().ToString("N");
                                byte[] pfx = created.Export(X509ContentType.Pfx, temporaryPassword);
                                try
                                {
                                    X509Certificate2 persisted = new X509Certificate2(pfx, temporaryPassword, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                                    store.Add(persisted);
                                    _tlsCertificate = persisted;
                                }
                                finally { Array.Clear(pfx, 0, pfx.Length); }
                            }
                        }
                    }
                }
                using (SHA256 sha256 = SHA256.Create())
                    _tlsFingerprint = BitConverter.ToString(sha256.ComputeHash(_tlsCertificate.RawData)).Replace("-", "");
            }
            catch (Exception ex)
            {
                _tlsCertificate = null;
                _tlsFingerprint = "TLS unavailable: " + ex.GetType().Name;
                QueueStatus("Optional TLS listener could not initialize; HTTP remains available.");
            }
        }

        private void RotatePairingToken()
        {
            string replacement = TokenFile.NewToken();
            lock (_tokenLock)
            {
                TokenFile.Write(_tokenPath, replacement);
                _token = replacement;
            }
        }

        private void QueueUiAction(Action action)
        {
            _uiContext.Post(delegate(object state)
            {
                if (!_running) return;
                action();
                RefreshSettingsStatusLabels();
            }, null);
        }

        private void LoadSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsRegistryPath))
                {
                    if (key == null) return;
                    _quietHoursEnabled = Convert.ToInt32(key.GetValue("QuietHoursEnabled", 0), CultureInfo.InvariantCulture) != 0;
                    TimeSpan parsed;
                    if (TimeSpan.TryParse(Convert.ToString(key.GetValue("QuietHoursStart", "22:00"), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out parsed) && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
                        _quietHoursStart = parsed;
                    if (TimeSpan.TryParse(Convert.ToString(key.GetValue("QuietHoursEnd", "07:00"), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out parsed) && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
                        _quietHoursEnd = parsed;
                    _mutedSenders = Convert.ToString(key.GetValue("MutedSenders", ""), CultureInfo.InvariantCulture) ?? "";
                    _macRdpTarget = Convert.ToString(key.GetValue("MacRdpTarget", ""), CultureInfo.InvariantCulture) ?? "";
                    _onlyAcceptPairedMacAddress = Convert.ToInt32(key.GetValue("OnlyAcceptPairedMacAddress", 0), CultureInfo.InvariantCulture) != 0;
                    _pairedMacAddress = Convert.ToString(key.GetValue("PairedMacAddress", ""), CultureInfo.InvariantCulture) ?? "";
                    _lastMacAddress = _pairedMacAddress;
                    _macQuietAlertEnabled = Convert.ToInt32(key.GetValue("MacQuietAlert", 1), CultureInfo.InvariantCulture) != 0;
                    _clickOpensMacDesktop = Convert.ToInt32(key.GetValue("ClickOpensMacDesktop", 0), CultureInfo.InvariantCulture) != 0;
                    string listen = Convert.ToString(key.GetValue("ListenAddress", ""), CultureInfo.InvariantCulture) ?? "";
                    _listenAddress = NetworkAddresses.IsUsableListenAddress(listen) ? listen.Trim() : "";
                    _discoverable = Convert.ToInt32(key.GetValue("Discoverable", 1), CultureInfo.InvariantCulture) != 0;
                }
            }
            catch
            {
                _quietHoursEnabled = false;
                _mutedSenders = "";
                _macRdpTarget = "";
            }
        }

        /// <summary>Saves settings. Returns true when the listen address changed (listeners must restart).</summary>
        private bool SaveSettings(bool quietEnabled, TimeSpan quietStart, TimeSpan quietEnd, string mutedSenders, string macRdpTarget,
            bool onlyAcceptPairedMacAddress, string pairedMacAddress, bool macQuietAlert, bool clickOpensMacDesktop, string listenAddress, bool discoverable)
        {
            listenAddress = (listenAddress ?? "").Trim();
            if (!NetworkAddresses.IsUsableListenAddress(listenAddress))
                throw new InvalidOperationException("Listen address must be blank or one of this PC's IPv4 addresses.");
            _quietHoursEnabled = quietEnabled;
            _quietHoursStart = quietStart;
            _quietHoursEnd = quietEnd;
            _mutedSenders = mutedSenders ?? "";
            _macRdpTarget = macRdpTarget == null ? "" : macRdpTarget.Trim();
            if (onlyAcceptPairedMacAddress && String.IsNullOrWhiteSpace(pairedMacAddress))
                throw new InvalidOperationException("Wait for a signed request from the Mac before enabling the address allowlist.");
            _onlyAcceptPairedMacAddress = onlyAcceptPairedMacAddress;
            _pairedMacAddress = onlyAcceptPairedMacAddress ? pairedMacAddress.Trim() : "";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath))
            {
                key.SetValue("QuietHoursEnabled", _quietHoursEnabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("QuietHoursStart", _quietHoursStart.ToString(@"hh\:mm", CultureInfo.InvariantCulture), RegistryValueKind.String);
                key.SetValue("QuietHoursEnd", _quietHoursEnd.ToString(@"hh\:mm", CultureInfo.InvariantCulture), RegistryValueKind.String);
                key.SetValue("MutedSenders", _mutedSenders, RegistryValueKind.String);
                key.SetValue("MacRdpTarget", _macRdpTarget, RegistryValueKind.String);
                key.SetValue("OnlyAcceptPairedMacAddress", _onlyAcceptPairedMacAddress ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("PairedMacAddress", _pairedMacAddress, RegistryValueKind.String);
                key.SetValue("MacQuietAlert", macQuietAlert ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ClickOpensMacDesktop", clickOpensMacDesktop ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ListenAddress", listenAddress, RegistryValueKind.String);
                key.SetValue("Discoverable", discoverable ? 1 : 0, RegistryValueKind.DWord);
            }
            _macQuietAlertEnabled = macQuietAlert;
            if (!macQuietAlert) _macQuietAlerted = false;
            _clickOpensMacDesktop = clickOpensMacDesktop;
            bool listenChanged = !String.Equals(_listenAddress, listenAddress, StringComparison.Ordinal);
            bool discoveryChanged = _discoverable != discoverable;
            _listenAddress = listenAddress;
            _discoverable = discoverable;
            if (listenChanged || discoveryChanged) UpdateAnnouncement();
            return listenChanged;
        }

        private void QueueStatus(string status)
        {
            QueueUiAction(delegate { _listenerStatus = status; });
        }

        private void QueueLatestMacAddress(string address)
        {
            QueueUiAction(delegate { _lastMacAddress = address; });
        }

        private void RefreshSettingsStatusLabels()
        {
            if (_statusLabel != null && !_statusLabel.IsDisposed)
            {
                _statusLabel.Text = _listenerStatus;
                DotLabel statusDot = _statusLabel as DotLabel;
                if (statusDot != null) statusDot.Tone = StatusToneClassifier.Classify(_listenerStatus);
            }
            if (_metricsLabel != null && !_metricsLabel.IsDisposed)
                _metricsLabel.Text = GetMetricsText();
            if (_pairingLabel != null && !_pairingLabel.IsDisposed)
            {
                _pairingLabel.Text = GetPairingStatusText();
                BadgeLabel badge = _pairingLabel as BadgeLabel;
                if (badge != null) badge.Tone = GetPairingTone();
            }
            UpdateTrayStatus();
            CheckMacQuiet();
        }

        /// <summary>One notice when the Mac stops checking in (health checks arrive every minute).</summary>
        private void CheckMacQuiet()
        {
            long ticks = Interlocked.Read(ref _lastSignedRequestUtcTicks);
            if (!_macQuietAlertEnabled || _macQuietAlerted || ticks == 0) return;
            if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < MacQuietThreshold) return;
            _macQuietAlerted = true;
            ShowNotice("Mac went quiet", "Bubblecast hasn't heard from your Mac for 5 minutes. The Mac, or Bubblecast on it, may have stopped.");
        }

        private string GetPairingStatusText()
        {
            long ticks = Interlocked.Read(ref _lastSignedRequestUtcTicks);
            return ticks == 0 ? "Waiting for Mac" : "Paired · Mac seen " + FormatElapsed(ticks) + " ago";
        }

        /// <summary>The Mac health check runs every minute, so a quiet Mac for 5+ minutes deserves attention.</summary>
        private StatusTone GetPairingTone()
        {
            long ticks = Interlocked.Read(ref _lastSignedRequestUtcTicks);
            if (ticks == 0) return StatusTone.Neutral;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMinutes(5) ? StatusTone.Warning : StatusTone.Success;
        }

        private void UpdateTrayStatus()
        {
            if (_tray == null || !_tray.Visible) return;
            long ticks = Interlocked.Read(ref _lastSignedRequestUtcTicks);
            string tooltip = ticks == 0
                ? "Bubblecast · Waiting for Mac"
                : "Bubblecast · Mac seen " + FormatElapsed(ticks) + " ago";
            _tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private static string FormatElapsed(long ticks)
        {
            if (ticks == 0) return "never";
            TimeSpan elapsed = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (elapsed.TotalSeconds < 60) return "less than 1 min";
            if (elapsed.TotalMinutes < 60) return Math.Max(1, (int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            return Math.Max(1, (int)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + " hr";
        }

        private void ShowSettings()
        {
            if (_settings == null || _settings.IsDisposed)
                _settings = BuildSettingsWindow();
            _settings.Show();
            _settings.WindowState = FormWindowState.Normal;
            _settings.BringToFront();
            _settings.Activate();
        }

        private string GetMetricsText()
        {
            long notificationTicks = Interlocked.Read(ref _lastNotificationUtcTicks);
            long healthTicks = Interlocked.Read(ref _lastHealthCheckUtcTicks);
            string lastNotification = notificationTicks == 0 ? "never" : new DateTime(notificationTicks, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string lastHealth = healthTicks == 0 ? "never" : new DateTime(healthTicks, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            return "Notifications received: " + Interlocked.Read(ref _notificationsReceived).ToString(CultureInfo.InvariantCulture) +
                "  •  Health checks: " + Interlocked.Read(ref _healthChecksReceived).ToString(CultureInfo.InvariantCulture) +
                "  •  Suppressed by quiet hours: " + Interlocked.Read(ref _quietSuppressedCount).ToString(CultureInfo.InvariantCulture) +
                "  •  Muted senders: " + Interlocked.Read(ref _senderSuppressedCount).ToString(CultureInfo.InvariantCulture) +
                Environment.NewLine + "Last notification: " + lastNotification + "  •  Last health check: " + lastHealth +
                Environment.NewLine + "Current Mac IP: " + (String.IsNullOrEmpty(_lastMacAddress) ? "not seen yet" : _lastMacAddress);
        }

        private string GetDiagnosticsText()
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            long notificationTicks = Interlocked.Read(ref _lastNotificationUtcTicks);
            long healthTicks = Interlocked.Read(ref _lastHealthCheckUtcTicks);
            string lastNotification = notificationTicks == 0 ? "never" : new DateTime(notificationTicks, DateTimeKind.Utc).ToLocalTime().ToString("o", CultureInfo.InvariantCulture);
            string lastHealth = healthTicks == 0 ? "never" : new DateTime(healthTicks, DateTimeKind.Utc).ToLocalTime().ToString("o", CultureInfo.InvariantCulture);
            return String.Join(Environment.NewLine, new string[] {
                "Bubblecast Windows " + version,
                "Listener: " + (_listener != null ? "listening" : "not currently listening"),
                "Notifications received: " + Interlocked.Read(ref _notificationsReceived).ToString(CultureInfo.InvariantCulture),
                "Health checks: " + Interlocked.Read(ref _healthChecksReceived).ToString(CultureInfo.InvariantCulture),
                "Rejected bad signatures: " + Interlocked.Read(ref _rejectionCounts[(int)RequestRejectionReason.BadSignature - 1]).ToString(CultureInfo.InvariantCulture),
                "Rejected clock-skew requests: " + Interlocked.Read(ref _rejectionCounts[(int)RequestRejectionReason.ClockSkew - 1]).ToString(CultureInfo.InvariantCulture),
                "Rejected replays: " + Interlocked.Read(ref _rejectionCounts[(int)RequestRejectionReason.Replay - 1]).ToString(CultureInfo.InvariantCulture),
                "Rejected malformed requests: " + Interlocked.Read(ref _rejectionCounts[(int)RequestRejectionReason.Malformed - 1]).ToString(CultureInfo.InvariantCulture),
                "Rejected oversize requests: " + Interlocked.Read(ref _rejectionCounts[(int)RequestRejectionReason.Oversize - 1]).ToString(CultureInfo.InvariantCulture),
                "Rejected addresses outside paired Mac allowlist: " + Interlocked.Read(ref _rejectionCounts[(int)RequestRejectionReason.AddressNotAllowed - 1]).ToString(CultureInfo.InvariantCulture),
                "Last authenticated Mac clock skew in seconds: " + _verifier.LastClockSkewSeconds.ToString(CultureInfo.InvariantCulture),
                "Notifications suppressed by quiet hours: " + Interlocked.Read(ref _quietSuppressedCount).ToString(CultureInfo.InvariantCulture),
                "Notifications suppressed by sender filter: " + Interlocked.Read(ref _senderSuppressedCount).ToString(CultureInfo.InvariantCulture),
                "Last notification: " + lastNotification,
                "Last health check: " + lastHealth,
                "Quiet hours enabled: " + (_quietHoursEnabled ? "yes" : "no"),
                "Muted sender entries configured: " + _mutedSenders.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length.ToString(CultureInfo.InvariantCulture),
                "Mac desktop shortcut configured: " + (!String.IsNullOrWhiteSpace(_macRdpTarget) ? "yes" : "no")
            });
        }

        private static bool TryNormalizeRdpTarget(string input, out string normalized)
        {
            normalized = "";
            if (String.IsNullOrWhiteSpace(input)) return true;
            string value = input.Trim();
            string host = value;
            string portText = null;
            int colon = value.LastIndexOf(':');
            if (colon >= 0)
            {
                if (value.IndexOf(':') != colon) return false;
                host = value.Substring(0, colon);
                portText = value.Substring(colon + 1);
                int port;
                if (!Int32.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    return false;
            }
            IPAddress parsedAddress;
            bool validHost = IPAddress.TryParse(host, out parsedAddress) ||
                (Uri.CheckHostName(host) == UriHostNameType.Dns && host.Length <= 253 && !host.StartsWith(".") && !host.EndsWith("."));
            if (!validHost) return false;
            normalized = host + (portText == null ? "" : ":" + portText);
            return true;
        }

        private void OpenMacDesktop()
        {
            string target;
            if (String.IsNullOrWhiteSpace(_macRdpTarget) && !String.IsNullOrWhiteSpace(_lastMacAddress))
                target = _lastMacAddress + ":3390";
            else if (!TryNormalizeRdpTarget(_macRdpTarget, out target) || String.IsNullOrEmpty(target))
            {
                MessageBox.Show("Wait for a signed connection from the Mac, or enter a Mac Remote Desktop address in Settings.", "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "mstsc.exe", Arguments = "/v:" + target, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't open Remote Desktop: " + ex.Message, "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string GetLocalIPv4Addresses()
        {
            List<string> likelyLan = new List<string>();
            List<string> other = new List<string>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                bool hasDefaultGateway = nic.GetIPProperties().GatewayAddresses.Any(delegate(GatewayIPAddressInformation gateway)
                {
                    return gateway.Address != null && !IPAddress.Any.Equals(gateway.Address) && !IPAddress.IPv6Any.Equals(gateway.Address);
                });
                string interfaceName = nic.Name + " (" + nic.NetworkInterfaceType + ")";
                bool virtualOrTunnel = (nic.Name + " " + nic.Description).IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (nic.Name + " " + nic.Description).IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (nic.Name + " " + nic.Description).IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address) &&
                        !address.Address.ToString().StartsWith("169.254."))
                    {
                        string entry = interfaceName + ": " + address.Address +
                            (hasDefaultGateway ? " (likely LAN; default gateway)" : "") +
                            (virtualOrTunnel ? " (virtual or tunnel adapter)" : "");
                        (hasDefaultGateway && !virtualOrTunnel ? likelyLan : other).Add(entry);
                    }
                }
            }
            List<string> addresses = likelyLan.Concat(other).ToList();
            return addresses.Count == 0 ? "No active IPv4 address found." : String.Join(Environment.NewLine, addresses.ToArray());
        }

        /// <summary>Announces this PC to Macs on the network (or stops) to match the current settings.</summary>
        private void UpdateAnnouncement()
        {
            _announcer.Stop();
            if (!_discoverable) return;
            IPAddress address = null;
            if (!String.IsNullOrEmpty(_listenAddress)) IPAddress.TryParse(_listenAddress, out address);
            if (address == null) address = NetworkAddresses.PrimaryLanIPv4();
            _announcer.Start(address, Port, _tlsCertificate != null ? TlsPort : 0);
        }

        protected override void ExitThreadCore()
        {
            _running = false;
            _announcer.Dispose();
            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
            }
            if (_tlsListener != null)
            {
                try { _tlsListener.Stop(); } catch { }
            }
            _toastBatchTimer.Stop();
            _toastBatchTimer.Dispose();
            _statusTimer.Stop();
            _statusTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_tlsCertificate != null) _tlsCertificate.Dispose();
            if (_settings != null)
                _settings.Dispose();
            base.ExitThreadCore();
        }
    }
}
