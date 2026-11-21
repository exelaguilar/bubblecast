using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bubblecast
{
    // The Settings window. Shared controls and layout helpers are in windows/shared/BubblecastUi.cs.
    internal sealed partial class RelayContext
    {
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
            form.ClientSize = new Size(1080, 800);
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
            TableLayoutPanel headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(24, 0, 24, 0), Margin = Padding.Empty };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
            TableLayoutPanel titleStack = UiLayout.NewStack();
            titleStack.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            UiLayout.AddRow(titleStack, UiLayout.MakeText("Bubblecast", UiRole.Body, titleFont, Padding.Empty));
            UiLayout.AddRow(titleStack, UiLayout.MakeText("Private LAN notifications from your Mac VM", UiRole.Muted, baseFont, Padding.Empty));
            BadgeLabel pairing = new BadgeLabel { Name = "PairingText", Font = labelFont, Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0), AccessibleName = "Mac pairing status" };
            pairing.Text = GetPairingStatusText();
            pairing.Tone = GetPairingTone();
            _pairingLabel = pairing;
            headerLayout.Controls.Add(logo, 0, 0);
            headerLayout.Controls.Add(titleStack, 1, 0);
            SegmentedControl pages = new SegmentedControl(labelFont, "This PC", "Mac") { Anchor = AnchorStyles.Right, AccessibleName = "Settings page" };
            headerLayout.Controls.Add(pages, 2, 0);
            headerLayout.Controls.Add(pairing, 3, 0);
            header.Controls.Add(headerLayout);
            header.Controls.Add(new Hairline { Dock = DockStyle.Bottom });

            // ── Pairing card: everything the Mac needs, above the fold ──
            CardPanel pairingCard = UiLayout.NewCard("Pairing", "Bubblecast on the Mac can find this PC on your network. Paste the pairing token there.", sectionFont, smallFont);

            string addressText = NetworkAddresses.DescribeLocalIPv4();
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
            UiLayout.AddLabel(pairingCard, "This PC's address", labelFont);
            UiLayout.AddRow(pairingCard, new FieldHost(ips, Math.Min(addressLines, 4)));
            NativeTheme.FollowAppTheme(ips);
            UiLayout.AddHelp(pairingCard, "Adapters with a default gateway are listed first as likely LAN addresses.", smallFont);

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
            UiLayout.AddLabel(pairingCard, "Pairing token", labelFont);
            UiLayout.AddRow(pairingCard, UiLayout.InlineRow(new FieldHost(tokenBox, 1), showToken, copy));
            UiLayout.AddHelp(pairingCard, "Keep this private. The Mac signs every request with it. Copies clear from the clipboard after one minute.", smallFont);

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
            UiLayout.AddLabel(pairingCard, "TLS certificate fingerprint", labelFont);
            UiLayout.AddRow(pairingCard, UiLayout.InlineRow(new FieldHost(tlsFingerprint, 1), copyFingerprint));
            UiLayout.AddHelp(pairingCard, "SHA-256. The Mac learns this when you first save with TLS on (port 8766); check that both show the same value.", smallFont);

            UiLayout.AddSeparator(pairingCard);
            RelayButton openMac = new RelayButton("Open Mac desktop", ButtonKind.Secondary) { AccessibleName = "Open Mac Remote Desktop" };
            openMac.Click += delegate { OpenMacDesktop(); };
            UiLayout.AddRow(pairingCard, UiLayout.ActionRow("Mac Remote Desktop", "Connects to the last Mac relay IP on port 3390, or to the override in Advanced.", openMac, labelFont, smallFont));

            // ── Notifications card: routine choices ──
            CardPanel notifyCard = UiLayout.NewCard("Notifications", "Stored for this Windows account; changes apply when you save. Previews may appear in Windows notification history.", sectionFont, smallFont);

            RelaySwitch quietHours = new RelaySwitch { Checked = _quietHoursEnabled };
            UiLayout.AddRow(notifyCard, UiLayout.ActionRow("Quiet hours", "Suppress notifications during these hours. Messages are not queued.", quietHours, labelFont, smallFont));

            TextBox quietStart = new TextBox { Text = QuietHoursText.Format(_quietHoursStart), MaxLength = 5, TextAlign = HorizontalAlignment.Center, AccessibleName = "Quiet hours start, 24-hour HH:mm" };
            TextBox quietEnd = new TextBox { Text = QuietHoursText.Format(_quietHoursEnd), MaxLength = 5, TextAlign = HorizontalAlignment.Center, AccessibleName = "Quiet hours end, 24-hour HH:mm" };
            TableLayoutPanel quietRow = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            for (int column = 0; column < 4; column++) quietRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            quietRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            quietRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label fromLabel = UiLayout.MakeText("From", UiRole.Muted, baseFont, new Padding(0, 0, 8, 0));
            Label untilLabel = UiLayout.MakeText("until", UiRole.Muted, baseFont, new Padding(12, 0, 8, 0));
            fromLabel.Anchor = AnchorStyles.Left;
            untilLabel.Anchor = AnchorStyles.Left;
            FieldHost quietStartHost = new FieldHost(quietStart, 1) { PreferredWidth = 72, Anchor = AnchorStyles.Left };
            FieldHost quietEndHost = new FieldHost(quietEnd, 1) { PreferredWidth = 72, Anchor = AnchorStyles.Left };
            quietRow.Controls.Add(fromLabel, 0, 0);
            quietRow.Controls.Add(quietStartHost, 1, 0);
            quietRow.Controls.Add(untilLabel, 2, 0);
            quietRow.Controls.Add(quietEndHost, 3, 0);
            UiLayout.AddRow(notifyCard, quietRow);
            UiLayout.AddHelp(notifyCard, "24-hour time on this PC's clock. For overnight quiet hours, set From later than until.", smallFont);

            UiLayout.AddSeparator(notifyCard);
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
            UiLayout.AddLabel(notifyCard, "Muted contacts", labelFont);
            UiLayout.AddRow(notifyCard, mutedHost);
            UiLayout.AddHelp(notifyCard, "One name per line. A name must exactly match the sender shown in alerts.", smallFont);

            UiLayout.AddSeparator(notifyCard);
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
            UiLayout.AddRow(notifyCard, UiLayout.ActionRow("Start when I sign in to Windows", "Opens quietly in the notification area. Applies immediately.", startup, labelFont, smallFont));
            UiLayout.AddSeparator(notifyCard);
            RelaySwitch macQuietAlert = new RelaySwitch { Checked = _macQuietAlertEnabled };
            UiLayout.AddRow(notifyCard, UiLayout.ActionRow("Alert me when the Mac goes quiet", "One notice if the Mac stops checking in for 5 minutes, and one when it's back.", macQuietAlert, labelFont, smallFont));

            // ── Advanced: infrequent maintenance and diagnostics, collapsed by default ──
            CardPanel advancedCard = new CardPanel { Padding = new Padding(8), Margin = Padding.Empty };
            DisclosureButton advancedToggle = new DisclosureButton("Advanced", "Discovery, Remote Desktop override, listen address, allowlist, token rotation, and diagnostics", labelFont, smallFont);
            TableLayoutPanel advancedBody = UiLayout.NewStack();
            advancedBody.Padding = new Padding(12, 0, 12, 4);
            advancedBody.Visible = false;
            Action<bool> setAdvancedExpanded = delegate(bool expanded)
            {
                advancedBody.Visible = expanded;
                advancedToggle.Expanded = expanded;
            };
            advancedToggle.Click += delegate { setAdvancedExpanded(!advancedBody.Visible); };
            UiLayout.AddRow(advancedCard, advancedToggle);
            UiLayout.AddRow(advancedCard, advancedBody);

            UiLayout.AddSeparator(advancedBody);
            TextBox rdpTarget = new TextBox { Text = _macRdpTarget, AccessibleName = "Mac Remote Desktop address" };
            UiLayout.AddLabel(advancedBody, "Mac Remote Desktop address", labelFont);
            UiLayout.AddRow(advancedBody, new FieldHost(rdpTarget, 1));
            UiLayout.AddHelp(advancedBody, "Optional, such as mac-vm.local:3390. Leave blank to use the current Mac relay IP on port 3390.", smallFont);

            UiLayout.AddSeparator(advancedBody);
            RelaySwitch discoverable = new RelaySwitch { Checked = _discoverable };
            UiLayout.AddRow(advancedBody, UiLayout.ActionRow("Let the Mac find this PC", "Announces \"" + ServiceAnnouncer.InstanceName + "\" on the local network so the Mac can pair without typing an address.", discoverable, labelFont, smallFont));

            UiLayout.AddSeparator(advancedBody);
            TextBox listenAddress = new TextBox { Text = _listenAddress, AccessibleName = "Listen address" };
            UiLayout.AddLabel(advancedBody, "Listen address", labelFont);
            UiLayout.AddRow(advancedBody, new FieldHost(listenAddress, 1));
            UiLayout.AddHelp(advancedBody, "Leave blank to listen on every network adapter. Enter one of this PC's IPv4 addresses to accept connections only there, for example to ignore VPN and virtual adapters.", smallFont);

            UiLayout.AddSeparator(advancedBody);
            RelaySwitch clickOpensMac = new RelaySwitch { Checked = _clickOpensMacDesktop };
            UiLayout.AddRow(advancedBody, UiLayout.ActionRow("Clicking a notification opens the Mac", "Opens Remote Desktop to the Mac instead of Settings.", clickOpensMac, labelFont, smallFont));

            UiLayout.AddSeparator(advancedBody);
            RelaySwitch allowPairedMacOnly = new RelaySwitch { Checked = _onlyAcceptPairedMacAddress, AccessibleName = "Restrict requests to the paired Mac IP address" };
            UiLayout.AddRow(advancedBody, UiLayout.ActionRow("Only accept requests from the paired Mac IP", "Uses the last Mac IP that sent a signed request. Applies when you save.", allowPairedMacOnly, labelFont, smallFont));

            UiLayout.AddSeparator(advancedBody);
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
            UiLayout.AddRow(advancedBody, UiLayout.ActionRow("Rotate pairing token", "Replaces the token immediately. Notifications stop until the Mac has the new token.", rotateToken, labelFont, smallFont));

            UiLayout.AddSeparator(advancedBody);
            UiLayout.AddLabel(advancedBody, "Diagnostics", labelFont);
            Label metrics = UiLayout.MakeText(GetMetricsText(), UiRole.Muted, smallFont, new Padding(0, 0, 0, 12));
            metrics.Name = "MetricsText";
            metrics.AccessibleName = "Notification and health metrics";
            _metricsLabel = metrics;
            UiLayout.AddRow(advancedBody, metrics);
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
            UiLayout.AddRow(advancedBody, diagnostics);
            UiLayout.AddHelp(advancedBody, "Diagnostics exclude the pairing token, addresses, sender names, and message text.", smallFont);

            // ── Mac page: the Mac's notification settings, shared both ways (RelayContext.SharedSettings.cs) ──
            SharedSettings sharedAtOpen = CurrentSharedSettings() ?? new SharedSettings();
            bool applyingShared = false;
            CardPanel showCard = UiLayout.NewCard("What alerts show", "Chosen on the Mac before anything is sent, so turning these off keeps the text off the network too.", sectionFont, smallFont);
            RelaySwitch macNames = new RelaySwitch();
            UiLayout.AddRow(showCard, UiLayout.ActionRow("Sender contact name", "The Mac looks up the sender in its Contacts.", macNames, labelFont, smallFont));
            UiLayout.AddSeparator(showCard);
            RelaySwitch macPreviews = new RelaySwitch();
            UiLayout.AddRow(showCard, UiLayout.ActionRow("Message previews", "Include message text. Previews may appear in Windows notification history.", macPreviews, labelFont, smallFont));
            UiLayout.AddSeparator(showCard);
            RelaySwitch macReactions = new RelaySwitch();
            UiLayout.AddRow(showCard, UiLayout.ActionRow("Include associated-message rows", "May include reactions; rows without text get a generic label.", macReactions, labelFont, smallFont));
            UiLayout.AddSeparator(showCard);
            Label sharedStatus = UiLayout.MakeText(GetSharedSettingsStatusText(), UiRole.Muted, smallFont, new Padding(0, 0, 0, 12));
            sharedStatus.AccessibleName = "Mac settings sharing status";
            _sharedStatusLabel = sharedStatus;
            UiLayout.AddRow(showCard, sharedStatus);

            CardPanel quietCard = UiLayout.NewCard("When to stay quiet", "Skipped messages are never sent to this PC.", sectionFont, smallFont);
            RelaySwitch macSkipRead = new RelaySwitch();
            UiLayout.AddRow(quietCard, UiLayout.ActionRow("Skip messages I've already read", "Checked just before sending, so it mostly catches messages that waited while this PC was unreachable. A conversation left open in Messages on the Mac counts as read.", macSkipRead, labelFont, smallFont));
            UiLayout.AddSeparator(quietCard);
            RelaySwitch macQuietActive = new RelaySwitch();
            UiLayout.AddRow(quietCard, UiLayout.ActionRow("Stay quiet while I'm active in a chat", "No alert for a conversation you sent or read a message in recently, on any of your devices.", macQuietActive, labelFont, smallFont));
            TextBox macActiveMinutes = new TextBox { MaxLength = 2, TextAlign = HorizontalAlignment.Center, AccessibleName = "Minutes to stay quiet after activity in a conversation" };
            FieldHost macActiveMinutesHost;
            UiLayout.AddRow(quietCard, MinutesRow("For", macActiveMinutes, "minutes after you were active", baseFont, out macActiveMinutesHost));
            UiLayout.AddSeparator(quietCard);
            RelaySwitch macSkipOld = new RelaySwitch();
            UiLayout.AddRow(quietCard, UiLayout.ActionRow("Ignore old or iCloud-synced messages", "Older or unreadable timestamps are skipped. Turn this off only to catch up on the full backlog.", macSkipOld, labelFont, smallFont));
            TextBox macMaximumAge = new TextBox { MaxLength = 4, TextAlign = HorizontalAlignment.Center, AccessibleName = "Maximum message age in minutes" };
            FieldHost macMaximumAgeHost;
            UiLayout.AddRow(quietCard, MinutesRow("Older than", macMaximumAge, "minutes", baseFont, out macMaximumAgeHost));

            Control[] sharedControls = { macNames, macPreviews, macReactions, macSkipRead, macQuietActive, macActiveMinutes, macSkipOld, macMaximumAge };
            RelaySwitch[] sharedSwitches = { macNames, macPreviews, macReactions, macSkipRead, macQuietActive, macSkipOld };
            Action<SharedSettings> showShared = delegate(SharedSettings values)
            {
                applyingShared = true;
                macNames.Checked = values.IncludeSenderNames;
                macPreviews.Checked = values.IncludeMessagePreviews;
                macReactions.Checked = values.NotifyForReactions;
                macSkipRead.Checked = values.SkipReadMessages;
                macQuietActive.Checked = values.QuietWhileActive;
                macActiveMinutes.Text = values.ActiveMinutes.ToString(CultureInfo.InvariantCulture);
                macSkipOld.Checked = values.SuppressOldMessages;
                macMaximumAge.Text = values.MaximumMessageAgeMinutes.ToString(CultureInfo.InvariantCulture);
                bool available = CurrentSharedSettings() != null;
                foreach (RelaySwitch toggle in sharedSwitches) toggle.Enabled = available;
                macActiveMinutesHost.Dimmed = !(available && values.QuietWhileActive);
                macMaximumAgeHost.Dimmed = !(available && values.SuppressOldMessages);
                applyingShared = false;
            };
            showShared(sharedAtOpen);
            macQuietActive.CheckedChanged += delegate { macActiveMinutesHost.Dimmed = !(macQuietActive.Enabled && macQuietActive.Checked); };
            macSkipOld.CheckedChanged += delegate { macMaximumAgeHost.Dimmed = !(macSkipOld.Enabled && macSkipOld.Checked); };

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
            Label unsaved = UiLayout.MakeText("Unsaved changes", UiRole.Muted, smallFont, new Padding(12, 0, 12, 0));
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
                if (!NetworkAddresses.IsUsableListenAddress(listenAddress.Text))
                {
                    setAdvancedExpanded(true);
                    MessageBox.Show("Leave the listen address blank, or enter one of this PC's IPv4 addresses: " + String.Join(", ", NetworkAddresses.LocalIPv4().ToArray()) + ".", "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    listenAddress.Focus();
                    return;
                }
                int activeMinutes;
                int maximumAge;
                bool sharedAvailable = CurrentSharedSettings() != null;
                if (sharedAvailable && (!Int32.TryParse(macActiveMinutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out activeMinutes) || activeMinutes < 1 || activeMinutes > 60))
                {
                    pages.SelectedIndex = 1;
                    MessageBox.Show("Enter how long to stay quiet as 1 to 60 minutes.", "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    macActiveMinutes.Focus();
                    return;
                }
                if (sharedAvailable && (!Int32.TryParse(macMaximumAge.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out maximumAge) || maximumAge < 1 || maximumAge > 1440))
                {
                    pages.SelectedIndex = 1;
                    MessageBox.Show("Enter the maximum message age as 1 to 1440 minutes.", "Bubblecast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    macMaximumAge.Focus();
                    return;
                }
                try
                {
                    if (sharedAvailable)
                    {
                        SaveSharedSettingsFromUi(new SharedSettings
                        {
                            IncludeSenderNames = macNames.Checked,
                            IncludeMessagePreviews = macPreviews.Checked,
                            NotifyForReactions = macReactions.Checked,
                            SkipReadMessages = macSkipRead.Checked,
                            QuietWhileActive = macQuietActive.Checked,
                            ActiveMinutes = Int32.Parse(macActiveMinutes.Text.Trim(), CultureInfo.InvariantCulture),
                            SuppressOldMessages = macSkipOld.Checked,
                            MaximumMessageAgeMinutes = Int32.Parse(macMaximumAge.Text.Trim(), CultureInfo.InvariantCulture)
                        });
                        showShared(CurrentSharedSettings());
                    }
                    bool listenChanged = SaveSettings(quietHours.Checked, validatedStart, validatedEnd, mutedSenders.Text, validatedTarget, allowPairedMacOnly.Checked, _lastMacAddress,
                        macQuietAlert.Checked, clickOpensMac.Checked, listenAddress.Text, discoverable.Checked);
                    quietStart.Text = QuietHoursText.Format(validatedStart);
                    quietEnd.Text = QuietHoursText.Format(validatedEnd);
                    listenAddress.Text = _listenAddress;
                    unsaved.Visible = false;
                    if (listenChanged) RestartListeners();
                    else QueueStatus("Settings saved. The listener is receiving signed requests.");
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

            EventHandler markUnsaved = delegate { if (!applyingShared) unsaved.Visible = true; };
            foreach (Control control in sharedControls)
            {
                RelaySwitch toggle = control as RelaySwitch;
                if (toggle != null) toggle.CheckedChanged += markUnsaved;
                else control.TextChanged += markUnsaved;
            }
            // The Mac changed its settings while this window is open: show them unless there are unsaved edits here.
            _sharedSettingsChangedByMac = delegate(SharedSettings values)
            {
                if (form.IsDisposed) return;
                if (unsaved.Visible)
                {
                    QueueStatus("The Mac changed its notification settings. Saving here will replace them with what's shown.");
                    return;
                }
                showShared(values);
            };
            quietHours.CheckedChanged += markUnsaved;
            quietStart.TextChanged += markUnsaved;
            quietEnd.TextChanged += markUnsaved;
            mutedSenders.TextChanged += markUnsaved;
            rdpTarget.TextChanged += markUnsaved;
            allowPairedMacOnly.CheckedChanged += markUnsaved;
            macQuietAlert.CheckedChanged += markUnsaved;
            clickOpensMac.CheckedChanged += markUnsaved;
            listenAddress.TextChanged += markUnsaved;
            discoverable.CheckedChanged += markUnsaved;

            // ── Scrolling body: tracks the window width; two columns when there is room ──
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = UiRole.Page };
            NativeTheme.FollowAppTheme(scroll);
            TableLayoutPanel page = UiLayout.NewStack();
            page.Dock = DockStyle.Top;
            page.Padding = new Padding(24, 16, 24, 4);
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
                    UiLayout.AddRow(page, pairingCard);
                    UiLayout.AddRow(page, notifyCard);
                    UiLayout.AddRow(page, advancedCard);
                    page.SetColumnSpan(advancedCard, 1);
                }
                page.ResumeLayout(true);
            };
            updatePageLayout();
            scroll.Resize += delegate { updatePageLayout(); };

            Panel macScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = UiRole.Page, Visible = false };
            NativeTheme.FollowAppTheme(macScroll);
            TableLayoutPanel macPage = UiLayout.NewStack();
            macPage.Dock = DockStyle.Top;
            macPage.Padding = new Padding(24, 16, 24, 4);
            macScroll.Controls.Add(macPage);
            bool? macWideLayout = null;
            Action updateMacLayout = delegate
            {
                bool wide = macScroll.ClientSize.Width >= UiPaint.Px(940);
                if (macWideLayout.HasValue && macWideLayout.Value == wide) return;
                macWideLayout = wide;
                macPage.SuspendLayout();
                macPage.Controls.Clear();
                macPage.ColumnStyles.Clear();
                macPage.RowStyles.Clear();
                if (wide)
                {
                    macPage.ColumnCount = 2;
                    macPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                    macPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                    macPage.RowCount = 1;
                    macPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    showCard.Margin = new Padding(0, 0, 8, 16);
                    quietCard.Margin = new Padding(8, 0, 0, 16);
                    macPage.Controls.Add(showCard, 0, 0);
                    macPage.Controls.Add(quietCard, 1, 0);
                }
                else
                {
                    macPage.ColumnCount = 1;
                    macPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                    macPage.RowCount = 0;
                    showCard.Margin = new Padding(0, 0, 0, 16);
                    quietCard.Margin = new Padding(0, 0, 0, 16);
                    UiLayout.AddRow(macPage, showCard);
                    UiLayout.AddRow(macPage, quietCard);
                }
                macPage.ResumeLayout(true);
            };
            // Lay the Mac page out now, so its cards are in the window when the theme is applied below.
            updateMacLayout();
            macScroll.Resize += delegate { updateMacLayout(); };
            pages.SelectedIndexChanged += delegate
            {
                bool showMac = pages.SelectedIndex == 1;
                form.SuspendLayout();
                macScroll.Visible = showMac;
                scroll.Visible = !showMac;
                form.ResumeLayout(true);
                if (showMac) updateMacLayout();
                else updatePageLayout();
            };

            form.Controls.Add(macScroll);
            form.Controls.Add(scroll);
            form.Controls.Add(header);
            form.Controls.Add(footer);

            UiLayout.ApplyTheme(form, RelayPalette.Current());
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
                    if (!form.IsDisposed) UiLayout.ApplyTheme(form, RelayPalette.Current());
                });
            };
            SystemEvents.UserPreferenceChanged += themeChanged;
            form.FormClosed += delegate
            {
                SystemEvents.UserPreferenceChanged -= themeChanged;
                _sharedSettingsChangedByMac = null;
                _sharedStatusLabel = null;
                copyResetTimer.Stop();
                copyResetTimer.Dispose();
                fingerprintResetTimer.Stop();
                fingerprintResetTimer.Dispose();
                clearTokenTimer.Stop();
                clearTokenTimer.Dispose();
            };
            // The controls use these fonts until the form and its controls are disposed.
            form.Disposed += delegate
            {
                foreach (Font font in new[] { baseFont, titleFont, sectionFont, labelFont, smallFont, monoFont }) font.Dispose();
            };
            return form;
        }

        /// <summary>A "label [number] unit" row, indented under the switch it belongs to.</summary>
        private static TableLayoutPanel MinutesRow(string label, TextBox input, string unit, Font font, out FieldHost host)
        {
            TableLayoutPanel row = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 12), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            for (int column = 0; column < 3; column++) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label before = UiLayout.MakeText(label, UiRole.Muted, font, new Padding(0, 0, 8, 0));
            Label after = UiLayout.MakeText(unit, UiRole.Muted, font, new Padding(8, 0, 0, 0));
            before.Anchor = AnchorStyles.Left;
            after.Anchor = AnchorStyles.Left;
            host = new FieldHost(input, 1) { PreferredWidth = 64, Anchor = AnchorStyles.Left };
            row.Controls.Add(before, 0, 0);
            row.Controls.Add(host, 1, 0);
            row.Controls.Add(after, 2, 0);
            return row;
        }
    }
}
