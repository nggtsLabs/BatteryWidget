using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryWidget
{
    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(30, 31, 34);
        public static readonly Color Track = Color.FromArgb(58, 60, 66);
        public static readonly Color Text = Color.FromArgb(235, 236, 240);
        public static readonly Color Muted = Color.FromArgb(150, 153, 162);
        public static readonly Color Good = Color.FromArgb(76, 194, 116);
        public static readonly Color Mid = Color.FromArgb(232, 178, 58);
        public static readonly Color Low = Color.FromArgb(232, 84, 72);
        public static readonly Color Charging = Color.FromArgb(78, 168, 236);
        public static readonly Color Offline = Color.FromArgb(110, 113, 122);

        public static Color ForStatus(BatteryStatus s)
        {
            Color c;
            if (!s.Online) return Offline;
            else if (s.Charging) c = Charging;
            else if (s.Percent <= 20) c = Low;
            else if (s.Percent <= 50) c = Mid;
            else c = Good;
            // Letzter bekannter Wert (Gerät schläft): gleiche Farbe, aber gedämpft
            return s.Stale ? Blend(c, Offline, 0.55f) : c;
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        // 4x7-Pixelziffern: bei 16 px Icongröße deutlich schärfer als jede Schrift
        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { ".##.", "#..#", "#..#", "#..#", "#..#", "#..#", ".##." } },
            { '1', new[] { "..#.", ".##.", "..#.", "..#.", "..#.", "..#.", ".###" } },
            { '2', new[] { ".##.", "#..#", "...#", "..#.", ".#..", "#...", "####" } },
            { '3', new[] { "###.", "...#", "...#", ".##.", "...#", "...#", "###." } },
            { '4', new[] { "#..#", "#..#", "#..#", "####", "...#", "...#", "...#" } },
            { '5', new[] { "####", "#...", "###.", "...#", "...#", "#..#", ".##." } },
            { '6', new[] { ".##.", "#...", "#...", "###.", "#..#", "#..#", ".##." } },
            { '7', new[] { "####", "...#", "..#.", "..#.", ".#..", ".#..", ".#.." } },
            { '8', new[] { ".##.", "#..#", "#..#", ".##.", "#..#", "#..#", ".##." } },
            { '9', new[] { ".##.", "#..#", "#..#", ".###", "...#", "...#", ".##." } },
            { '-', new[] { "....", "....", "....", "####", "....", "....", "...." } },
            { '?', new[] { ".##.", "#..#", "...#", "..#.", "..#.", "....", "..#." } },
        };
        const int GlyphW = 4, GlyphH = 7;

        /// <summary>Ein Tray-Icon für alle Geräte: je Gerät eine Zeile mit der Prozentzahl in Statusfarbe.</summary>
        public static Icon RenderTrayIcon(List<BatteryStatus> list)
        {
            int size = SystemInformation.SmallIconSize.Width;
            int px = Math.Max(1, size / 16);
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    var rows = list.Count > 0 ? list.Take(2).ToList() : null;
                    int gap = 2 * px;
                    int rowH = GlyphH * px;
                    int count = rows == null ? 1 : rows.Count;
                    int y = (size - (count * rowH + (count - 1) * gap)) / 2;
                    if (rows == null)
                        DrawPixelText(g, "?", Offline, size, y, px);
                    else
                        foreach (var s in rows)
                        {
                            DrawPixelText(g, s.Online ? Math.Min(100, s.Percent).ToString() : "--", ForStatus(s), size, y, px);
                            y += rowH + gap;
                        }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static void DrawPixelText(Graphics g, string text, Color color, int size, int y, int px)
        {
            int width = (text.Length * (GlyphW + 1) - 1) * px;
            int x = (size - width) / 2;
            using (var b = new SolidBrush(color))
                foreach (char c in text)
                {
                    string[] glyph;
                    if (Glyphs.TryGetValue(c, out glyph))
                        for (int r = 0; r < GlyphH; r++)
                            for (int col = 0; col < GlyphW; col++)
                                if (glyph[r][col] == '#') g.FillRectangle(b, x + col * px, y + r * px, px, px);
                    x += (GlyphW + 1) * px;
                }
        }

        public static void FreeIcon(Icon icon)
        {
            if (icon == null) return;
            IntPtr h = icon.Handle;
            icon.Dispose();
            DestroyIcon(h);
        }
    }
}
