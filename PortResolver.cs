using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using LibraryTerminal.Core;

namespace LibraryTerminal
{
    public static class PortResolver
    {
        // "COM5" -> "COM5"
        // "auto:VID_1A86&PID_7523,index=1,name=Card" -> ищем по VID/PID/имени
        public static Option<string> Resolve(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting)) 
                return Option<string>.None;

            if (!setting.StartsWith("auto:", StringComparison.OrdinalIgnoreCase))
                return Option<string>.Some(setting.Trim()); // явный COMx

            var pars = ParseAuto(setting.Substring(5));
            var list = QueryPnPSerials();

            var q = list.AsEnumerable();

            if (!string.IsNullOrEmpty(pars.VidPid))
            {
                q = q.Where(x => x.HardwareIds.Any(h =>
                    h.IndexOf(pars.VidPid, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (!string.IsNullOrEmpty(pars.NameSub))
            {
                q = q.Where(x =>
                    ((x.Caption ?? "").IndexOf(pars.NameSub, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    ((x.Description ?? "").IndexOf(pars.NameSub, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var arr = q.OrderBy(x => x.Port).ToArray();
            if (arr.Length == 0) 
                return Option<string>.None;

            if (pars.Index.HasValue && pars.Index.Value >= 0 && pars.Index.Value < arr.Length)
                return Option<string>.Some(arr[pars.Index.Value].Port);

            return Option<string>.Some(arr[0].Port);
        }

        // --- вспомогательные типы/методы ---

        private record AutoParams(string VidPid, int? Index, string NameSub);

        private static AutoParams ParseAuto(string s)
        {
            // пример строки: "VID_1A86&PID_7523,index=1,name=Card"
            string vidPid = null;
            int? index = null;
            string nameSub = null;
            
            var parts = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var t = part.Trim();
                if (t.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                    vidPid = t;
                else if (t.StartsWith("index=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(t.Substring(6), out var tmp)) 
                        index = tmp;
                }
                else if (t.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    nameSub = t.Substring(5);
            }
            return new AutoParams(vidPid, index, nameSub);
        }

        private record SerialPnP(string Port, string Caption, string Description, List<string> HardwareIds);

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

                        var ids = obj["HardwareID"] as string[];
                        var hardwareIds = ids != null ? new List<string>(ids) : new List<string>();
                        
                        var sp = new SerialPnP(port, cap, desc, hardwareIds);

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