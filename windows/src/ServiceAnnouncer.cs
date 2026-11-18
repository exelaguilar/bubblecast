using System;
using System.Net;
using System.Runtime.InteropServices;

namespace Bubblecast
{
    /// <summary>
    /// Announces this receiver on the local network as "&lt;PC name&gt;._bubblecast._tcp.local" using the DNS-SD
    /// API built into Windows 10 1809 and later (dnsapi.dll), so the Mac can find it without typing an address.
    /// Best effort: on older Windows or if registration fails, the app works as before.
    /// </summary>
    internal sealed class ServiceAnnouncer : IDisposable
    {
        public const string ServiceType = "_bubblecast._tcp";
        private const uint DnsQueryRequestVersion1 = 1;
        private const uint DnsRequestPending = 9506;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void RegisterComplete(uint status, IntPtr queryContext, IntPtr instance);

        [StructLayout(LayoutKind.Sequential)]
        private struct DnsServiceRegisterRequest
        {
            public uint Version;
            public uint InterfaceIndex;
            public IntPtr ServiceInstance;
            public RegisterComplete RegisterCompletionCallback;
            public IntPtr QueryContext;
            public IntPtr Credentials;
            [MarshalAs(UnmanagedType.Bool)] public bool UnicastEnabled;
        }

        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, ref uint ip4, IntPtr ip6,
            ushort port, ushort priority, ushort weight, uint propertiesCount,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern void DnsServiceFreeInstance(IntPtr instance);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern uint DnsServiceRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern uint DnsServiceDeRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

        private readonly RegisterComplete _callback;
        private DnsServiceRegisterRequest _request;
        private IntPtr _instance = IntPtr.Zero;
        private bool _registered;

        public ServiceAnnouncer()
        {
            // Keep the callback delegate alive for as long as Windows may call it.
            _callback = delegate { };
        }

        /// <summary>The DNS-SD instance name: this PC's name.</summary>
        public static string InstanceName { get { return Environment.MachineName; } }

        /// <summary>Starts announcing. Returns false if this Windows version can't, or the address is unusable.</summary>
        public bool Start(IPAddress ipv4, int port, int tlsPort)
        {
            Stop();
            if (ipv4 == null) return false;
            try
            {
                byte[] bytes = ipv4.GetAddressBytes();
                if (bytes.Length != 4) return false;
                uint address = BitConverter.ToUInt32(bytes, 0);
                string host = InstanceName + ".local";
                string name = InstanceName + "." + ServiceType + ".local";
                string[] keys = { "v", "tls" };
                string[] values = { "2", tlsPort > 0 ? tlsPort.ToString(System.Globalization.CultureInfo.InvariantCulture) : "" };
                _instance = DnsServiceConstructInstance(name, host, ref address, IntPtr.Zero, (ushort)port, 0, 0, (uint)keys.Length, keys, values);
                if (_instance == IntPtr.Zero) return false;
                _request = new DnsServiceRegisterRequest
                {
                    Version = DnsQueryRequestVersion1,
                    InterfaceIndex = 0,
                    ServiceInstance = _instance,
                    RegisterCompletionCallback = _callback,
                    QueryContext = IntPtr.Zero,
                    Credentials = IntPtr.Zero,
                    UnicastEnabled = false
                };
                uint status = DnsServiceRegister(ref _request, IntPtr.Zero);
                _registered = status == DnsRequestPending || status == 0;
                if (!_registered) FreeInstance();
                return _registered;
            }
            catch (EntryPointNotFoundException) { FreeInstance(); return false; }
            catch (DllNotFoundException) { FreeInstance(); return false; }
        }

        public void Stop()
        {
            if (_registered)
            {
                try { DnsServiceDeRegister(ref _request, IntPtr.Zero); } catch (Exception) { }
                _registered = false;
            }
            FreeInstance();
        }

        private void FreeInstance()
        {
            if (_instance == IntPtr.Zero) return;
            try { DnsServiceFreeInstance(_instance); } catch (Exception) { }
            _instance = IntPtr.Zero;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
