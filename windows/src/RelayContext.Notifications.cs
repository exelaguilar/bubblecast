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
    // Quiet hours, muted senders, batching, and showing notifications.
    internal sealed partial class RelayContext
    {
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
            RecordRecent(batch);
            ShowNotification(_tray.BalloonTipTitle, _tray.BalloonTipText, false);
        }

        /// <summary>A Bubblecast status notice (not a message), such as the Mac going quiet.</summary>
        private void ShowNotice(string title, string text)
        {
            ShowNotification(title, text, true);
        }

        private void ShowNotification(string title, string text, bool isNotice)
        {
            _lastNotificationWasNotice = isNotice;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = ToolTipIcon.None;
            _tray.ShowBalloonTip(5000);
        }

        private void OnNotificationClicked()
        {
            if (_clickOpensMacDesktop && !_lastNotificationWasNotice) OpenMacDesktop();
            else ShowSettings();
        }

        private void RecordRecent(List<PendingNotification> batch)
        {
            foreach (PendingNotification item in batch)
            {
                if (item.IsTest) continue;
                _recentNotifications.Insert(0, new RecentNotification
                {
                    Time = DateTime.Now,
                    Title = String.IsNullOrEmpty(item.Sender) ? "iMessage" : DisplayTextSanitizer.SanitizeAndTruncate(item.Sender, 40),
                    Text = String.IsNullOrEmpty(item.Preview) ? "New message" : DisplayTextSanitizer.SanitizeAndTruncate(item.Preview, 60)
                });
            }
            while (_recentNotifications.Count > RecentNotificationLimit)
                _recentNotifications.RemoveAt(_recentNotifications.Count - 1);
        }

        /// <summary>Rebuilds the tray's "Recent notifications" submenu (memory only; cleared on exit).</summary>
        private void FillRecentNotificationsMenu(ToolStripMenuItem menu)
        {
            menu.DropDownItems.Clear();
            if (_recentNotifications.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No notifications yet") { Enabled = false });
                return;
            }
            foreach (RecentNotification item in _recentNotifications)
            {
                string label = item.Time.ToString("t", CultureInfo.CurrentCulture) + "  " + item.Title + ": " + item.Text;
                menu.DropDownItems.Add(new ToolStripMenuItem(label.Replace("&", "&&")) { Enabled = false });
            }
            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add("Clear", null, delegate { _recentNotifications.Clear(); });
        }
    }
}
