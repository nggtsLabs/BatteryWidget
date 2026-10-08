using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BatteryWidget
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "BatteryWidget.SingleInstance", out created))
            {
                if (!created) return; // läuft schon
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly Settings settings = new Settings();
        readonly IBatterySource[] sources = { new ArctisNovaProSource(), new LogitechSource() };
        readonly Dictionary<string, BatteryStatus> devices = new Dictionary<string, BatteryStatus>();
        readonly HashSet<string> lowWarned = new HashSet<string>();
        readonly PopupForm popup = new PopupForm();
        readonly NotifyIcon tray;
        Icon trayIcon;
        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem autostartItem;
        bool polling;
        Point lastMove;
        DateTime lastPoll, hoverStart;
        bool hovering;
        const string TrayText = "Akkustand";

        public TrayApp()
        {
            autostartItem = new ToolStripMenuItem("Mit Windows starten", null, delegate
            {
                settings.Autostart = !settings.Autostart;
            });
            menu.Items.Add(new ToolStripMenuItem("Jetzt aktualisieren", null, delegate { Poll(); }));
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Beenden", null, delegate { ExitThread(); }));
            menu.Opening += delegate
            {
                HidePopup();
                autostartItem.Checked = settings.Autostart;
            };

            trayIcon = Theme.RenderTrayIcon(new List<BatteryStatus>());
            tray = new NotifyIcon
            {
                Icon = trayIcon,
                Text = TrayText, // wird geleert, solange das Hover-Fenster offen ist
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseMove += delegate { OnTrayHover(); };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Poll(); };

            hoverTimer.Interval = 100;
            hoverTimer.Tick += delegate { CheckHover(); };

            pollTimer.Interval = settings.PollSeconds * 1000;
            pollTimer.Tick += delegate { Poll(); };
            pollTimer.Start();
            Poll();
        }

        // --- Hover-Fenster ---

        void OnTrayHover()
        {
            lastMove = Cursor.Position;
            if (hovering || menu.Visible) return;
            hovering = true;
            hoverStart = DateTime.Now;
            hoverTimer.Start();

            // Wer hovert, bewegt die Maus – sie ist also wach. Frischen Wert schon jetzt holen,
            // damit er da ist, wenn das Fenster nach der Verzögerung erscheint (höchstens alle 10 s).
            if ((DateTime.Now - lastPoll).TotalSeconds >= 10) Poll();
        }

        void CheckHover()
        {
            var r = IconRect();
            r.Inflate(2, 2);
            if (!r.Contains(Cursor.Position))
            {
                HidePopup();
                return;
            }
            if (!popup.Visible && (DateTime.Now - hoverStart).TotalMilliseconds >= settings.HoverDelayMs)
            {
                // Kleinen Windows-Tooltip ausblenden, solange das große Fenster offen ist
                tray.Text = "";
                popup.SetItems(Ordered());
                popup.ShowNear(IconRect());
            }
        }

        void HidePopup()
        {
            hoverTimer.Stop();
            hovering = false;
            if (popup.Visible) popup.Hide();
            tray.Text = TrayText;
        }

        /// <summary>Position des Tray-Icons auf dem Bildschirm (Shell_NotifyIconGetRect); Fallback: Bereich um den Mauszeiger.</summary>
        Rectangle IconRect()
        {
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var window = (NativeWindow)typeof(NotifyIcon).GetField("window", flags).GetValue(tray);
                var id = new NOTIFYICONIDENTIFIER
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
                    hWnd = window.Handle,
                    uID = (uint)(int)typeof(NotifyIcon).GetField("id", flags).GetValue(tray),
                };
                RECT rc;
                if (Shell_NotifyIconGetRect(ref id, out rc) == 0)
                    return Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
            }
            catch (Exception)
            {
            }
            int half = SystemInformation.SmallIconSize.Width;
            return new Rectangle(lastMove.X - half, lastMove.Y - half, 2 * half, 2 * half);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll")]
        static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

        // --- Abfrage ---

        void Poll()
        {
            if (polling) return;
            polling = true;
            lastPoll = DateTime.Now;
            Task.Factory.StartNew(() =>
            {
                var all = new List<BatteryStatus>();
                foreach (var src in sources)
                {
                    try { all.AddRange(src.Poll()); }
                    catch (Exception) { }
                }
                return all;
            }).ContinueWith(t =>
            {
                polling = false;
                if (t.Status == TaskStatus.RanToCompletion) Apply(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Apply(List<BatteryStatus> results)
        {
            var seen = new HashSet<string>();
            foreach (var s in results)
            {
                seen.Add(s.Key);
                devices[s.Key] = s;
            }

            // Bekannte Geräte, die gerade nicht antworten (z. B. Maus im Schlafmodus): letzten Wert gedämpft weiter anzeigen.
            // Der Akku ändert sich im Schlaf praktisch nicht. Echtes "aus" (Headset meldet es selbst) kommt oben über Online=false.
            foreach (var key in devices.Keys.ToList())
            {
                if (seen.Contains(key)) continue;
                var old = devices[key];
                devices[key] = new BatteryStatus
                {
                    Key = key,
                    Name = old.Name,
                    Online = old.Online,
                    Percent = old.Percent,
                    Stale = true,
                    LastSeen = old.LastSeen,
                    Detail = old.Online ? "Ruhemodus · Stand " + old.LastSeen.ToString("HH:mm") : old.Detail,
                };
            }

            var list = Ordered();
            var oldIcon = trayIcon;
            trayIcon = Theme.RenderTrayIcon(list);
            tray.Icon = trayIcon;
            Theme.FreeIcon(oldIcon);

            if (popup.Visible) popup.SetItems(list);
            foreach (var s in list) CheckLow(s);
        }

        /// <summary>Headset immer zuerst (oben im Icon), danach die übrigen Geräte nach Name.</summary>
        List<BatteryStatus> Ordered()
        {
            return devices.Values
                .OrderBy(s => s.Key == "arctis" ? 0 : 1)
                .ThenBy(s => s.Name)
                .ToList();
        }

        void CheckLow(BatteryStatus s)
        {
            int limit = settings.LowBatteryPercent;
            if (!s.Online || s.Stale) return;
            if (s.Charging || s.Percent > limit + 5)
            {
                lowWarned.Remove(s.Key);
                return;
            }
            if (s.Percent <= limit && lowWarned.Add(s.Key))
            {
                tray.BalloonTipTitle = "Akku fast leer";
                tray.BalloonTipText = string.Format("{0}: noch {1} %", s.Name, s.Percent);
                tray.BalloonTipIcon = ToolTipIcon.Warning;
                tray.ShowBalloonTip(5000);
            }
        }

        protected override void ExitThreadCore()
        {
            pollTimer.Stop();
            hoverTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            Theme.FreeIcon(trayIcon);
            popup.Dispose();
            base.ExitThreadCore();
        }
    }
}
