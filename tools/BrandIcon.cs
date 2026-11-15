using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Bubblecast brand mark: a white chat bubble broadcasting signal arcs on a blue-to-teal rounded tile.
// Drawn as vectors at every output size; 16-24 px use a simplified mark so the tray icon stays crisp.
//
// Regenerate the brand assets (Windows, from the repository root):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:%TEMP%\BrandIcon.exe /reference:System.Drawing.dll tools\BrandIcon.cs
//   %TEMP%\BrandIcon.exe %TEMP%\bubblecast-icons
// Then copy Bubblecast.ico to windows\Bubblecast.ico, AppIcon-1024.png to macos\Resources\,
// BrandMark.png to windows\, and logo-256.png to docs\images\logo.png. An optional second argument writes a review sheet.png.
internal static class BrandIcon
{
    private static readonly Color GradientStart = Color.FromArgb(0x2B, 0x8C, 0xF5); // sky blue
    private static readonly Color GradientEnd = Color.FromArgb(0x0E, 0xB5, 0xA6);   // teal
    private static readonly Color Ink = Color.FromArgb(0x16, 0x9E, 0xC0);           // blue-teal dots inside the bubble

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        // macOS master: Apple's grid puts the tile at 824/1024 with room for a soft shadow.
        using (Bitmap mac = Render(1024, 100f / 1024f, true)) mac.Save(Path.Combine(outDir, "AppIcon-1024.png"), ImageFormat.Png);
        // README logo and preview sheet.
        using (Bitmap logo = Render(256, 0.02f, false)) logo.Save(Path.Combine(outDir, "logo-256.png"), ImageFormat.Png);
        // In-app header mark (Windows Settings and Setup), downscaled at runtime to the exact pixel size.
        using (Bitmap mark = Render(256, 0.03f, false)) mark.Save(Path.Combine(outDir, "BrandMark.png"), ImageFormat.Png);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap frame = Render(size, size <= 32 ? 0f : 0.03f, false))
            {
                // 256 px is PNG-compressed (standard for ICO); smaller frames are 32-bit DIBs, which
                // every Windows and .NET Framework icon loader reads, including System.Drawing.Icon.
                frames.Add(size >= 256 ? EncodePng(frame) : EncodeDib(frame));
                if (size <= 48) frame.Save(Path.Combine(outDir, "preview-" + size + ".png"), ImageFormat.Png);
            }
        }
        WriteIco(Path.Combine(outDir, "Bubblecast.ico"), sizes, frames);
        if (args.Length > 1) WriteSheet(Path.Combine(args[1], "sheet.png"));
        return 0;
    }

    // Review sheet: large icon plus actual-size and 4x-zoomed small sizes on light and dark backgrounds.
    private static void WriteSheet(string path)
    {
        using (Bitmap sheet = new Bitmap(900, 560))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(250, 250, 250));
            using (SolidBrush darkBg = new SolidBrush(Color.FromArgb(9, 9, 11)))
                g.FillRectangle(darkBg, 450, 0, 450, 560);
            for (int half = 0; half < 2; half++)
            {
                int ox = half * 450;
                using (Bitmap big = Render(1024, 100f / 1024f, true))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, ox + 95, 20, 260, 260);
                }
                int x = ox + 20;
                foreach (int size in new[] { 16, 24, 32, 48 })
                {
                    using (Bitmap b = Render(size, size <= 32 ? 0f : 0.03f, false))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(b, x, 310, size, size);
                        g.DrawImage(b, x, 380, size * 4, size * 4);
                    }
                    x += size * 4 + 14;
                }
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>inset is the tile margin as a fraction of the canvas.</summary>
    private static Bitmap Render(int size, float inset, bool macShadow)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            float margin = size * inset;
            RectangleF tile = new RectangleF(margin, margin, size - margin * 2, size - margin * 2);
            float s = tile.Width / 1000f; // design units: tile is 1000 x 1000
            Func<float, float, PointF> P = (x, y) => new PointF(tile.X + x * s, tile.Y + y * s);
            float radius = 225f * s;

            if (macShadow)
            {
                for (int i = 10; i >= 1; i--)
                {
                    RectangleF shadowRect = tile;
                    shadowRect.Offset(0, 10f * s);
                    shadowRect.Inflate(i * 3f * s, i * 3f * s);
                    using (GraphicsPath sp = Rounded(shadowRect, radius + i * 3f * s))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(5, 10, 14, 40)))
                        g.FillPath(sb, sp);
                }
            }

            using (GraphicsPath tilePath = Rounded(tile, radius))
            {
                using (LinearGradientBrush bg = new LinearGradientBrush(new PointF(tile.Left, tile.Top), new PointF(tile.Right, tile.Bottom), GradientStart, GradientEnd))
                    g.FillPath(bg, tilePath);
                // Soft top highlight for depth, fading out by the middle (clamped, so it never repeats).
                using (LinearGradientBrush shine = new LinearGradientBrush(new RectangleF(tile.X, tile.Y - 1, tile.Width, tile.Height + 2), Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                {
                    shine.Blend = new Blend { Factors = new[] { 0f, 1f, 1f }, Positions = new[] { 0f, 0.55f, 1f } };
                    g.FillPath(shine, tilePath);
                }
            }

            bool tiny = size <= 24;
            bool small = size <= 48;

            // Chat bubble with a tail at the lower left.
            RectangleF bubble = tiny
                ? new RectangleF(tile.X + 140 * s, tile.Y + 330 * s, 560 * s, 430 * s)
                : new RectangleF(tile.X + 150 * s, tile.Y + 360 * s, 520 * s, 400 * s);
            using (GraphicsPath bubblePath = Rounded(bubble, bubble.Height * 0.5f))
            using (GraphicsPath tail = new GraphicsPath())
            {
                PointF a = new PointF(bubble.X + bubble.Width * 0.18f, bubble.Bottom - bubble.Height * 0.22f);
                PointF tip = new PointF(bubble.X - 10 * s, bubble.Bottom + 70 * s);
                PointF c = new PointF(bubble.X + bubble.Width * 0.42f, bubble.Bottom - bubble.Height * 0.02f);
                tail.AddBezier(a, new PointF(a.X - 20 * s, tip.Y - 60 * s), new PointF(tip.X + 10 * s, tip.Y), tip);
                tail.AddBezier(tip, new PointF(tip.X + 110 * s, tip.Y + 10 * s), new PointF(c.X - 60 * s, c.Y + 10 * s), c);
                tail.CloseFigure();
                using (Region shape = new Region(bubblePath))
                {
                    shape.Union(tail);
                    using (SolidBrush white = new SolidBrush(Color.White))
                        g.FillRegion(white, shape);
                }
            }

            // Typing dots inside the bubble (skipped where they would turn to mush).
            if (!small)
            {
                float dot = 62 * s;
                float cy = bubble.Y + bubble.Height / 2f;
                float cx = bubble.X + bubble.Width / 2f;
                using (SolidBrush ink = new SolidBrush(Ink))
                    for (int i = -1; i <= 1; i++)
                        g.FillEllipse(ink, cx + i * 120 * s - dot / 2f, cy - dot / 2f, dot, dot);
            }

            // Broadcast arcs radiating from the bubble's upper right.
            PointF origin = P(tiny ? 640 : 610, tiny ? 360 : 390);
            float stroke = (tiny ? 84f : small ? 70f : 58f) * s;
            float[] radii = tiny ? new[] { 170f } : new[] { 150f, 270f };
            for (int i = 0; i < radii.Length; i++)
            {
                float r = radii[i] * s;
                using (Pen pen = new Pen(Color.FromArgb(i == 0 ? 255 : 200, 255, 255, 255), stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, origin.X - r, origin.Y - r, r * 2, r * 2, -82f, 74f);
                }
            }
        }
        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Writes a Vista+ ICO with PNG-compressed frames.</summary>
    private static byte[] EncodePng(Bitmap frame)
    {
        using (MemoryStream png = new MemoryStream())
        {
            frame.Save(png, ImageFormat.Png);
            return png.ToArray();
        }
    }

    /// <summary>ICO DIB frame: BITMAPINFOHEADER, bottom-up premultiplied-free BGRA pixels, then a 1-bpp AND mask.</summary>
    private static byte[] EncodeDib(Bitmap frame)
    {
        int size = frame.Width;
        int maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(size); w.Write(size * 2);
            w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(size * size * 4 + maskStride * size);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                {
                    Color c = frame.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            // AND mask: opaque everywhere; the alpha channel controls transparency.
            w.Write(new byte[maskStride * size]);
            return ms.ToArray();
        }
    }

    private static void WriteIco(string path, int[] sizes, List<byte[]> frames)
    {
        using (FileStream fs = File.Create(path))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) w.Write(frame);
        }
    }
}
