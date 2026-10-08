using System;
using System.Collections.Generic;
using System.Text;

namespace BatteryWidget
{
    public sealed class BatteryStatus
    {
        public string Key;          // stabile ID für die Anzeige
        public string Name;
        public bool Online;
        public int Percent;         // 0..100
        public bool Charging;
        public string Detail;       // z. B. "Stufe 6/8"
        public bool Stale;          // antwortet gerade nicht (z. B. Maus im Schlafmodus) – letzter bekannter Wert
        public DateTime LastSeen = DateTime.Now;
    }

    public interface IBatterySource
    {
        IEnumerable<BatteryStatus> Poll();
    }

    /// <summary>
    /// SteelSeries Arctis Nova Pro Wireless (Basisstation). Protokoll wie in HeadsetControl:
    /// Anfrage 06 B0, Antwort: Byte 6 = Akkustufe 0..8, Byte 15 = Status (1 = Headset aus, 2 = lädt).
    /// </summary>
    public sealed class ArctisNovaProSource : IBatterySource
    {
        const ushort Vendor = 0x1038;
        static readonly ushort[] Products = { 0x12E0, 0x12E5 }; // PC- und Xbox-Variante
        const int MaxLevel = 8;

        public IEnumerable<BatteryStatus> Poll()
        {
            var result = new List<BatteryStatus>();
            foreach (var info in HidDevice.Enumerate(Vendor))
            {
                if (Array.IndexOf(Products, info.ProductId) < 0) continue;
                if (info.UsagePage < 0xFF00 || info.OutputReportLength < 2) continue;

                var resp = Request(info);
                if (resp == null) continue;

                var s = new BatteryStatus { Key = "arctis", Name = "Arctis Nova Pro" };
                byte state = resp[15];
                int level = Math.Min(resp[6], (byte)MaxLevel);
                s.Online = state != 0x01;
                s.Charging = state == 0x02;
                s.Percent = (int)Math.Round(level * 100.0 / MaxLevel);
                s.Detail = s.Online ? string.Format("Stufe {0}/{1}", level, MaxLevel) : "Headset aus";
                result.Add(s);
                break;
            }
            return result;
        }

        static byte[] Request(HidDeviceInfo info)
        {
            try
            {
                using (var dev = HidDevice.Open(info))
                {
                    dev.Write(new byte[] { 0x06, 0xB0 });
                    for (int i = 0; i < 10; i++)
                    {
                        var r = dev.Read(500);
                        if (r == null) return null;
                        if (r.Length > 15 && r[0] == 0x06 && r[1] == 0xB0) return r;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }

    /// <summary>
    /// Logitech-Geräte über HID++ 2.0 (Lightspeed-Empfänger oder Kabel).
    /// Nutzt Feature 0x1004 (Unified Battery), sonst 0x1000 (Battery Level Status).
    /// </summary>
    public sealed class LogitechSource : IBatterySource
    {
        const ushort Vendor = 0x046D;
        const byte LongReport = 0x11;
        const byte SoftwareId = 0x0B;
        const ushort FeatureDeviceName = 0x0005;
        const ushort FeatureBattery = 0x1000;
        const ushort FeatureUnifiedBattery = 0x1004;

        // Namen bleiben gleich, solange das Gerät da ist – nicht bei jedem Poll neu abfragen
        readonly Dictionary<string, string> nameCache = new Dictionary<string, string>();

        public IEnumerable<BatteryStatus> Poll()
        {
            var result = new List<BatteryStatus>();
            var seen = new HashSet<string>();
            foreach (var info in HidDevice.Enumerate(Vendor))
            {
                // HID++-Long-Collection: Usage Page FF00, Usage 02, 20 Byte
                if (info.UsagePage != 0xFF00 || info.Usage != 0x02 || info.OutputReportLength != 20) continue;
                try
                {
                    using (var dev = HidDevice.Open(info))
                    {
                        // Antwortet 0xFF, ist es ein per Kabel angeschlossenes Gerät (antwortet dann auf jeden Index).
                        // Sonst ist es ein Empfänger mit Geräten an Index 1..6.
                        var direct = PollDevice(dev, 0xFF);
                        var found = direct != null
                            ? new List<BatteryStatus> { direct }
                            : new List<BatteryStatus>();
                        if (direct == null)
                        {
                            for (byte index = 1; index <= 6; index++)
                            {
                                var s = PollDevice(dev, index);
                                if (s != null) found.Add(s);
                            }
                        }
                        // Gleiches Gerät per Kabel und Empfänger nur einmal anzeigen
                        foreach (var s in found)
                            if (seen.Add(s.Key)) result.Add(s);
                    }
                }
                catch (Exception)
                {
                }
            }
            return result;
        }

        BatteryStatus PollDevice(HidDevice dev, byte index)
        {
            // Ping (Root-Feature, getProtocolVersion) – antwortet nur ein eingeschaltetes HID++-2.0-Gerät
            if (Call(dev, index, 0, 1, new byte[] { 0, 0, 0x5A }, 300) == null) return null;

            string cacheKey = string.Format("{0}|{1}", dev.Info.Path, index);
            string name;
            if (!nameCache.TryGetValue(cacheKey, out name))
            {
                name = GetName(dev, index) ?? "Logitech-Gerät";
                if (name == "PRO X 2") name = "PRO X Superlight 2";
                nameCache[cacheKey] = name;
            }

            // Key über den Namen, damit die Maus per Kabel und per Funk dieselbe Anzeige behält
            var s = new BatteryStatus { Key = "logi-" + name, Name = name, Online = true };

            byte feat = GetFeatureIndex(dev, index, FeatureUnifiedBattery);
            if (feat != 0)
            {
                var r = Call(dev, index, feat, 1, null, 500); // get_status
                if (r == null) return null;
                s.Percent = r[0];
                s.Charging = r[2] == 1 || r[2] == 2;
                s.Detail = r[2] == 3 ? "Voll" : null;
                return s;
            }

            feat = GetFeatureIndex(dev, index, FeatureBattery);
            if (feat != 0)
            {
                var r = Call(dev, index, feat, 0, null, 500); // GetBatteryLevelStatus
                if (r == null) return null;
                s.Percent = r[0];
                s.Charging = r[2] >= 1 && r[2] <= 2;
                return s;
            }

            return null; // Gerät ohne Akku (z. B. Tastatur am Kabel)
        }

        string GetName(HidDevice dev, byte index)
        {
            byte feat = GetFeatureIndex(dev, index, FeatureDeviceName);
            if (feat == 0) return null;
            var count = Call(dev, index, feat, 0, null, 500);
            if (count == null) return null;
            int len = count[0];
            var sb = new StringBuilder();
            while (sb.Length < len)
            {
                var r = Call(dev, index, feat, 1, new byte[] { (byte)sb.Length }, 500);
                if (r == null) break;
                int before = sb.Length;
                for (int i = 0; i < r.Length && sb.Length < len; i++)
                {
                    if (r[i] == 0) break;
                    sb.Append((char)r[i]);
                }
                if (sb.Length == before) break;
            }
            string name = sb.ToString().Trim();
            return name.Length > 0 ? name : null;
        }

        static byte GetFeatureIndex(HidDevice dev, byte index, ushort feature)
        {
            var r = Call(dev, index, 0, 0, new byte[] { (byte)(feature >> 8), (byte)feature }, 500);
            return r == null ? (byte)0 : r[0];
        }

        /// <summary>Sendet eine HID++-Anfrage und liefert die 16 Parameter-Bytes der Antwort, oder null.</summary>
        static byte[] Call(HidDevice dev, byte index, byte feature, int function, byte[] args, int timeoutMs)
        {
            byte fnSw = (byte)((function << 4) | SoftwareId);
            var msg = new byte[20];
            msg[0] = LongReport;
            msg[1] = index;
            msg[2] = feature;
            msg[3] = fnSw;
            if (args != null) Array.Copy(args, 0, msg, 4, Math.Min(args.Length, 16));
            dev.Write(msg);

            var deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0) return null;
                var r = dev.Read(left);
                if (r == null) return null;
                if (r.Length < 20 || r[0] != LongReport || r[1] != index) continue;
                if (r[2] == 0xFF && r[3] == feature && r[4] == fnSw) return null; // HID++-Fehler
                if (r[2] == feature && r[3] == fnSw)
                {
                    var p = new byte[16];
                    Array.Copy(r, 4, p, 0, 16);
                    return p;
                }
            }
        }
    }
}
