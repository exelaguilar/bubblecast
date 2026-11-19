using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bubblecast
{
    // ───────────────────────── Settings window design system ─────────────────────────
    // Native WinForms controls painted with a neutral palette modeled on shadcn/ui's
    // zinc light and dark themes. High Contrast falls back to system colors.

    public enum StatusTone { Neutral, Success, Warning, Destructive }

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

    /// <summary>A thin rounded progress bar (0-100).</summary>
    internal sealed class ProgressLine : Control, IThemed
    {
        private RelayPalette _palette;
        private int _value;

        public ProgressLine()
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

        public Color ApplyPalette(RelayPalette palette, Color inherited)
        {
            _palette = palette;
            BackColor = inherited;
            Invalidate();
            return inherited;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPaint.FillOuter(e.Graphics, this);
            if (_palette == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF track = new RectangleF(0f, 0f, Width - 1f, Height - 1f);
            using (GraphicsPath path = UiPaint.RoundedRect(track, track.Height / 2f))
            using (SolidBrush brush = new SolidBrush(_palette.Muted))
                e.Graphics.FillPath(brush, path);
            float fillWidth = track.Width * _value / 100f;
            if (fillWidth <= 0f) return;
            RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, fillWidth), track.Height);
            using (GraphicsPath path = UiPaint.RoundedRect(fill, track.Height / 2f))
            using (SolidBrush brush = new SolidBrush(_palette.Primary))
                e.Graphics.FillPath(brush, path);
        }
    }

    /// <summary>Layout helpers shared by the Settings window and Setup.</summary>
    internal static class UiLayout
    {
        internal static void ApplyTheme(Form form, RelayPalette palette)
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

        internal static void ApplyPalette(Control control, RelayPalette palette, Color inherited)
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

        internal static TableLayoutPanel NewStack()
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

        internal static void AddRow(TableLayoutPanel table, Control control)
        {
            int row = table.RowStyles.Count;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowCount = row + 1;
            table.Controls.Add(control, 0, row);
        }

        internal static Label MakeText(string text, UiRole role, Font font, Padding margin)
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

        internal static void AddLabel(TableLayoutPanel table, string text, Font font)
        {
            AddRow(table, MakeText(text, UiRole.Body, font, new Padding(0, 0, 0, 6)));
        }

        internal static void AddHelp(TableLayoutPanel table, string text, Font font)
        {
            AddRow(table, MakeText(text, UiRole.Muted, font, new Padding(0, 6, 0, 12)));
        }

        internal static void AddSeparator(TableLayoutPanel table)
        {
            AddRow(table, new Hairline { Margin = new Padding(0, 0, 0, 12) });
        }

        internal static CardPanel NewCard(string title, string description, Font titleFont, Font descriptionFont)
        {
            CardPanel card = new CardPanel();
            AddRow(card, MakeText(title, UiRole.Body, titleFont, new Padding(0, 0, 0, 2)));
            AddRow(card, MakeText(description, UiRole.Muted, descriptionFont, new Padding(0, 0, 0, 14)));
            return card;
        }

        internal static TableLayoutPanel InlineRow(Control fill, params Control[] trailing)
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
        internal static TableLayoutPanel ActionRow(string title, string description, Control action, Font titleFont, Font descriptionFont)
        {
            TableLayoutPanel row = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 12),
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
    }
}
