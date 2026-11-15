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
    internal sealed class RelayContext : ApplicationContext
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
        private long _notificationsReceived;
        private long _healthChecksReceived;
        private long _quietSuppressedCount;
        private long _senderSuppressedCount;
        private long _lastNotificationUtcTicks;
        private long _lastHealthCheckUtcTicks;
        private long _lastSignedRequestUtcTicks;
        private readonly long _processStartedUnixSeconds = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        private long _lastClockSkewSeconds;
        private readonly Dictionary<string, long> _recentNonces = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _recentMessageIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private sealed class PendingNotification
        {
            public string Sender;
            public string Preview;
            public bool IsTest;
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
            _token = LoadOrCreateToken(_tokenPath);
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
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            _tray = new NotifyIcon();
            _tray.Icon = BrandAssets.AppIcon(SystemInformation.SmallIconSize) ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application;
            _tray.Text = "Bubblecast";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowSettings(); };
            _tray.BalloonTipClicked += delegate { ShowSettings(); };

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
            if (showSettingsOnLaunch)
                ShowSettings();
        }

        private static string LoadOrCreateToken(string path)
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path, Encoding.ASCII);
                if (!TokenValidation.IsValid(existing))
                    throw new InvalidDataException("The pairing token file is empty or invalid. Replace token.txt with a 64-character hexadecimal token, or remove it to generate a new pairing token.");
                return existing;
            }

            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            string token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            string temporaryPath = path + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            File.WriteAllText(temporaryPath, token, Encoding.ASCII);
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
            return token;
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
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string replacement = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            string temporaryPath = _tokenPath + ".tmp";
            File.WriteAllText(temporaryPath, replacement, Encoding.ASCII);
            lock (_tokenLock)
            {
                File.Replace(temporaryPath, _tokenPath, null);
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
                }
            }
            catch
            {
                _quietHoursEnabled = false;
                _mutedSenders = "";
                _macRdpTarget = "";
            }
        }

        private void SaveSettings(bool quietEnabled, TimeSpan quietStart, TimeSpan quietEnd, string mutedSenders, string macRdpTarget, bool onlyAcceptPairedMacAddress, string pairedMacAddress)
        {
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
            }
        }

        private void ListenLoop()
        {
            int retryDelayMs = 1000;
            while (_running)
            {
                TcpListener listener = new TcpListener(IPAddress.Any, Port);
                try
                {
                    listener.Start();
                    _listener = listener;
                    retryDelayMs = 1000;
                    QueueStatus("Listening on TCP " + Port);
                    while (_running)
                    {
                        TcpClient client = null;
                        try
                        {
                            client = listener.AcceptTcpClient();
                            if (!_clientSlots.Wait(0))
                            {
                                WriteResponse(client.GetStream(), "503 Service Unavailable");
                                client.Close();
                                client = null;
                            }
                            else
                            {
                                TcpClient accepted = client;
                                client = null;
                                Task.Factory.StartNew(delegate
                                {
                                    try { HandleClient(accepted, false); }
                                    catch (SocketException)
                                    {
                                        if (_running) QueueStatus("A relay connection ended unexpectedly; the listener is still running.");
                                    }
                                    catch (Exception)
                                    {
                                        if (_running) QueueStatus("A relay request could not be processed; the listener is still running.");
                                    }
                                    finally
                                    {
                                        accepted.Close();
                                        _clientSlots.Release();
                                    }
                                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                            }
                        }
                        catch (SocketException)
                        {
                            if (_running)
                                throw;
                        }
                        finally
                        {
                            if (client != null)
                                client.Close();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        QueueStatus("Listener unavailable; retrying in " + (retryDelayMs / 1000) + " sec: " + ex.Message);
                        Thread.Sleep(retryDelayMs);
                        retryDelayMs = Math.Min(retryDelayMs * 2, 30000);
                    }
                }
                finally
                {
                    try { listener.Stop(); } catch { }
                    if (Object.ReferenceEquals(_listener, listener))
                        _listener = null;
                }
            }
        }

        private void ListenTlsLoop()
        {
            int retryDelayMs = 1000;
            while (_running && _tlsCertificate != null)
            {
                TcpListener listener = new TcpListener(IPAddress.Any, TlsPort);
                try
                {
                    listener.Start();
                    _tlsListener = listener;
                    retryDelayMs = 1000;
                    QueueStatus("Listening on TCP " + Port + " (HTTP) and " + TlsPort + " (TLS)");
                    while (_running)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        if (!_clientSlots.Wait(0)) { client.Close(); continue; }
                        TcpClient accepted = client;
                        Task.Factory.StartNew(delegate
                        {
                            try { HandleClient(accepted, true); }
                            catch (Exception) { if (_running) QueueStatus("A TLS relay connection failed; the listener remains available."); }
                            finally { accepted.Close(); _clientSlots.Release(); }
                        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                    }
                }
                catch (Exception)
                {
                    if (_running)
                    {
                        QueueStatus("TLS listener unavailable; HTTP remains available.");
                        Thread.Sleep(retryDelayMs);
                        retryDelayMs = Math.Min(retryDelayMs * 2, 30000);
                    }
                }
                finally
                {
                    try { listener.Stop(); } catch { }
                    if (Object.ReferenceEquals(_tlsListener, listener)) _tlsListener = null;
                }
            }
        }

        private void HandleClient(TcpClient client, bool useTls)
        {
            Stream stream = client.GetStream();
            SslStream secureStream = null;
            if (useTls)
            {
                secureStream = new SslStream(stream, false);
                secureStream.AuthenticateAsServer(_tlsCertificate, false, SslProtocols.Tls12, false);
                stream = secureStream;
            }
            try { HandleClientStream(client, stream); }
            finally { if (secureStream != null) secureStream.Dispose(); }
        }

        private void HandleClientStream(TcpClient client, Stream stream)
        {
            string status = "404 Not Found";
            IPEndPoint remoteEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (_onlyAcceptPairedMacAddress && (remoteEndpoint == null || !String.Equals(remoteEndpoint.Address.ToString(), _pairedMacAddress, StringComparison.OrdinalIgnoreCase)))
            {
                RecordRejection(RequestRejectionReason.AddressNotAllowed);
                WriteResponse(stream, "403 Forbidden");
                return;
            }
            ParsedHttpRequest parsed;
            HttpRequestReadStatus readStatus = BoundedHttpRequestReader.Read(stream, delegate(int timeout) { client.ReceiveTimeout = timeout; stream.ReadTimeout = timeout; }, out parsed);
            if (readStatus != HttpRequestReadStatus.Success)
            {
                if (readStatus == HttpRequestReadStatus.HeadersTooLarge)
                {
                    status = "431 Request Header Fields Too Large";
                    RecordRejection(RequestRejectionReason.Oversize);
                }
                else if (readStatus == HttpRequestReadStatus.Timeout)
                {
                    status = "408 Request Timeout";
                }
                else
                {
                    status = "400 Bad Request";
                    RecordRejection(RequestRejectionReason.Malformed);
                }
            }
            else
            {
                string[] parts = parsed.RequestLine.Split(new char[] { ' ' }, 3);
                if (parts.Length >= 2 &&
                    ((parts[0] == "POST" && parts[1] == "/notify") || (parts[0] == "GET" && parts[1] == "/health")))
                {
                    string path = parts[1];
                    string senderBase64;
                    string previewBase64;
                    if (!parsed.Headers.TryGetValue("X-Sender-Base64", out senderBase64)) senderBase64 = "";
                    if (!parsed.Headers.TryGetValue("X-Preview-Base64", out previewBase64)) previewBase64 = "";
                    string kind = "message", messageId = "", errorCode = null;
                    bool isV2 = false, duplicateMessage = false;
                    RequestRejectionReason rejection = senderBase64.Length > 1024 || previewBase64.Length > 4096
                        ? RequestRejectionReason.Oversize
                        : ValidateSignature(parts[0], path, parsed.Headers, senderBase64, previewBase64, out kind, out messageId, out errorCode, out isV2, out duplicateMessage);
                    if (rejection != RequestRejectionReason.None)
                    {
                        status = "403 Forbidden";
                        errorCode = rejection == RequestRejectionReason.ClockSkew ? "clock-skew" : rejection == RequestRejectionReason.Replay ? "replay" : "auth";
                        RecordRejection(rejection);
                    }
                    else
                    {
                        status = "204 No Content";
                        RecordAcceptedRequest(path);
                        IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                        if (remote != null && remote.Address.AddressFamily == AddressFamily.InterNetwork)
                            QueueLatestMacAddress(remote.Address.ToString());
                        if (path == "/notify" && !duplicateMessage)
                            QueueToast(DecodeHeader(senderBase64, 80), DecodeHeader(previewBase64, 180), kind == "test");
                        errorCode = null;
                    }
                    WriteResponse(stream, status, errorCode);
                    return;
                }
            }

            WriteResponse(stream, status);
        }

        private static void WriteResponse(Stream stream, string status, string errorCode = null)
        {
            string serverVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            string headers = "HTTP/1.1 " + status + "\r\nX-Relay-Server-Version: " + serverVersion + "\r\n";
            if (!String.IsNullOrEmpty(errorCode)) headers += "X-Relay-Error: " + errorCode + "\r\n";
            byte[] response = Encoding.ASCII.GetBytes(headers + "Content-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.Flush();
        }

        private enum RequestRejectionReason { None = 0, BadSignature = 1, ClockSkew = 2, Replay = 3, Malformed = 4, Oversize = 5, AddressNotAllowed = 6 }

        private RequestRejectionReason ValidateSignature(string method, string path, Dictionary<string, string> headers, string senderBase64, string previewBase64,
            out string kind, out string messageId, out string errorCode, out bool isV2, out bool duplicateMessage)
        {
            kind = "message";
            messageId = "";
            errorCode = "auth";
            duplicateMessage = false;
            string version;
            isV2 = headers.TryGetValue("X-Relay-Version", out version) && version == "2";
            if (headers.ContainsKey("X-Relay-Version") && !isV2) return RequestRejectionReason.Malformed;
            string timestampText;
            string nonce;
            string suppliedSignature;
            long timestamp;
            Guid parsedNonce;
            if (!headers.TryGetValue("X-Relay-Time", out timestampText) ||
                !headers.TryGetValue("X-Relay-Nonce", out nonce) ||
                !headers.TryGetValue("X-Relay-Signature", out suppliedSignature) ||
                !Int64.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp) ||
                !Guid.TryParse(nonce, out parsedNonce) || suppliedSignature.Length != 64)
                return RequestRejectionReason.Malformed;

            string canonical;
            if (isV2)
            {
                if (!headers.TryGetValue("X-Relay-Kind", out kind) ||
                    !headers.TryGetValue("X-Relay-Message-Id", out messageId) ||
                    (kind != "message" && kind != "test" && kind != "health") ||
                    (path == "/health" && kind != "health") ||
                    (path == "/notify" && kind == "health") ||
                    (kind == "message" && !IsOpaqueMessageId(messageId)) ||
                    (kind != "message" && messageId.Length != 0))
                    return RequestRejectionReason.Malformed;
                canonical = RelayProtocolV2.Canonical(method, path, timestampText, nonce, kind, messageId, senderBase64, previewBase64);
            }
            else canonical = RelayProtocolV1.Canonical(path, timestampText, nonce, senderBase64, previewBase64);
            string token;
            lock (_tokenLock) token = _token;
            string expectedSignature = RelayProtocolV1.Signature(token, canonical);
            if (!ConstantTimeEquals(expectedSignature, suppliedSignature.ToLowerInvariant()))
                return RequestRejectionReason.BadSignature;

            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            if (timestamp < now - 120 || timestamp > now + 120)
            {
                decimal measuredSkew = (decimal)timestamp - now;
                long skew = measuredSkew < Int64.MinValue ? Int64.MinValue : measuredSkew > Int64.MaxValue ? Int64.MaxValue : (long)measuredSkew;
                Interlocked.Exchange(ref _lastClockSkewSeconds, skew);
                errorCode = "clock-skew";
                return RequestRejectionReason.ClockSkew;
            }
            if (timestamp < _processStartedUnixSeconds)
                return RequestRejectionReason.Replay;

            lock (_recentNonces)
            {
                long cutoffTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;
                foreach (string oldNonce in _recentNonces.Where(delegate(KeyValuePair<string, long> item) { return item.Value < cutoffTicks; }).Select(delegate(KeyValuePair<string, long> item) { return item.Key; }).ToArray())
                    _recentNonces.Remove(oldNonce);
                if (_recentNonces.ContainsKey(nonce))
                {
                    errorCode = "replay";
                    return RequestRejectionReason.Replay;
                }
                _recentNonces[nonce] = DateTime.UtcNow.Ticks;
            }
            if (isV2 && kind == "message")
            {
                lock (_recentMessageIds)
                {
                    long cutoffTicks = DateTime.UtcNow.AddMinutes(-10).Ticks;
                    foreach (string oldId in _recentMessageIds.Where(delegate(KeyValuePair<string, long> item) { return item.Value < cutoffTicks; }).Select(delegate(KeyValuePair<string, long> item) { return item.Key; }).ToArray())
                        _recentMessageIds.Remove(oldId);
                    duplicateMessage = _recentMessageIds.ContainsKey(messageId);
                    _recentMessageIds[messageId] = DateTime.UtcNow.Ticks;
                }
            }
            errorCode = null;
            return RequestRejectionReason.None;
        }

        private static bool IsOpaqueMessageId(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private void RecordAcceptedRequest(string path)
        {
            long now = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _lastSignedRequestUtcTicks, now);
            if (path == "/notify")
            {
                Interlocked.Increment(ref _notificationsReceived);
                Interlocked.Exchange(ref _lastNotificationUtcTicks, now);
            }
            else
            {
                Interlocked.Increment(ref _healthChecksReceived);
                Interlocked.Exchange(ref _lastHealthCheckUtcTicks, now);
            }
            QueueUiAction(delegate { UpdateTrayStatus(); });
        }

        private void RecordRejection(RequestRejectionReason reason)
        {
            Interlocked.Increment(ref _rejectionCounts[(int)reason - 1]);
            string description;
            switch (reason)
            {
                case RequestRejectionReason.BadSignature: description = "bad signature"; break;
                case RequestRejectionReason.ClockSkew:
                    long skew = Interlocked.Read(ref _lastClockSkewSeconds);
                    description = "Mac clock differs by " + Math.Abs((decimal)skew).ToString("0", CultureInfo.InvariantCulture) + " seconds (" + (skew < 0 ? "behind" : "ahead") + ")";
                    break;
                case RequestRejectionReason.Replay: description = "replayed nonce"; break;
                case RequestRejectionReason.Oversize: description = "oversize request"; break;
                case RequestRejectionReason.AddressNotAllowed: description = "request from an address outside the paired Mac allowlist"; break;
                default: description = "malformed request"; break;
            }
            QueueStatus("Rejected a request from the Mac: " + description + ".");
        }

        private static bool ConstantTimeEquals(string left, string right)
        {
            int difference = left.Length ^ right.Length;
            int count = Math.Max(left.Length, right.Length);
            for (int index = 0; index < count; index++)
            {
                char leftValue = index < left.Length ? left[index] : '\0';
                char rightValue = index < right.Length ? right[index] : '\0';
                difference |= leftValue ^ rightValue;
            }
            return difference == 0;
        }

        private static string DecodeHeader(string encoded, int maximumLength)
        {
            if (String.IsNullOrEmpty(encoded)) return null;
            try
            {
                string value = DisplayTextSanitizer.SanitizeAndTruncate(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), maximumLength);
                return value.Length == 0 ? null : value;
            }
            catch (FormatException) { return null; }
        }

        private void QueueToast(string sender, string preview, bool isTest = false)
        {
            QueueUiAction(delegate
            {
                if (!isTest && IsQuietHoursNow())
                {
                    Interlocked.Increment(ref _quietSuppressedCount);
                    return;
                }
                if (!isTest && IsSenderMuted(sender))
                {
                    Interlocked.Increment(ref _senderSuppressedCount);
                    return;
                }
                _pendingNotifications.Add(isTest
                    ? new PendingNotification { Sender = "Test from Mac ✓", Preview = "Test notification accepted.", IsTest = true }
                    : new PendingNotification { Sender = sender, Preview = preview });
                if (!_toastBatchTimer.Enabled)
                    _toastBatchTimer.Start();
            });
        }

        private bool IsQuietHoursNow()
        {
            if (!_quietHoursEnabled || _quietHoursStart == _quietHoursEnd)
                return false;
            TimeSpan now = DateTime.Now.TimeOfDay;
            return _quietHoursStart < _quietHoursEnd
                ? now >= _quietHoursStart && now < _quietHoursEnd
                : now >= _quietHoursStart || now < _quietHoursEnd;
        }

        private bool IsSenderMuted(string sender)
        {
            if (String.IsNullOrWhiteSpace(sender) || String.IsNullOrWhiteSpace(_mutedSenders))
                return false;
            return _mutedSenders.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(delegate(string value) { return value.Trim(); })
                .Any(delegate(string value) { return value.Length > 0 && String.Equals(value, sender.Trim(), StringComparison.OrdinalIgnoreCase); });
        }

        private void FlushPendingNotifications()
        {
            _toastBatchTimer.Stop();
            if (_pendingNotifications.Count == 0) return;

            int count = _pendingNotifications.Count;
            List<PendingNotification> batch = _pendingNotifications.ToList();
            _pendingNotifications.Clear();
            if (count == 1 && batch[0].IsTest)
            {
                _tray.BalloonTipTitle = "Test from Mac ✓";
                _tray.BalloonTipText = "Test notification accepted.";
            }
            else if (count == 1)
            {
                PendingNotification item = batch[0];
                _tray.BalloonTipTitle = String.IsNullOrEmpty(item.Sender)
                    ? "iMessage"
                    : "iMessage — " + DisplayTextSanitizer.SanitizeAndTruncate(item.Sender, 40);
                _tray.BalloonTipText = !String.IsNullOrEmpty(item.Preview)
                    ? item.Preview
                    : (String.IsNullOrEmpty(item.Sender) ? "New message received" : "New message from " + item.Sender);
            }
            else
            {
                List<string> senders = batch.Select(delegate(PendingNotification item) { return item.Sender; })
                    .Where(delegate(string item) { return !String.IsNullOrWhiteSpace(item); })
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
                _tray.BalloonTipTitle = count.ToString(CultureInfo.InvariantCulture) + " new iMessages";
                _tray.BalloonTipText = senders.Count == 0
                    ? "New messages received"
                    : "From " + String.Join(", ", senders.ToArray()) + (senders.Count < batch.Select(delegate(PendingNotification item) { return item.Sender; }).Where(delegate(string item) { return !String.IsNullOrWhiteSpace(item); }).Distinct(StringComparer.OrdinalIgnoreCase).Count() ? ", and others" : "");
            }
            _tray.BalloonTipIcon = ToolTipIcon.None;
            _tray.ShowBalloonTip(5000);
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

        private Form BuildSettingsWindow()
        {
            Font baseFont = new Font("Segoe UI", 9F);
            Font titleFont = new Font("Segoe UI Semibold", 14F);
            Font sectionFont = new Font("Segoe UI Semibold", 11F);
            Font labelFont = new Font("Segoe UI Semibold", 9F);
            Font smallFont = new Font("Segoe UI", 8.25F);
            Font monoFont = new Font("Consolas", 9.5F);

            Form form = new Form();
            form.Text = "Bubblecast";
            form.Icon = BrandAssets.AppIcon(Size.Empty) ?? _tray.Icon;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.MaximizeBox = true;
            form.MinimizeBox = false;
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.Font = baseFont;
            // Wide enough for Pairing and Notifications side by side, so the default view needs no scrolling.
            form.ClientSize = new Size(1080, 780);
            form.MinimumSize = new Size(560, 520);
            form.Load += delegate
            {
                // Keep the DPI-scaled window inside the screen's working area, then center it.
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
                form.Location = new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2);
            };

            // ── Header: identity and live pairing badge (always visible) ──
            Panel header = new Panel { Dock = DockStyle.Top, Height = 72, Tag = UiRole.Surface };
            TableLayoutPanel headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(24, 0, 24, 0), Margin = Padding.Empty };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            PictureBox logo = new PictureBox
            {
                Image = BrandAssets.Mark(UiPaint.Px(36)) ?? _tray.Icon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(36, 36),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 12, 0),
                AccessibleRole = AccessibleRole.Graphic,
                AccessibleName = "Bubblecast"
            };
            TableLayoutPanel titleStack = NewStack();
            titleStack.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            AddRow(titleStack, MakeText("Bubblecast", UiRole.Body, titleFont, Padding.Empty));
            AddRow(titleStack, MakeText("Private LAN notifications from your Mac VM", UiRole.Muted, baseFont, Padding.Empty));
            BadgeLabel pairing = new BadgeLabel { Name = "PairingText", Font = labelFont, Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0), AccessibleName = "Mac pairing status" };
            pairing.Text = GetPairingStatusText();
            pairing.Tone = GetPairingTone();
            _pairingLabel = pairing;
            headerLayout.Controls.Add(logo, 0, 0);
            headerLayout.Controls.Add(titleStack, 1, 0);
            headerLayout.Controls.Add(pairing, 2, 0);
            header.Controls.Add(headerLayout);
            header.Controls.Add(new Hairline { Dock = DockStyle.Bottom });

            // ── Pairing card: everything the Mac needs, above the fold ──
            CardPanel pairingCard = NewCard("Pairing", "Enter these values in Bubblecast on the Mac.", sectionFont, smallFont);

            string addressText = GetLocalIPv4Addresses();
            int addressLines = Math.Max(1, addressText.Split('\n').Length);
            TextBox ips = new TextBox
            {
                ReadOnly = true,
                Multiline = true,
                WordWrap = false,
                ScrollBars = addressLines > 4 ? ScrollBars.Vertical : ScrollBars.None,
                Text = addressText,
                AccessibleName = "This PC network addresses"
            };
            AddLabel(pairingCard, "This PC's address", labelFont);
            AddRow(pairingCard, new FieldHost(ips, Math.Min(addressLines, 4)));
            NativeTheme.FollowAppTheme(ips);
            AddHelp(pairingCard, "Adapters with a default gateway are listed first as likely LAN addresses.", smallFont);

            TextBox tokenBox = new TextBox { ReadOnly = true, UseSystemPasswordChar = true, Text = _token, Font = monoFont, AccessibleName = "Pairing token" };
            RelayButton showToken = new RelayButton("Show", ButtonKind.Secondary) { AccessibleName = "Show or hide the pairing token" };
            showToken.Click += delegate
            {
                tokenBox.UseSystemPasswordChar = !tokenBox.UseSystemPasswordChar;
                showToken.Text = tokenBox.UseSystemPasswordChar ? "Show" : "Hide";
            };

            RelayButton copy = new RelayButton("Copy", ButtonKind.Secondary) { AccessibleName = "Copy pairing token" };
            System.Windows.Forms.Timer copyResetTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            copyResetTimer.Tick += delegate { copyResetTimer.Stop(); copy.Text = "Copy"; };
            string clipboardTokenToClear = null;
            System.Windows.Forms.Timer clearTokenTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            clearTokenTimer.Tick += delegate
            {
                clearTokenTimer.Stop();
                try
                {
                    if (!String.IsNullOrEmpty(clipboardTokenToClear) && Clipboard.ContainsText() &&
                        String.Equals(Clipboard.GetText(), clipboardTokenToClear, StringComparison.Ordinal))
                        Clipboard.Clear();
                }
                catch (ExternalException) { }
                clipboardTokenToClear = null;
            };
            copy.Click += delegate
            {
                try
                {
                    DataObject tokenData = new DataObject();
                    tokenData.SetText(_token);
                    tokenData.SetData("ExcludeClipboardContentFromMonitorProcessing", true);
                    Clipboard.SetDataObject(tokenData, true);
                    clipboardTokenToClear = _token;
                    clearTokenTimer.Stop();
                    clearTokenTimer.Start();
                    copy.Text = "Copied";
                    copyResetTimer.Stop();
                    copyResetTimer.Start();
                }
                catch (ExternalException)
                {
                    QueueStatus("The clipboard is busy. Try copying the pairing token again.");
                }
            };
            AddLabel(pairingCard, "Pairing token", labelFont);
            AddRow(pairingCard, InlineRow(new FieldHost(tokenBox, 1), showToken, copy));
            AddHelp(pairingCard, "Keep this private. The Mac signs every request with it. Copies clear from the clipboard after one minute.", smallFont);

            TextBox tlsFingerprint = new TextBox { Text = _tlsFingerprint, ReadOnly = true, Font = monoFont, AccessibleName = "TLS certificate SHA-256 fingerprint" };
            RelayButton copyFingerprint = new RelayButton("Copy", ButtonKind.Secondary) { AccessibleName = "Copy TLS certificate fingerprint" };
            System.Windows.Forms.Timer fingerprintResetTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            fingerprintResetTimer.Tick += delegate { fingerprintResetTimer.Stop(); copyFingerprint.Text = "Copy"; };
            copyFingerprint.Click += delegate
            {
                try
                {
                    Clipboard.SetText(_tlsFingerprint);
                    QueueStatus("TLS certificate fingerprint copied.");
                    copyFingerprint.Text = "Copied";
                    fingerprintResetTimer.Stop();
                    fingerprintResetTimer.Start();
                }
                catch (ExternalException) { QueueStatus("The clipboard is busy. Try copying the TLS fingerprint again."); }
            };
            AddLabel(pairingCard, "TLS certificate fingerprint · optional", labelFont);
            AddRow(pairingCard, InlineRow(new FieldHost(tlsFingerprint, 1), copyFingerprint));
            AddHelp(pairingCard, "SHA-256. Paste it into the Mac app to pin TLS on port 8766. HTTP on port 8765 stays available for rollback.", smallFont);

            AddSeparator(pairingCard);
            RelayButton openMac = new RelayButton("Open Mac desktop", ButtonKind.Secondary) { AccessibleName = "Open Mac Remote Desktop" };
            openMac.Click += delegate { OpenMacDesktop(); };
            AddRow(pairingCard, ActionRow("Mac Remote Desktop", "Connects to the last Mac relay IP on port 3390, or to the override in Advanced.", openMac, labelFont, smallFont));

            // ── Notifications card: routine choices ──
            CardPanel notifyCard = NewCard("Notifications", "Stored for this Windows account. Changes apply when you save.", sectionFont, smallFont);

            RelaySwitch quietHours = new RelaySwitch { Checked = _quietHoursEnabled };
            AddRow(notifyCard, ActionRow("Quiet hours", "Suppress notifications during these hours. Messages are not queued.", quietHours, labelFont, smallFont));

            TextBox quietStart = new TextBox { Text = QuietHoursText.Format(_quietHoursStart), MaxLength = 5, TextAlign = HorizontalAlignment.Center, AccessibleName = "Quiet hours start, 24-hour HH:mm" };
            TextBox quietEnd = new TextBox { Text = QuietHoursText.Format(_quietHoursEnd), MaxLength = 5, TextAlign = HorizontalAlignment.Center, AccessibleName = "Quiet hours end, 24-hour HH:mm" };
            TableLayoutPanel quietRow = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            for (int column = 0; column < 4; column++) quietRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            quietRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            quietRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label fromLabel = MakeText("From", UiRole.Muted, baseFont, new Padding(0, 0, 8, 0));
            Label untilLabel = MakeText("until", UiRole.Muted, baseFont, new Padding(12, 0, 8, 0));
            fromLabel.Anchor = AnchorStyles.Left;
            untilLabel.Anchor = AnchorStyles.Left;
            FieldHost quietStartHost = new FieldHost(quietStart, 1) { PreferredWidth = 72, Anchor = AnchorStyles.Left };
            FieldHost quietEndHost = new FieldHost(quietEnd, 1) { PreferredWidth = 72, Anchor = AnchorStyles.Left };
            quietRow.Controls.Add(fromLabel, 0, 0);
            quietRow.Controls.Add(quietStartHost, 1, 0);
            quietRow.Controls.Add(untilLabel, 2, 0);
            quietRow.Controls.Add(quietEndHost, 3, 0);
            AddRow(notifyCard, quietRow);
            AddHelp(notifyCard, "24-hour time on this PC's clock. For overnight quiet hours, set From later than until.", smallFont);

            AddSeparator(notifyCard);
            // The muted list grows with its content (3–8 lines) so it normally needs no scroll bar of its
            // own; only a list longer than 8 lines gets one, themed like the page scroll bar.
            const int mutedMinimumLines = 3;
            const int mutedMaximumLines = 8;
            int mutedLineCount = _mutedSenders.Split('\n').Length;
            TextBox mutedSenders = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = mutedLineCount > mutedMaximumLines ? ScrollBars.Vertical : ScrollBars.None,
                Text = _mutedSenders,
                AccessibleName = "Muted sender names"
            };
            FieldHost mutedHost = new FieldHost(mutedSenders, Math.Min(Math.Max(mutedLineCount, mutedMinimumLines), mutedMaximumLines));
            mutedSenders.TextChanged += delegate
            {
                int lines = mutedSenders.Lines.Length;
                mutedHost.VisibleLines = Math.Min(Math.Max(lines, mutedMinimumLines), mutedMaximumLines);
                if (lines > mutedMaximumLines && mutedSenders.ScrollBars != ScrollBars.Vertical)
                    mutedSenders.ScrollBars = ScrollBars.Vertical;
            };
            NativeTheme.FollowAppTheme(mutedSenders);
            AddLabel(notifyCard, "Muted contacts", labelFont);
            AddRow(notifyCard, mutedHost);
            AddHelp(notifyCard, "One name per line. A name must exactly match the sender shown in alerts.", smallFont);

            AddSeparator(notifyCard);
            RelaySwitch startup = new RelaySwitch { Checked = StartupShortcut.Exists() };
            EventHandler startupChanged = null;
            startupChanged = delegate
            {
                try { StartupShortcut.SetEnabled(startup.Checked); }
                catch (Exception ex)
                {
                    MessageBox.Show("Couldn't update the sign-in setting: " + ex.Message, "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    startup.CheckedChanged -= startupChanged;
                    startup.Checked = !startup.Checked;
                    startup.CheckedChanged += startupChanged;
                }
            };
            startup.CheckedChanged += startupChanged;
            AddRow(notifyCard, ActionRow("Start when I sign in to Windows", "Opens quietly in the notification area. Applies immediately.", startup, labelFont, smallFont));
            AddSeparator(notifyCard);
            AddRow(notifyCard, MakeText("Message previews sent by the Mac may appear in Windows notification history.", UiRole.Muted, smallFont, new Padding(0, 0, 0, 16)));

            // ── Advanced: infrequent maintenance and diagnostics, collapsed by default ──
            CardPanel advancedCard = new CardPanel { Padding = new Padding(8) };
            DisclosureButton advancedToggle = new DisclosureButton("Advanced", "Remote Desktop override, address allowlist, token rotation, and diagnostics", labelFont, smallFont);
            TableLayoutPanel advancedBody = NewStack();
            advancedBody.Padding = new Padding(12, 0, 12, 4);
            advancedBody.Visible = false;
            Action<bool> setAdvancedExpanded = delegate(bool expanded)
            {
                advancedBody.Visible = expanded;
                advancedToggle.Expanded = expanded;
            };
            advancedToggle.Click += delegate { setAdvancedExpanded(!advancedBody.Visible); };
            AddRow(advancedCard, advancedToggle);
            AddRow(advancedCard, advancedBody);

            AddSeparator(advancedBody);
            TextBox rdpTarget = new TextBox { Text = _macRdpTarget, AccessibleName = "Mac Remote Desktop address" };
            AddLabel(advancedBody, "Mac Remote Desktop address", labelFont);
            AddRow(advancedBody, new FieldHost(rdpTarget, 1));
            AddHelp(advancedBody, "Optional, such as mac-vm.local:3390. Leave blank to use the current Mac relay IP on port 3390.", smallFont);

            AddSeparator(advancedBody);
            RelaySwitch allowPairedMacOnly = new RelaySwitch { Checked = _onlyAcceptPairedMacAddress, AccessibleName = "Restrict requests to the paired Mac IP address" };
            AddRow(advancedBody, ActionRow("Only accept requests from the paired Mac IP", "Uses the last Mac IP that sent a signed request. Applies when you save.", allowPairedMacOnly, labelFont, smallFont));

            AddSeparator(advancedBody);
            RelayButton rotateToken = new RelayButton("Generate new token…", ButtonKind.Secondary) { AccessibleName = "Generate a new pairing token" };
            rotateToken.Click += delegate
            {
                DialogResult answer = MessageBox.Show(
                    "Generate a new pairing token? The Mac must be updated with the new token before notifications can continue.",
                    "Bubblecast", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
                try
                {
                    RotatePairingToken();
                    tokenBox.Text = _token;
                    QueueStatus("Pairing token changed. Update the Mac token to restore notifications.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Couldn't generate a new pairing token: " + ex.Message, "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            AddRow(advancedBody, ActionRow("Rotate pairing token", "Replaces the token immediately. Notifications stop until the Mac has the new token.", rotateToken, labelFont, smallFont));

            AddSeparator(advancedBody);
            AddLabel(advancedBody, "Diagnostics", labelFont);
            Label metrics = MakeText(GetMetricsText(), UiRole.Muted, smallFont, new Padding(0, 0, 0, 12));
            metrics.Name = "MetricsText";
            metrics.AccessibleName = "Notification and health metrics";
            _metricsLabel = metrics;
            AddRow(advancedBody, metrics);
            RelayButton diagnostics = new RelayButton("Copy privacy-safe diagnostics", ButtonKind.Secondary);
            diagnostics.Margin = Padding.Empty;
            diagnostics.Anchor = AnchorStyles.Left;
            diagnostics.Click += delegate
            {
                try
                {
                    Clipboard.SetText(GetDiagnosticsText());
                    QueueStatus("Privacy-safe diagnostics copied; no token, addresses, sender names, or message text included.");
                }
                catch (ExternalException)
                {
                    QueueStatus("The clipboard is busy. Try copying diagnostics again.");
                }
            };
            AddRow(advancedBody, diagnostics);
            AddHelp(advancedBody, "Diagnostics exclude the pairing token, addresses, sender names, and message text.", smallFont);

            // ── Footer: live receiver status and the persistent Save action ──
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 64, Tag = UiRole.Surface };
            TableLayoutPanel footerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(24, 0, 24, 0), Margin = Padding.Empty };
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            DotLabel status = new DotLabel { Name = "StatusText", Text = _listenerStatus, Dock = DockStyle.Fill, AccessibleName = "Receiver status" };
            status.Tone = StatusToneClassifier.Classify(_listenerStatus);
            _statusLabel = status;
            Label unsaved = MakeText("Unsaved changes", UiRole.Muted, smallFont, new Padding(12, 0, 12, 0));
            unsaved.Anchor = AnchorStyles.Right;
            unsaved.Visible = false;
            RelayButton save = new RelayButton("Save settings", ButtonKind.Primary) { Anchor = AnchorStyles.Right, Margin = Padding.Empty, AccessibleName = "Save notification and advanced settings" };
            save.Click += delegate
            {
                TimeSpan validatedStart;
                TimeSpan validatedEnd;
                if (!QuietHoursText.TryParse(quietStart.Text, out validatedStart) || !QuietHoursText.TryParse(quietEnd.Text, out validatedEnd))
                {
                    MessageBox.Show("Enter quiet hours as 24-hour times, such as 22:00 and 07:00.", "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    TimeSpan ignored;
                    (QuietHoursText.TryParse(quietStart.Text, out ignored) ? quietEnd : quietStart).Focus();
                    return;
                }
                string validatedTarget;
                if (!TryNormalizeRdpTarget(rdpTarget.Text, out validatedTarget))
                {
                    setAdvancedExpanded(true);
                    MessageBox.Show("Enter an IP address or host name, optionally followed by :port, such as mac-vm.local:3390.", "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    rdpTarget.Focus();
                    return;
                }
                try
                {
                    SaveSettings(quietHours.Checked, validatedStart, validatedEnd, mutedSenders.Text, validatedTarget, allowPairedMacOnly.Checked, _lastMacAddress);
                    quietStart.Text = QuietHoursText.Format(validatedStart);
                    quietEnd.Text = QuietHoursText.Format(validatedEnd);
                    unsaved.Visible = false;
                    QueueStatus("Settings saved. The listener is receiving signed requests.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Couldn't save settings: " + ex.Message, "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            footerLayout.Controls.Add(status, 0, 0);
            footerLayout.Controls.Add(unsaved, 1, 0);
            footerLayout.Controls.Add(save, 2, 0);
            footer.Controls.Add(footerLayout);
            footer.Controls.Add(new Hairline { Dock = DockStyle.Top });

            EventHandler markUnsaved = delegate { unsaved.Visible = true; };
            quietHours.CheckedChanged += markUnsaved;
            quietStart.TextChanged += markUnsaved;
            quietEnd.TextChanged += markUnsaved;
            mutedSenders.TextChanged += markUnsaved;
            rdpTarget.TextChanged += markUnsaved;
            allowPairedMacOnly.CheckedChanged += markUnsaved;

            // ── Scrolling body: tracks the window width; two columns when there is room ──
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = UiRole.Page };
            NativeTheme.FollowAppTheme(scroll);
            TableLayoutPanel page = NewStack();
            page.Dock = DockStyle.Top;
            page.Padding = new Padding(24, 20, 24, 8);
            scroll.Controls.Add(page);
            bool? wideLayout = null;
            Action updatePageLayout = delegate
            {
                bool wide = scroll.ClientSize.Width >= UiPaint.Px(940);
                if (wideLayout.HasValue && wideLayout.Value == wide) return;
                wideLayout = wide;
                page.SuspendLayout();
                page.Controls.Clear();
                page.ColumnStyles.Clear();
                page.RowStyles.Clear();
                if (wide)
                {
                    // Pairing gets the wider column so the 64-character fingerprint stays fully visible.
                    page.ColumnCount = 2;
                    page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
                    page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
                    page.RowCount = 2;
                    page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    pairingCard.Margin = new Padding(0, 0, 8, 16);
                    notifyCard.Margin = new Padding(8, 0, 0, 16);
                    page.Controls.Add(pairingCard, 0, 0);
                    page.Controls.Add(notifyCard, 1, 0);
                    page.Controls.Add(advancedCard, 0, 1);
                    page.SetColumnSpan(advancedCard, 2);
                }
                else
                {
                    page.ColumnCount = 1;
                    page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                    page.RowCount = 0;
                    pairingCard.Margin = new Padding(0, 0, 0, 16);
                    notifyCard.Margin = new Padding(0, 0, 0, 16);
                    AddRow(page, pairingCard);
                    AddRow(page, notifyCard);
                    AddRow(page, advancedCard);
                    page.SetColumnSpan(advancedCard, 1);
                }
                page.ResumeLayout(true);
            };
            updatePageLayout();
            scroll.Resize += delegate { updatePageLayout(); };

            form.Controls.Add(scroll);
            form.Controls.Add(header);
            form.Controls.Add(footer);

            ApplyTheme(form, RelayPalette.Current());
            form.HandleCreated += delegate { NativeTheme.Apply(form, RelayPalette.Current().UseDarkChrome); };
            form.Shown += delegate
            {
                NativeTheme.Apply(form, RelayPalette.Current().UseDarkChrome);
                // Don't open with the read-only address list fully selected.
                ips.Select(0, 0);
            };
            UserPreferenceChangedEventHandler themeChanged = delegate(object sender, UserPreferenceChangedEventArgs args)
            {
                if (args.Category != UserPreferenceCategory.General && args.Category != UserPreferenceCategory.Color &&
                    args.Category != UserPreferenceCategory.Accessibility && args.Category != UserPreferenceCategory.VisualStyle)
                    return;
                if (form.IsDisposed || !form.IsHandleCreated) return;
                form.BeginInvoke((MethodInvoker)delegate
                {
                    if (!form.IsDisposed) ApplyTheme(form, RelayPalette.Current());
                });
            };
            SystemEvents.UserPreferenceChanged += themeChanged;
            form.FormClosed += delegate
            {
                SystemEvents.UserPreferenceChanged -= themeChanged;
                copyResetTimer.Stop();
                copyResetTimer.Dispose();
                fingerprintResetTimer.Stop();
                fingerprintResetTimer.Dispose();
                clearTokenTimer.Stop();
                clearTokenTimer.Dispose();
            };
            return form;
        }

        private static void ApplyTheme(Form form, RelayPalette palette)
        {
            form.SuspendLayout();
            form.BackColor = palette.Page;
            form.ForeColor = palette.Foreground;
            foreach (Control child in form.Controls)
                ApplyPalette(child, palette, palette.Page);
            form.ResumeLayout(true);
            if (form.IsHandleCreated)
                NativeTheme.Apply(form, palette.UseDarkChrome);
            form.Invalidate(true);
        }

        private static void ApplyPalette(Control control, RelayPalette palette, Color inherited)
        {
            Color childBackground;
            IThemed themed = control as IThemed;
            if (themed != null)
                childBackground = themed.ApplyPalette(palette, inherited);
            else
            {
                UiRole role = control.Tag is UiRole ? (UiRole)control.Tag : UiRole.None;
                childBackground = role == UiRole.Page ? palette.Page : role == UiRole.Surface ? palette.Card : inherited;
                control.BackColor = childBackground;
                control.ForeColor = role == UiRole.Muted ? palette.MutedForeground : palette.Foreground;
            }
            foreach (Control child in control.Controls)
                ApplyPalette(child, palette, childBackground);
        }

        private static TableLayoutPanel NewStack()
        {
            TableLayoutPanel stack = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
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

        private static Label MakeText(string text, UiRole role, Font font, Padding margin)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = font,
                Tag = role,
                UseMnemonic = false,
                Margin = margin,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
        }

        private static void AddLabel(TableLayoutPanel table, string text, Font font)
        {
            AddRow(table, MakeText(text, UiRole.Body, font, new Padding(0, 0, 0, 6)));
        }

        private static void AddHelp(TableLayoutPanel table, string text, Font font)
        {
            AddRow(table, MakeText(text, UiRole.Muted, font, new Padding(0, 6, 0, 16)));
        }

        private static void AddSeparator(TableLayoutPanel table)
        {
            AddRow(table, new Hairline { Margin = new Padding(0, 0, 0, 16) });
        }

        private static CardPanel NewCard(string title, string description, Font titleFont, Font descriptionFont)
        {
            CardPanel card = new CardPanel();
            AddRow(card, MakeText(title, UiRole.Body, titleFont, new Padding(0, 0, 0, 2)));
            AddRow(card, MakeText(description, UiRole.Muted, descriptionFont, new Padding(0, 0, 0, 18)));
            return card;
        }

        private static TableLayoutPanel InlineRow(Control fill, params Control[] trailing)
        {
            TableLayoutPanel row = new TableLayoutPanel
            {
                ColumnCount = 1 + trailing.Length,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fill.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            fill.Margin = Padding.Empty;
            row.Controls.Add(fill, 0, 0);
            for (int index = 0; index < trailing.Length; index++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                trailing[index].Anchor = AnchorStyles.None;
                trailing[index].Margin = new Padding(8, 0, 0, 0);
                row.Controls.Add(trailing[index], index + 1, 0);
            }
            return row;
        }

        /// <summary>Title and description on the left, a switch or button on the right.</summary>
        private static TableLayoutPanel ActionRow(string title, string description, Control action, Font titleFont, Font descriptionFont)
        {
            TableLayoutPanel row = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 16),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label titleLabel = MakeText(title, UiRole.Body, titleFont, new Padding(0, 0, 0, 2));
            Label descriptionLabel = MakeText(description, UiRole.Muted, descriptionFont, Padding.Empty);
            action.Anchor = AnchorStyles.Right;
            action.Margin = new Padding(16, 0, 0, 0);
            row.Controls.Add(titleLabel, 0, 0);
            row.Controls.Add(descriptionLabel, 0, 1);
            row.Controls.Add(action, 1, 0);
            row.SetRowSpan(action, 2);
            CheckBox toggle = action as CheckBox;
            if (toggle != null)
            {
                if (String.IsNullOrEmpty(toggle.AccessibleName)) toggle.AccessibleName = title;
                toggle.AccessibleDescription = description;
                EventHandler flip = delegate
                {
                    if (!toggle.Enabled) return;
                    toggle.Checked = !toggle.Checked;
                    toggle.Focus();
                };
                titleLabel.Click += flip;
                descriptionLabel.Click += flip;
            }
            return row;
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
                "Last authenticated Mac clock skew in seconds: " + Interlocked.Read(ref _lastClockSkewSeconds).ToString(CultureInfo.InvariantCulture),
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

        protected override void ExitThreadCore()
        {
            _running = false;
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

    // ───────────────────────── Settings window design system ─────────────────────────
    // Native WinForms controls painted with a neutral palette modeled on shadcn/ui's
    // zinc light and dark themes. High Contrast falls back to system colors.

    public enum StatusTone { Neutral, Success, Warning, Destructive }

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

    internal enum UiRole { None, Page, Surface, Body, Muted }

    internal interface IThemed
    {
        /// <summary>Applies the palette and returns the background its children sit on.</summary>
        Color ApplyPalette(RelayPalette palette, Color inherited);
    }

    internal sealed class RelayPalette
    {
        public bool IsDark;
        public bool IsHighContrast;
        public Color Page, Card, Muted, Border, Input, Ring, Foreground, MutedForeground, Primary, PrimaryForeground;
        public Color Success, SuccessSoft, SuccessBorder, Warning, WarningSoft, WarningBorder, Destructive, DestructiveSoft, DestructiveBorder;

        public bool UseDarkChrome { get { return IsDark && !IsHighContrast; } }

        public static RelayPalette Current()
        {
            if (SystemInformation.HighContrast) return HighContrast();
            return IsSystemDarkTheme() ? Dark() : Light();
        }

        public static RelayPalette Light()
        {
            return new RelayPalette
            {
                Page = Hex(0xFAFAFA), Card = Hex(0xFFFFFF), Muted = Hex(0xF4F4F5), Border = Hex(0xE4E4E7),
                Input = Hex(0xD4D4D8), Ring = Hex(0xA1A1AA), Foreground = Hex(0x09090B), MutedForeground = Hex(0x71717A),
                Primary = Hex(0x18181B), PrimaryForeground = Hex(0xFAFAFA),
                Success = Hex(0x15803D), SuccessSoft = Hex(0xF0FDF4), SuccessBorder = Hex(0xBBF7D0),
                Warning = Hex(0xB45309), WarningSoft = Hex(0xFFFBEB), WarningBorder = Hex(0xFDE68A),
                Destructive = Hex(0xB91C1C), DestructiveSoft = Hex(0xFEF2F2), DestructiveBorder = Hex(0xFECACA)
            };
        }

        public static RelayPalette Dark()
        {
            return new RelayPalette
            {
                IsDark = true,
                Page = Hex(0x09090B), Card = Hex(0x111113), Muted = Hex(0x1C1C1F), Border = Hex(0x27272A),
                Input = Hex(0x3F3F46), Ring = Hex(0x71717A), Foreground = Hex(0xFAFAFA), MutedForeground = Hex(0xA1A1AA),
                Primary = Hex(0xFAFAFA), PrimaryForeground = Hex(0x18181B),
                Success = Hex(0x4ADE80), SuccessSoft = Hex(0x0B2316), SuccessBorder = Hex(0x14532D),
                Warning = Hex(0xFBBF24), WarningSoft = Hex(0x261C06), WarningBorder = Hex(0x713F12),
                Destructive = Hex(0xF87171), DestructiveSoft = Hex(0x2A1010), DestructiveBorder = Hex(0x7F1D1D)
            };
        }

        public static RelayPalette HighContrast()
        {
            Color window = SystemColors.Window;
            Color text = SystemColors.WindowText;
            return new RelayPalette
            {
                IsHighContrast = true,
                Page = window, Card = window, Muted = window, Border = text, Input = text, Ring = SystemColors.Highlight,
                Foreground = text, MutedForeground = text, Primary = SystemColors.Highlight, PrimaryForeground = SystemColors.HighlightText,
                Success = text, SuccessSoft = window, SuccessBorder = text,
                Warning = text, WarningSoft = window, WarningBorder = text,
                Destructive = text, DestructiveSoft = window, DestructiveBorder = text
            };
        }

        public Color ToneForeground(StatusTone tone)
        {
            switch (tone)
            {
                case StatusTone.Success: return Success;
                case StatusTone.Warning: return Warning;
                case StatusTone.Destructive: return Destructive;
                default: return MutedForeground;
            }
        }

        public Color ToneSoft(StatusTone tone)
        {
            switch (tone)
            {
                case StatusTone.Success: return SuccessSoft;
                case StatusTone.Warning: return WarningSoft;
                case StatusTone.Destructive: return DestructiveSoft;
                default: return Muted;
            }
        }

        public Color ToneBorder(StatusTone tone)
        {
            switch (tone)
            {
                case StatusTone.Success: return SuccessBorder;
                case StatusTone.Warning: return WarningBorder;
                case StatusTone.Destructive: return DestructiveBorder;
                default: return Border;
            }
        }

        internal static bool IsSystemDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { return false; }
        }

        private static Color Hex(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
    }

    internal static class UiPaint
    {
        private static float _scale;

        /// <summary>System DPI scale; the app manifest declares system DPI awareness.</summary>
        public static float Scale
        {
            get
            {
                if (_scale <= 0f)
                {
                    try
                    {
                        using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero)) _scale = graphics.DpiX / 96f;
                    }
                    catch { _scale = 1f; }
                }
                return _scale;
            }
        }

        public static int Px(float value) { return (int)Math.Round(value * Scale); }

        public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 1f)
            {
                path.AddRectangle(bounds);
                return path;
            }
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

        public static Color OuterBackground(Control control)
        {
            return control.Parent != null ? control.Parent.BackColor : control.BackColor;
        }

        public static void FillOuter(Graphics graphics, Control control)
        {
            using (SolidBrush brush = new SolidBrush(OuterBackground(control)))
                graphics.FillRectangle(brush, control.ClientRectangle);
        }

        public static void DrawFocusRing(Graphics graphics, RectangleF bounds, float radius, Color color)
        {
            using (GraphicsPath ring = RoundedRect(RectangleF.Inflate(bounds, 1.5f * Scale, 1.5f * Scale), radius + 1.5f * Scale))
            using (Pen pen = new Pen(color, 2f * Scale))
                graphics.DrawPath(pen, ring);
        }
    }

    internal static class NativeTheme
    {
        private const int DwmUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        /// <summary>Matches the title bar and scroll bars to the app theme. Best effort; older Windows builds ignore it.</summary>
        public static void Apply(Form form, bool dark)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
            try
            {
                int value = dark ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
            }
            catch (Exception) { }
            ApplyScrollBars(form, dark);
        }

        /// <summary>
        /// Themes a scrollable control's scroll bars as soon as Windows creates its handle, and again
        /// whenever WinForms recreates it (for example after a ScrollBars change), so no scroll bar
        /// ever appears in the light style first or misses the theme because it was created late.
        /// </summary>
        public static void FollowAppTheme(Control control)
        {
            control.HandleCreated += delegate { ThemeScrollBars(control, RelayPalette.Current().UseDarkChrome); };
            if (control.IsHandleCreated) ThemeScrollBars(control, RelayPalette.Current().UseDarkChrome);
        }

        private static void ThemeScrollBars(Control control, bool dark)
        {
            try { SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (Exception) { }
        }

        private static void ApplyScrollBars(Control parent, bool dark)
        {
            foreach (Control child in parent.Controls)
            {
                Panel panel = child as Panel;
                TextBox textBox = child as TextBox;
                if (child.IsHandleCreated && ((panel != null && panel.AutoScroll) || (textBox != null && textBox.Multiline)))
                    ThemeScrollBars(child, dark);
                ApplyScrollBars(child, dark);
            }
        }
    }

    internal sealed class Hairline : Control, IThemed
    {
        public Hairline()
        {
            Height = 1;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            BackColor = palette.Border;
            return palette.Border;
        }
    }

    /// <summary>A single-column layout with a rounded, bordered card surface.</summary>
    internal sealed class CardPanel : TableLayoutPanel, IThemed
    {
        private RelayPalette _palette;

        public CardPanel()
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

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = palette.Card;
            Invalidate();
            return palette.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            UiPaint.FillOuter(e.Graphics, this);
            if (_palette == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = UiPaint.RoundedRect(bounds, 10f * UiPaint.Scale))
            using (SolidBrush fill = new SolidBrush(_palette.Card))
            using (Pen border = new Pen(_palette.Border))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    /// <summary>Hosts a borderless TextBox inside a rounded input frame with a focus ring.</summary>
    internal sealed class FieldHost : Panel, IThemed
    {
        private readonly TextBox _box;
        private int _lines;
        private RelayPalette _palette;

        public FieldHost(TextBox box, int lines)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            _box = box;
            _lines = Math.Max(1, lines);
            PreferredWidth = 80;
            box.BorderStyle = BorderStyle.None;
            Controls.Add(box);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Margin = Padding.Empty;
            Cursor = Cursors.IBeam;
            box.GotFocus += delegate { Invalidate(); };
            box.LostFocus += delegate { Invalidate(); };
            box.EnabledChanged += delegate { UpdateBoxColors(); Invalidate(); };
            box.FontChanged += delegate { PerformLayout(); };
            Click += delegate { if (_box.CanFocus) _box.Focus(); };
        }

        public int PreferredWidth { get; set; }

        /// <summary>Visible text lines; changing it resizes the field and re-lays out its parent.</summary>
        public int VisibleLines
        {
            get { return _lines; }
            set
            {
                int lines = Math.Max(1, value);
                if (lines == _lines) return;
                _lines = lines;
                Height = GetPreferredSize(Size.Empty).Height;
                if (Parent != null) Parent.PerformLayout();
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(UiPaint.Px(PreferredWidth), _box.Font.Height * _lines + UiPaint.Px(9) * 2 + 2);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int horizontal = UiPaint.Px(10);
            int width = Math.Max(1, Width - horizontal * 2);
            if (_box.Multiline)
            {
                int vertical = UiPaint.Px(8);
                _box.SetBounds(horizontal, vertical, width, Math.Max(1, Height - vertical * 2));
            }
            else
            {
                _box.Width = width;
                _box.Location = new Point(horizontal, Math.Max(0, (Height - _box.Height) / 2));
            }
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            UpdateBoxColors();
            Invalidate();
            return FieldBackground;
        }

        private Color FieldBackground
        {
            get
            {
                if (_palette == null) return SystemColors.Window;
                return _box.ReadOnly || !_box.Enabled ? _palette.Muted : _palette.Card;
            }
        }

        private void UpdateBoxColors()
        {
            if (_palette == null) return;
            _box.BackColor = FieldBackground;
            _box.ForeColor = _palette.Foreground;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            UiPaint.FillOuter(e.Graphics, this);
            if (_palette == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = 2f * UiPaint.Scale;
            RectangleF bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
            float radius = 6f * UiPaint.Scale;
            bool focused = _box.Focused;
            using (GraphicsPath path = UiPaint.RoundedRect(bounds, radius))
            using (SolidBrush fill = new SolidBrush(FieldBackground))
            using (Pen border = new Pen(focused ? _palette.Ring : _palette.Input))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            if (focused)
                UiPaint.DrawFocusRing(e.Graphics, bounds, radius, UiPaint.Blend(_palette.Ring, UiPaint.OuterBackground(this), 0.45f));
        }
    }

    internal enum ButtonKind { Primary, Secondary, Ghost }

    internal sealed class RelayButton : Button, IThemed
    {
        private readonly ButtonKind _kind;
        private RelayPalette _palette;
        private bool _hover;
        private bool _pressed;

        public RelayButton(string text, ButtonKind kind)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _kind = kind;
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return new Size(text.Width + UiPaint.Px(28), Math.Max(text.Height + UiPaint.Px(14), UiPaint.Px(32)));
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            ForeColor = _kind == ButtonKind.Primary ? palette.PrimaryForeground : palette.Foreground;
            Invalidate();
            return inherited;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics graphics = pevent.Graphics;
            UiPaint.FillOuter(graphics, this);
            RelayPalette palette = _palette ?? RelayPalette.Light();
            Color outer = UiPaint.OuterBackground(this);
            Color fill;
            Color text;
            Color border = Color.Empty;
            switch (_kind)
            {
                case ButtonKind.Primary:
                    fill = _pressed ? UiPaint.Blend(palette.Primary, outer, 0.25f) : _hover ? UiPaint.Blend(palette.Primary, outer, 0.12f) : palette.Primary;
                    text = palette.PrimaryForeground;
                    if (palette.IsHighContrast) border = palette.Border;
                    break;
                case ButtonKind.Secondary:
                    fill = _pressed ? palette.Border : _hover ? palette.Muted : palette.Card;
                    text = palette.Foreground;
                    border = palette.Input;
                    break;
                default:
                    fill = _pressed ? palette.Border : _hover ? palette.Muted : outer;
                    text = palette.Foreground;
                    break;
            }
            if (!Enabled)
            {
                fill = UiPaint.Blend(fill, outer, 0.5f);
                text = UiPaint.Blend(text, outer, 0.5f);
                if (border != Color.Empty) border = UiPaint.Blend(border, outer, 0.5f);
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = 2f * UiPaint.Scale;
            RectangleF bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
            float radius = 6f * UiPaint.Scale;
            using (GraphicsPath path = UiPaint.RoundedRect(bounds, radius))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                graphics.FillPath(brush, path);
                if (border != Color.Empty)
                    using (Pen pen = new Pen(border)) graphics.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
                UiPaint.DrawFocusRing(graphics, bounds, radius, palette.Ring);
            TextRenderer.DrawText(graphics, Text, Font, Rectangle.Round(bounds), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>A CheckBox drawn as a switch. Keyboard, accessibility, and Checked semantics are unchanged.</summary>
    internal sealed class RelaySwitch : CheckBox, IThemed
    {
        private RelayPalette _palette;
        private bool _hover;

        public RelaySwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Text = "";
            Size = new Size(44, 28);
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
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

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics graphics = pevent.Graphics;
            UiPaint.FillOuter(graphics, this);
            RelayPalette palette = _palette ?? RelayPalette.Light();
            Color outer = UiPaint.OuterBackground(this);
            float scale = UiPaint.Scale;
            float trackWidth = Math.Min(36f * scale, Width - 6f * scale);
            float trackHeight = trackWidth * 20f / 36f;
            RectangleF track = new RectangleF((Width - trackWidth) / 2f, (Height - trackHeight) / 2f, trackWidth, trackHeight);
            Color trackColor = Checked ? palette.Primary : (_hover ? UiPaint.Blend(palette.Input, palette.Ring, 0.35f) : palette.Input);
            Color knobColor = Checked ? palette.PrimaryForeground : palette.Card;
            if (!Enabled)
            {
                trackColor = UiPaint.Blend(trackColor, outer, 0.5f);
                knobColor = UiPaint.Blend(knobColor, outer, 0.5f);
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = UiPaint.RoundedRect(track, trackHeight / 2f))
            using (SolidBrush brush = new SolidBrush(trackColor))
            {
                graphics.FillPath(brush, path);
                if (palette.IsHighContrast)
                    using (Pen pen = new Pen(palette.Border)) graphics.DrawPath(pen, path);
            }
            float gap = 2f * scale;
            float knob = trackHeight - gap * 2f;
            float knobX = Checked ? track.Right - gap - knob : track.X + gap;
            using (SolidBrush brush = new SolidBrush(knobColor))
                graphics.FillEllipse(brush, knobX, track.Y + gap, knob, knob);
            if (Focused && ShowFocusCues)
                UiPaint.DrawFocusRing(graphics, track, trackHeight / 2f, palette.Ring);
        }
    }

    /// <summary>A pill badge with a status dot, used for the live pairing state.</summary>
    internal sealed class BadgeLabel : Label, IThemed
    {
        private RelayPalette _palette;
        private StatusTone _tone;

        public BadgeLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = true;
            UseMnemonic = false;
        }

        public StatusTone Tone
        {
            get { return _tone; }
            set { _tone = value; Invalidate(); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return new Size(text.Width + UiPaint.Px(34), text.Height + UiPaint.Px(10));
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            ForeColor = palette.Foreground;
            Invalidate();
            return inherited;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPaint.FillOuter(e.Graphics, this);
            RelayPalette palette = _palette ?? RelayPalette.Light();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = UiPaint.RoundedRect(bounds, bounds.Height / 2f))
            using (SolidBrush fill = new SolidBrush(palette.ToneSoft(_tone)))
            using (Pen border = new Pen(palette.ToneBorder(_tone)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            float dot = 7f * UiPaint.Scale;
            using (SolidBrush dotBrush = new SolidBrush(palette.ToneForeground(_tone)))
                e.Graphics.FillEllipse(dotBrush, UiPaint.Px(12), (Height - dot) / 2f, dot, dot);
            Rectangle textBounds = new Rectangle(UiPaint.Px(24), 0, Math.Max(1, Width - UiPaint.Px(34)), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                _tone == StatusTone.Neutral ? palette.Foreground : palette.ToneForeground(_tone),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Status text with a leading tone dot; long text ends in an ellipsis.</summary>
    internal sealed class DotLabel : Label, IThemed
    {
        private RelayPalette _palette;
        private StatusTone _tone;

        public DotLabel()
        {
            AutoSize = false;
            AutoEllipsis = true;
            UseMnemonic = false;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(18, 0, 0, 0);
            Margin = Padding.Empty;
        }

        public StatusTone Tone
        {
            get { return _tone; }
            set { _tone = value; UpdateColors(); Invalidate(); }
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            UpdateColors();
            Invalidate();
            return inherited;
        }

        private void UpdateColors()
        {
            if (_palette == null) return;
            ForeColor = _tone == StatusTone.Destructive || _tone == StatusTone.Warning ? _palette.ToneForeground(_tone) : _palette.Foreground;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RelayPalette palette = _palette ?? RelayPalette.Light();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float dot = 8f * UiPaint.Scale;
            using (SolidBrush brush = new SolidBrush(palette.ToneForeground(_tone)))
                e.Graphics.FillEllipse(brush, UiPaint.Px(2), (Height - dot) / 2f, dot, dot);
        }
    }

    /// <summary>A full-width disclosure header that expands or collapses a section.</summary>
    internal sealed class DisclosureButton : Button, IThemed
    {
        private readonly string _subtitle;
        private readonly Font _titleFont;
        private readonly Font _subtitleFont;
        private RelayPalette _palette;
        private bool _expanded;
        private bool _hover;

        public DisclosureButton(string title, string subtitle, Font titleFont, Font subtitleFont)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = title;
            AccessibleName = title;
            _subtitle = subtitle;
            _titleFont = titleFont;
            _subtitleFont = subtitleFont;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Margin = Padding.Empty;
            UpdateAccessibility();
        }

        public bool Expanded
        {
            get { return _expanded; }
            set { _expanded = value; UpdateAccessibility(); Invalidate(); }
        }

        private void UpdateAccessibility()
        {
            AccessibleDescription = (_expanded ? "Expanded. " : "Collapsed. ") + _subtitle;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(UiPaint.Px(120), _titleFont.Height + _subtitleFont.Height + UiPaint.Px(26));
        }

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            ForeColor = palette.Foreground;
            Invalidate();
            return inherited;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics graphics = pevent.Graphics;
            UiPaint.FillOuter(graphics, this);
            RelayPalette palette = _palette ?? RelayPalette.Light();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = 2f * UiPaint.Scale;
            RectangleF bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
            float radius = 6f * UiPaint.Scale;
            if (_hover)
                using (GraphicsPath path = UiPaint.RoundedRect(bounds, radius))
                using (SolidBrush brush = new SolidBrush(palette.Muted))
                    graphics.FillPath(brush, path);
            if (Focused && ShowFocusCues)
                UiPaint.DrawFocusRing(graphics, bounds, radius, palette.Ring);

            int padding = UiPaint.Px(12);
            int chevronSpace = UiPaint.Px(28);
            int textHeight = _titleFont.Height + _subtitleFont.Height + UiPaint.Px(2);
            int top = (Height - textHeight) / 2;
            int textWidth = Math.Max(1, Width - padding - chevronSpace);
            TextRenderer.DrawText(graphics, Text, _titleFont, new Rectangle(padding, top, textWidth, _titleFont.Height), palette.Foreground,
                TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(graphics, _subtitle, _subtitleFont, new Rectangle(padding, top + _titleFont.Height + UiPaint.Px(2), textWidth, _subtitleFont.Height), palette.MutedForeground,
                TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            float size = 4f * UiPaint.Scale;
            float centerX = Width - padding - size;
            float centerY = Height / 2f;
            PointF[] chevron = _expanded
                ? new PointF[] { new PointF(centerX - size, centerY - size / 2f), new PointF(centerX, centerY + size / 2f), new PointF(centerX + size, centerY - size / 2f) }
                : new PointF[] { new PointF(centerX - size / 2f, centerY - size), new PointF(centerX + size / 2f, centerY), new PointF(centerX - size / 2f, centerY + size) };
            using (Pen pen = new Pen(palette.MutedForeground, 1.6f * UiPaint.Scale))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                graphics.DrawLines(pen, chevron);
            }
        }
    }

    /// <summary>
    /// Bubblecast icon and brand mark embedded by build.cmd. Icons load at native frame sizes and the
    /// header mark is downscaled from 256 px with high-quality resampling, so neither is ever upscaled.
    /// </summary>
    internal static class BrandAssets
    {
        public static Icon AppIcon(Size size)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bubblecast.AppIcon.ico"))
                    return stream == null ? null : (size.IsEmpty ? new Icon(stream) : new Icon(stream, size));
            }
            catch (Exception) { return null; }
        }

        public static Image Mark(int pixels)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bubblecast.BrandMark.png"))
                {
                    if (stream == null) return null;
                    using (Bitmap source = new Bitmap(stream))
                    {
                        Bitmap result = new Bitmap(pixels, pixels);
                        using (Graphics graphics = Graphics.FromImage(result))
                        {
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.SmoothingMode = SmoothingMode.AntiAlias;
                            graphics.DrawImage(source, 0, 0, pixels, pixels);
                        }
                        return result;
                    }
                }
            }
            catch (Exception) { return null; }
        }
    }
}
