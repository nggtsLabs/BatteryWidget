using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows.Forms;

namespace BatteryWidget
{
    /// <summary>Einfache key=value-Datei unter %APPDATA%\BatteryWidget\settings.ini.</summary>
    public sealed class Settings
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "BatteryWidget";

        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public Settings()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryWidget");
            path = Path.Combine(dir, "settings.ini");
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (IOException)
            {
            }
        }

        public int LowBatteryPercent { get { return GetInt("LowBatteryPercent", 15); } }
        public int HoverDelayMs { get { return Math.Max(0, GetInt("HoverDelayMs", 1500)); } }
        public int PollSeconds { get { return Math.Max(15, GetInt("PollSeconds", 60)); } }

        public bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
        }

        bool GetBool(string key, bool def)
        {
            string v;
            return values.TryGetValue(key, out v) ? v == "1" : def;
        }

        int GetInt(string key, int def)
        {
            string v;
            int i;
            return values.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : def;
        }

        void Set(string key, bool v) { Set(key, v ? "1" : "0"); }
        void Set(string key, int v) { Set(key, v.ToString(CultureInfo.InvariantCulture)); }

        void Set(string key, string v)
        {
            values[key] = v;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                foreach (var kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(path, lines);
            }
            catch (IOException)
            {
            }
        }
    }
}
