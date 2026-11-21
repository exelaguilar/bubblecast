using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bubblecast
{
    // TCP listeners (HTTP 8765, TLS 8766), request handling, and rejection accounting.
    internal sealed partial class RelayContext
    {
        private IPAddress ListenIPAddress()
        {
            IPAddress address;
            string configured = _listenAddress;
            return !String.IsNullOrEmpty(configured) && IPAddress.TryParse(configured, out address) ? address : IPAddress.Any;
        }

        /// <summary>Stops both listeners so their loops reopen them on the current listen address.</summary>
        private void RestartListeners()
        {
            Interlocked.Increment(ref _listenerGeneration);
            TcpListener http = _listener;
            TcpListener tls = _tlsListener;
            if (http != null) { try { http.Stop(); } catch { } }
            if (tls != null) { try { tls.Stop(); } catch { } }
        }

        private string ListeningOn()
        {
            return String.IsNullOrEmpty(_listenAddress) ? "" : " on " + _listenAddress;
        }

        private void ListenLoop()
        {
            AcceptLoop(Port, false);
        }

        private void ListenTlsLoop()
        {
            AcceptLoop(TlsPort, true);
        }

        /// <summary>
        /// Accepts connections on one port until the app exits, reopening the listener after errors or a
        /// listen-address change. Both ports share the client slots.
        /// </summary>
        private void AcceptLoop(int port, bool useTls)
        {
            int retryDelayMs = 1000;
            while (_running && (!useTls || _tlsCertificate != null))
            {
                int generation = Volatile.Read(ref _listenerGeneration);
                TcpListener listener = new TcpListener(ListenIPAddress(), port);
                try
                {
                    listener.Start();
                    if (useTls) _tlsListener = listener; else _listener = listener;
                    retryDelayMs = 1000;
                    QueueStatus(_tlsCertificate != null
                        ? "Listening on TCP " + Port + " (HTTP) and " + TlsPort + " (TLS)" + ListeningOn()
                        : "Listening on TCP " + Port + ListeningOn());
                    while (_running)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        // Every read and write gives up after the request deadline, so a client that
                        // connects and goes silent (including mid-TLS-handshake) can't hold a slot.
                        client.ReceiveTimeout = BoundedHttpRequestReader.TotalReadDeadlineMilliseconds;
                        client.SendTimeout = BoundedHttpRequestReader.TotalReadDeadlineMilliseconds;
                        if (!_clientSlots.Wait(0))
                        {
                            if (!useTls)
                            {
                                try { WriteResponse(client.GetStream(), "503 Service Unavailable"); }
                                catch (IOException) { }
                            }
                            client.Close();
                            continue;
                        }
                        TcpClient accepted = client;
                        Task.Factory.StartNew(delegate
                        {
                            try { HandleClient(accepted, useTls); }
                            catch (Exception)
                            {
                                if (_running)
                                    QueueStatus(useTls
                                        ? "A TLS relay connection failed; the listener remains available."
                                        : "A relay request could not be processed; the listener is still running.");
                            }
                            finally
                            {
                                accepted.Close();
                                _clientSlots.Release();
                            }
                        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                    }
                }
                catch (Exception ex)
                {
                    if (_running && generation != Volatile.Read(ref _listenerGeneration))
                        retryDelayMs = 1000;
                    else if (_running)
                    {
                        QueueStatus(useTls
                            ? "TLS listener unavailable; HTTP remains available."
                            : "Listener unavailable; retrying in " + (retryDelayMs / 1000) + " sec: " + ex.Message);
                        Thread.Sleep(retryDelayMs);
                        retryDelayMs = Math.Min(retryDelayMs * 2, 30000);
                    }
                }
                finally
                {
                    try { listener.Stop(); } catch { }
                    if (useTls) { if (Object.ReferenceEquals(_tlsListener, listener)) _tlsListener = null; }
                    else if (Object.ReferenceEquals(_listener, listener)) _listener = null;
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
                    ((parts[0] == "POST" && parts[1] == "/notify") || (parts[0] == "GET" && parts[1] == "/health") ||
                     (parts[0] == "POST" && parts[1] == "/settings")))
                {
                    string path = parts[1];
                    string senderBase64;
                    string previewBase64;
                    if (!parsed.Headers.TryGetValue("X-Sender-Base64", out senderBase64)) senderBase64 = "";
                    // For /settings, the settings text is signed in the preview's place (see docs/PROTOCOL.md).
                    if (!parsed.Headers.TryGetValue(path == "/settings" ? "X-Relay-Settings" : "X-Preview-Base64", out previewBase64)) previewBase64 = "";
                    if (path == "/settings") senderBase64 = "";
                    RequestVerification verdict = _verifier.Verify(parts[0], path, parsed.Headers, senderBase64, previewBase64);
                    string errorCode = verdict.ErrorCode;
                    string settingsHeaders = null;
                    if (!verdict.Accepted)
                    {
                        status = "403 Forbidden";
                        RecordRejection(verdict.Rejection);
                    }
                    else if (path == "/settings" && !ReceiveSharedSettings(previewBase64))
                    {
                        status = "400 Bad Request";
                        RecordRejection(RequestRejectionReason.Malformed);
                    }
                    else
                    {
                        status = "204 No Content";
                        string nonce;
                        if (parsed.Headers.TryGetValue("X-Relay-Nonce", out nonce))
                            settingsHeaders = SignedSharedSettingsHeaders(nonce);
                        RecordAcceptedRequest(path);
                        if (remoteEndpoint != null && remoteEndpoint.Address.AddressFamily == AddressFamily.InterNetwork)
                            QueueLatestMacAddress(remoteEndpoint.Address.ToString());
                        if (path == "/notify" && !verdict.DuplicateMessage)
                            QueueToast(DecodeHeader(senderBase64, 80), DecodeHeader(previewBase64, 180), verdict.Kind == "test");
                    }
                    WriteResponse(stream, status, errorCode, settingsHeaders);
                    return;
                }
            }

            WriteResponse(stream, status);
        }

        private static readonly string ServerVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private static void WriteResponse(Stream stream, string status, string errorCode = null, string extraHeaders = null)
        {
            string headers = "HTTP/1.1 " + status + "\r\nX-Relay-Server-Version: " + ServerVersion + "\r\n";
            if (!String.IsNullOrEmpty(errorCode)) headers += "X-Relay-Error: " + errorCode + "\r\n";
            if (!String.IsNullOrEmpty(extraHeaders)) headers += extraHeaders;
            byte[] response = Encoding.ASCII.GetBytes(headers + "Content-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.Flush();
        }

        private void RecordAcceptedRequest(string path)
        {
            long now = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _lastSignedRequestUtcTicks, now);
            if (path == "/settings")
            {
                // Settings arrive alongside health checks; only the last-seen time matters.
            }
            else if (path == "/notify")
            {
                Interlocked.Increment(ref _notificationsReceived);
                Interlocked.Exchange(ref _lastNotificationUtcTicks, now);
            }
            else
            {
                Interlocked.Increment(ref _healthChecksReceived);
                Interlocked.Exchange(ref _lastHealthCheckUtcTicks, now);
            }
            QueueUiAction(delegate
            {
                UpdateTrayStatus();
                if (_macQuietAlerted)
                {
                    _macQuietAlerted = false;
                    ShowNotice("Mac is back", "Bubblecast is hearing from your Mac again.");
                }
            });
        }

        private void RecordRejection(RequestRejectionReason reason)
        {
            Interlocked.Increment(ref _rejectionCounts[(int)reason - 1]);
            string description;
            switch (reason)
            {
                case RequestRejectionReason.BadSignature: description = "bad signature"; break;
                case RequestRejectionReason.ClockSkew:
                    long skew = _verifier.LastClockSkewSeconds;
                    description = "Mac clock differs by " + Math.Abs((decimal)skew).ToString("0", CultureInfo.InvariantCulture) + " seconds (" + (skew < 0 ? "behind" : "ahead") + ")";
                    break;
                case RequestRejectionReason.Replay: description = "replayed nonce"; break;
                case RequestRejectionReason.Oversize: description = "oversize request"; break;
                case RequestRejectionReason.AddressNotAllowed: description = "request from an address outside the paired Mac allowlist"; break;
                default: description = "malformed request"; break;
            }
            QueueStatus("Rejected a request from the Mac: " + description + ".");
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
    }
}
