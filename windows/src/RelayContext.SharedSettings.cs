using System;
using System.Globalization;
using System.Threading;
using Microsoft.Win32;

namespace Bubblecast
{
    // The Mac's notification settings, shared both ways (see SharedSettings.cs and docs/PROTOCOL.md).
    internal sealed partial class RelayContext
    {
        private const string SharedSettingsValueName = "MacSettings";
        private readonly object _sharedLock = new object();
        /// <summary>Null until the Mac first shares its settings (it needs Bubblecast 1.2 or later).</summary>
        private SharedSettings _shared;
        /// <summary>Set by the Settings window; called on the UI thread when the Mac changes the settings.</summary>
        private Action<SharedSettings> _sharedSettingsChangedByMac;

        private void LoadSharedSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsRegistryPath))
                {
                    SharedSettings loaded;
                    if (key != null && SharedSettings.TryParse(Convert.ToString(key.GetValue(SharedSettingsValueName, ""), CultureInfo.InvariantCulture), out loaded) && loaded.Revision > 0)
                        _shared = loaded;
                }
            }
            catch { _shared = null; }
        }

        private SharedSettings CurrentSharedSettings()
        {
            lock (_sharedLock) return _shared == null ? null : _shared.Clone();
        }

        /// <summary>Listener thread: settings the Mac pushed. The newer revision wins. False when malformed.</summary>
        private bool ReceiveSharedSettings(string text)
        {
            SharedSettings incoming;
            if (!SharedSettings.TryParse(text, out incoming) || incoming.Revision <= 0) return false;
            bool changed = false;
            lock (_sharedLock)
            {
                if (_shared == null || incoming.Revision > _shared.Revision)
                {
                    _shared = incoming;
                    changed = true;
                    try { WriteSharedSettings(incoming); }
                    catch { QueueStatus("Couldn't store the Mac's notification settings on this PC."); }
                }
            }
            if (changed)
            {
                SharedSettings copy = incoming.Clone();
                QueueUiAction(delegate
                {
                    if (_sharedSettingsChangedByMac != null) _sharedSettingsChangedByMac(copy);
                });
            }
            return true;
        }

        /// <summary>Response headers carrying this PC's copy of the settings, signed for this request's nonce.</summary>
        private string SignedSharedSettingsHeaders(string nonce)
        {
            SharedSettings current = CurrentSharedSettings();
            // "rev=0" tells a 1.2 Mac that this PC has nothing yet, so it sends its own copy.
            string text = current == null ? "rev=0" : current.Encode();
            string token;
            lock (_tokenLock) token = _token;
            return "X-Relay-Settings: " + text + "\r\nX-Relay-Settings-Signature: " + SharedSettings.ResponseSignature(token, nonce, text) + "\r\n";
        }

        /// <summary>UI thread: the user changed the Mac's settings here. The Mac picks them up on its next request.</summary>
        private void SaveSharedSettingsFromUi(SharedSettings edited)
        {
            lock (_sharedLock)
            {
                if (_shared != null && _shared.SameValues(edited)) return;
                SharedSettings stamped = edited.Clone();
                stamped.Revision = Math.Max(SharedSettings.NowRevision(), _shared == null ? 1 : _shared.Revision + 1);
                WriteSharedSettings(stamped);
                _shared = stamped;
            }
        }

        private static void WriteSharedSettings(SharedSettings settings)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath))
                key.SetValue(SharedSettingsValueName, settings.Encode(), RegistryValueKind.String);
        }

        private string GetSharedSettingsStatusText()
        {
            if (CurrentSharedSettings() == null)
                return "Waiting for the Mac. These settings appear once Bubblecast 1.2 or later on the Mac checks in.";
            // Every accepted request carries the settings back, so the Mac's last check-in is the last sync.
            long ticks = Interlocked.Read(ref _lastSignedRequestUtcTicks);
            return "Shared with the Mac. Changes you save here reach it within a minute" +
                (ticks == 0 ? "." : "; last synced " + FormatElapsed(ticks) + " ago.");
        }
    }
}
