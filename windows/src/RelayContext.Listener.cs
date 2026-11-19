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
            int retryDelayMs = 1000;
            while (_running)
            {
                int generation = Volatile.Read(ref _listenerGeneration);
                TcpListener listener = new TcpListener(ListenIPAddress(), Port);
                try
                {
                    listener.Start();
                    _listener = listener;
                    retryDelayMs = 1000;
                    QueueStatus("Listening on TCP " + Port + ListeningOn());
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
                    if (_running && generation != Volatile.Read(ref _listenerGeneration))
                        retryDelayMs = 1000;
                    else if (_running)
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
                int generation = Volatile.Read(ref _listenerGeneration);
                TcpListener listener = new TcpListener(ListenIPAddress(), TlsPort);
                try
                {
                    listener.Start();
                    _tlsListener = listener;
                    retryDelayMs = 1000;
                    QueueStatus("Listening on TCP " + Port + " (HTTP) and " + TlsPort + " (TLS)" + ListeningOn());
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
                    if (_running && generation != Volatile.Read(ref _listenerGeneration))
                        retryDelayMs = 1000;
                    else if (_running)
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
                    RequestVerification verdict = _verifier.Verify(parts[0], path, parsed.Headers, senderBase64, previewBase64);
                    string errorCode = verdict.ErrorCode;
                    if (!verdict.Accepted)
                    {
                        status = "403 Forbidden";
                        RecordRejection(verdict.Rejection);
                    }
                    else
                    {
                        status = "204 No Content";
                        RecordAcceptedRequest(path);
                        IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                        if (remote != null && remote.Address.AddressFamily == AddressFamily.InterNetwork)
                            QueueLatestMacAddress(remote.Address.ToString());
                        if (path == "/notify" && !verdict.DuplicateMessage)
                            QueueToast(DecodeHeader(senderBase64, 80), DecodeHeader(previewBase64, 180), verdict.Kind == "test");
                    }
                    WriteResponse(stream, status, errorCode);
                    return;
                }
            }

            WriteResponse(stream, status);
        }

        private static readonly string ServerVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private static void WriteResponse(Stream stream, string status, string errorCode = null)
        {
            string headers = "HTTP/1.1 " + status + "\r\nX-Relay-Server-Version: " + ServerVersion + "\r\n";
            if (!String.IsNullOrEmpty(errorCode)) headers += "X-Relay-Error: " + errorCode + "\r\n";
            byte[] response = Encoding.ASCII.GetBytes(headers + "Content-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.Flush();
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
