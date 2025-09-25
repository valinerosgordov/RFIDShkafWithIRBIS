using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace LibraryTerminal
{
    public static class PortResolver
    {
        // "COM5" -> "COM5"
        // "auto:VID_1A86&PID_7523,index=1,name=Card" -> ищем по VID/PID/имени
        public static string Resolve(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting)) return null;

            if (!setting.StartsWith("auto:", StringComparison.OrdinalIgnoreCase))
                return setting.Trim(); // явный COMx

            AutoParams pars = ParseAuto(setting.Substring(5));
            List<SerialPnP> list = QueryPnPSerials();

            IEnumerable<SerialPnP> q = list;

            if (!string.IsNullOrEmpty(pars.VidPid))
                q = q.Where(x => x.HardwareIds.Any(h =>
                        h.IndexOf(pars.VidPid, StringComparison.OrdinalIgnoreCase) >= 0));

            if (!string.IsNullOrEmpty(pars.NameSub))
                q = q.Where(x =>
                        ((x.Caption ?? "").IndexOf(pars.NameSub, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        ((x.Description ?? "").IndexOf(pars.NameSub, StringComparison.OrdinalIgnoreCase) >= 0));

            SerialPnP[] arr = q.OrderBy(x => x.Port).ToArray();
            if (arr.Length == 0) return null;

            if (pars.Index.HasValue && pars.Index.Value >= 0 && pars.Index.Value < arr.Length)
                return arr[pars.Index.Value].Port;

            return arr[0].Port;
        }

        // --- вспомогательные типы/методы ---

        private class AutoParams
        {
            public string VidPid;
            public int? Index;
            public string NameSub;
        }

        private static AutoParams ParseAuto(string s)
        {
            // пример строки: "VID_1A86&PID_7523,index=1,name=Card"
            AutoParams p = new AutoParams();
            string[] parts = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                    p.VidPid = t;
                else if (t.StartsWith("index=", StringComparison.OrdinalIgnoreCase))
                {
                    int tmp;
                    if (int.TryParse(t.Substring(6), out tmp)) p.Index = tmp;
                }
                else if (t.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    p.NameSub = t.Substring(5);
            }
            return p;
        }

        private class SerialPnP
        {
            public string Port;
            public string Caption;
            public string Description;
            public List<string> HardwareIds = new List<string>();
        }

        private static List<SerialPnP> QueryPnPSerials()
        {
            var list = new List<SerialPnP>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        string cap = (obj["Caption"] as string) ?? "";
                        string desc = (obj["Description"] as string) ?? "";

                        // из строки вроде "USB-SERIAL CH340 (COM5)" выдёргиваем COM5
                        Match m = Regex.Match(cap, @"\((COM\d+)\)");
                        if (!m.Success) continue;
                        string port = m.Groups[1].Value;

                        var sp = new SerialPnP
                        {
                            Port = port,
                            Caption = cap,
                            Description = desc
                        };

                        string[] ids = obj["HardwareID"] as string[];
                        if (ids != null) sp.HardwareIds.AddRange(ids);

                        list.Add(sp);
                    } catch
                    {
                        // пропускаем «шумные» устройства
                    }
                }
            }
            return list;
        }
    }
}