using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryWidget
{
    /// <summary>Hover-Fenster über dem Tray-Icon: eine Zeile pro Gerät mit Name, Prozent und Balken.</summary>
    sealed class PopupForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;     // nicht in Taskleiste/Alt+Tab
        const int WS_EX_TOPMOST = 0x8;
        const int WS_EX_NOACTIVATE = 0x8000000; // nimmt nie den Fokus
        const int WS_EX_TRANSPARENT = 0x20;     // Klicks gehen durch

        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        List<BatteryStatus> items = new List<BatteryStatus>();
        readonly float scale;
        readonly Font nameFont, valueFont, symbolFont;

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Background;
            Opacity = 0.96;
            DoubleBuffered = true;
            Text = "Akkustand";

            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            nameFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            valueFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular);
            symbolFont = new Font("Segoe UI Symbol", 8.5f, FontStyle.Regular);
            UpdateLayout();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        /// <summary>Zeigt das Fenster neben dem Tray-Icon, auf der Seite, wo die Taskleiste Platz lässt.</summary>
        public void ShowNear(Rectangle icon)
        {
            var screen = Screen.FromPoint(new Point(icon.X + icon.Width / 2, icon.Y + icon.Height / 2));
            var work = screen.WorkingArea;
            var bounds = screen.Bounds;
            int margin = S(8);
            int x, y;
            if (work.Bottom < bounds.Bottom) // Taskleiste unten
            {
                x = icon.X + icon.Width / 2 - Width / 2;
                // Icon im Überlauf-Bereich ("ausgeblendete Symbole") liegt über der Taskleiste
                y = Math.Min(work.Bottom, icon.Top) - Height - margin;
            }
            else if (work.Top > bounds.Top) // oben
            {
                x = icon.X + icon.Width / 2 - Width / 2;
                y = work.Top + margin;
            }
            else if (work.Left > bounds.Left) // links
            {
                x = work.Left + margin;
                y = icon.Y + icon.Height / 2 - Height / 2;
            }
            else // rechts oder automatisch ausgeblendet
            {
                x = icon.X + icon.Width / 2 - Width / 2;
                y = icon.Y - Height - margin;
                if (y < work.Top) y = icon.Bottom + margin;
            }
            x = Math.Max(work.Left + margin, Math.Min(x, work.Right - Width - margin));
            y = Math.Max(work.Top + margin, Math.Min(y, work.Bottom - Height - margin));
            Location = new Point(x, y);
            if (!Visible) Show();
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        public void SetItems(List<BatteryStatus> list)
        {
            items = list;
            UpdateLayout();
            Invalidate();
        }

        void UpdateLayout()
        {
            int rows = Math.Max(1, items.Count);
            Size = new Size(S(230), S(14) + rows * S(44) + S(4));
            var rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, S(14), S(14));
            Region = Region.FromHrgn(rgn);
            DeleteObject(rgn);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int pad = S(14);
            int y = S(10);
            int w = Width - 2 * pad;

            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "Suche Geräte …", nameFont, new Point(pad, y + S(12)), Theme.Muted);
                return;
            }

            foreach (var s in items)
            {
                var color = Theme.ForStatus(s);

                // Zeile 1: Name links, Prozent rechts
                TextRenderer.DrawText(g, s.Name, nameFont, new Rectangle(pad, y, w - S(60), S(20)), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                string value = s.Online ? s.Percent + " %" : "aus";
                TextRenderer.DrawText(g, value, valueFont, new Rectangle(pad, y, w, S(20)), s.Online && !s.Stale ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                // Symbol links neben dem Wert: ⚡ lädt, ☾ Ruhemodus (letzter bekannter Wert)
                string symbol = !s.Online ? null : s.Stale ? "☾" : s.Charging ? "⚡" : null;
                if (symbol != null)
                {
                    var vw = TextRenderer.MeasureText(value, valueFont).Width;
                    TextRenderer.DrawText(g, symbol, symbolFont, new Rectangle(pad, y, w - vw + S(4), S(20)),
                        s.Stale ? Theme.Muted : Theme.Charging, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }

                // Zeile 2: Balken
                int barY = y + S(24);
                int barH = S(6);
                using (var track = new SolidBrush(Theme.Track))
                    FillRounded(g, track, new RectangleF(pad, barY, w, barH));
                if (s.Online && s.Percent > 0)
                    using (var fill = new SolidBrush(color))
                        FillRounded(g, fill, new RectangleF(pad, barY, Math.Max(barH, w * s.Percent / 100f), barH));

                y += S(44);
            }
        }

        static void FillRounded(Graphics g, Brush b, RectangleF r)
        {
            float d = r.Height;
            using (var p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, d, d, 90, 180);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                nameFont.Dispose();
                valueFont.Dispose();
                symbolFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
