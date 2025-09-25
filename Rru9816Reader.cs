using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LibraryTerminal
{
    /// <summary>
    /// RRU9816 через вендорскую DLL (RRU9816.dll), активный режим (буфер тегов).
    /// Делает точную попытку как в демо + расширенный перебор бодрейта/адреса.
    /// Пишет подробный лог попыток.
    /// Совместим с C# 7.3 / .NET Framework 4.8.
    /// </summary>
    public sealed class Rru9816Reader : IDisposable
    {
        public event Action<string> OnEpcHex;

        private readonly string _portName;     // "COM5" или пусто (тогда перебор/AutoOpen)
        private readonly int _baud;            // предпочтительный (115200/57600/…)
        private readonly byte _comAdr;         // адрес по конфигу (в демо чаще 0x00)

        private volatile bool _running;
        private Thread _thread;

        private int _frmIdx = -1;              // индекс COM внутри DLL
        private int _openedPortNum = 0;        // фактический номер COM
        private byte _adrInUse = 0x00;         // адрес, с которым реально открылись

        private const string DLL = "RRU9816.dll";
        private const string LOG_FILE = "rru_attempts.log";

        // ===== P/Invoke (сигнатуры как в демо RWDev.cs) =====
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int OpenComPort(int Port, ref byte ComAddr, byte Baud, ref int PortHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int AutoOpenComPort(ref int Port, ref byte ComAddr, byte Baud, ref int PortHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int CloseComPort();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int CloseSpecComPort(int Port);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetWorkMode(ref byte ComAdr, byte Read_mode, int frmComPortindex);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int ClearTagBuffer(ref byte ComAdr, int frmComPortindex);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int ReadActiveModeData(byte[] ScanModeData, ref int ValidDatalength, int frmComPortindex);
        // =====================================================

        public Rru9816Reader(string comPort, int baud = 115200, byte comAdr = 0x00)
        {
            _portName = comPort;
            _baud = baud;
            _comAdr = comAdr;
            _adrInUse = comAdr;
        }

        public void Start()
        {
            if (_running) return;

            _frmIdx = -1;
            _openedPortNum = 0;
            _adrInUse = _comAdr;

            // 0) Соберём список кандидатов COM
            var names = System.IO.Ports.SerialPort.GetPortNames(); // ["COM3","COM5",...]
            var ports = new System.Collections.Generic.List<int>();
            for (int i = 0; i < names.Length; i++)
            {
                int p = ExtractPortNumber(names[i]);
                if (p > 0 && !ports.Contains(p)) ports.Add(p);
            }
            ports.Sort();

            int desired = 0;
            if (!string.IsNullOrWhiteSpace(_portName))
            {
                desired = ExtractPortNumber(_portName);
                if (desired <= 0) throw new InvalidOperationException("RRU9816: неверный порт '" + (_portName ?? "") + "'.");
                ports.Remove(desired);
                ports.Insert(0, desired);
            }
            if (ports.Count == 0) { for (int i = 1; i <= 32; i++) ports.Add(i); }

            // 1) Коды бодрейта: preferred, 4(115200), 5(115200-альт), 3(57600), 2,1,0
            byte preferredCode = BaudToCode(_baud);
            byte[] baudCodesAll = new byte[] { preferredCode, 4, 5, 3, 2, 1, 0 };
            var seen = new System.Collections.Generic.HashSet<byte>();
            var baudCodes = new System.Collections.Generic.List<byte>();
            for (int i = 0; i < baudCodesAll.Length; i++) if (seen.Add(baudCodesAll[i])) baudCodes.Add(baudCodesAll[i]);

            // 2) Адреса: твой → 0x00 → 0xFF (убираем дубликаты)
            byte[] addrCandidates = new byte[] { _comAdr, 0x00, 0xFF };
            var seenA = new System.Collections.Generic.HashSet<byte>();
            var addrs = new System.Collections.Generic.List<byte>();
            for (int i = 0; i < addrCandidates.Length; i++) if (seenA.Add(addrCandidates[i])) addrs.Add(addrCandidates[i]);

            int lastRet = -1;

            // === 2.5) Точный выстрел как в демо, если задан конкретный порт ===
            if (desired > 0)
            {
                for (int ai = 0; ai < addrs.Count; ai++)
                {
                    byte adrExact = addrs[ai];
                    byte adrLocal = adrExact;
                    byte baudCode = preferredCode;     // именно preferred
                    int baudReal = CodeToBaud(baudCode);

                    Log("EXACT Open: COM" + desired + " @ " + baudReal + " (code=" + baudCode + "), ADR=0x" + adrExact.ToString("X2"));
                    NudgeLines("COM" + desired, baudReal);

                    int rcExact = OpenComPort(desired, ref adrLocal, baudCode, ref _frmIdx);
                    Log("EXACT -> ret=" + rcExact + ", frmIdx=" + _frmIdx);

                    if (rcExact == 0)
                    {
                        _openedPortNum = desired;
                        _adrInUse = adrLocal; // запоминаем адрес, с которым реально открылись
                        goto OPEN_OK;
                    }
                }
            }

            // 3) Перебор портов/скоростей/адресов
            for (int pi = 0; pi < ports.Count; pi++)
            {
                int portNum = ports[pi];

                for (int bi = 0; bi < baudCodes.Count; bi++)
                {
                    byte baudCode = baudCodes[bi];
                    int baudReal = CodeToBaud(baudCode);

                    for (int ai = 0; ai < addrs.Count; ai++)
                    {
                        byte addr = addrs[ai];
                        byte addrLocal = addr;

                        Log("TRY OpenComPort: COM" + portNum + " @ " + baudReal + " (code=" + baudCode + "), ADR=0x" + addr.ToString("X2"));

                        // «Пинок» линий DTR/RTS
                        NudgeLines("COM" + portNum, baudReal);

                        lastRet = OpenComPort(portNum, ref addrLocal, baudCode, ref _frmIdx);
                        Log("OpenComPort -> ret=" + lastRet + ", frmIdx=" + _frmIdx);

                        if (lastRet == 0)
                        {
                            _openedPortNum = portNum;
                            _adrInUse = addrLocal; // важно!
                            goto OPEN_OK;
                        }

                        Thread.Sleep(120);
                    }
                }
            }

            // 4) Фолбэк: AutoOpen на тех же скоростях и адресах
            for (int bi = 0; bi < baudCodes.Count; bi++)
            {
                byte baudCode = baudCodes[bi];
                int baudReal = CodeToBaud(baudCode);

                for (int ai = 0; ai < addrs.Count; ai++)
                {
                    byte addr = addrs[ai];
                    byte addrLocal = addr;

                    Log("TRY AutoOpenComPort: ? @ " + baudReal + " (code=" + baudCode + "), ADR=0x" + addr.ToString("X2"));

                    int autoPort = 0;
                    lastRet = AutoOpenComPort(ref autoPort, ref addrLocal, baudCode, ref _frmIdx);
                    Log("AutoOpenComPort -> ret=" + lastRet + ", frmIdx=" + _frmIdx + ", autoPort=" + autoPort);

                    if (lastRet == 0)
                    {
                        _openedPortNum = autoPort;
                        _adrInUse = addrLocal; // важно!
                        goto OPEN_OK;
                    }

                    Thread.Sleep(120);
                }
            }

            throw new InvalidOperationException("RRU9816: порт не найден/не открыт (последний ret=" + lastRet +
                                                "). COM занят другим процессом или скорость/адрес не совпадают с настройкой устройства.");

        OPEN_OK:
            {
                // Активный режим
                byte adrForWM = _adrInUse; // используем именно адрес, с которым открылись
                int retWM = SetWorkMode(ref adrForWM, 1 /*active*/, _frmIdx);
                Log("SetWorkMode(active) -> ret=" + retWM);
                if (retWM != 0) throw new InvalidOperationException("RRU9816: SetWorkMode failed (ret=" + retWM + ").");

                byte adrForClr = _adrInUse;
                try { ClearTagBuffer(ref adrForClr, _frmIdx); } catch { }

                _running = true;
                _thread = new Thread(ReadLoop);
                _thread.IsBackground = true;
                _thread.Name = "RRU9816-Poll";
                _thread.Start();
            }
        }

        private void ReadLoop()
        {
            var buf = new byte[4096];
            while (_running)
            {
                try
                {
                    int valid = 0;
                    int ret = ReadActiveModeData(buf, ref valid, _frmIdx);
                    if (ret == 0 && valid > 0)
                        TryExtractEpcs(buf, valid);
                } catch { /* единичные сбои игнорим */ }
                Thread.Sleep(50);
            }
        }

        private void TryExtractEpcs(byte[] data, int length)
        {
            int i = 0;
            while (i < length)
            {
                int epcLen = data[i];
                if (epcLen >= 8 && epcLen <= 64 && i + 1 + epcLen <= length)
                {
                    var epcBytes = new byte[epcLen];
                    Buffer.BlockCopy(data, i + 1, epcBytes, 0, epcLen);
                    SafeRaise(BytesToHex(epcBytes));
                    i += 1 + epcLen;
                }
                else i++;
            }
        }

        private void SafeRaise(string epc)
        {
            try { var h = OnEpcHex; if (h != null) h(epc); } catch { }
        }

        private static string BytesToHex(byte[] arr)
        {
            var sb = new StringBuilder(arr.Length * 2);
            for (int i = 0; i < arr.Length; i++) sb.Append(arr[i].ToString("X2"));
            return sb.ToString();
        }

        // ===== Вспомогалки =====
        private static int ExtractPortNumber(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName)) return 0;
            var s = portName.Trim().ToUpperInvariant();
            if (s.StartsWith("COM")) s = s.Substring(3);
            int n;
            return int.TryParse(s, out n) ? n : 0;
        }

        private static byte BaudToCode(int baud)
        {
            // 0:9600, 1:19200, 2:38400, 3:57600, 4/5:115200
            switch (baud)
            {
                case 9600: return 0;
                case 19200: return 1;
                case 38400: return 2;
                case 57600: return 3;
                default: return 4; // 115200 по умолчанию
            }
        }

        private static int CodeToBaud(byte code)
        {
            switch (code)
            {
                case 0: return 9600;
                case 1: return 19200;
                case 2: return 38400;
                case 3: return 57600;
                case 4:
                case 5:
                default: return 115200;
            }
        }

        private static void NudgeLines(string portName, int baud)
        {
            var sp = new System.IO.Ports.SerialPort(portName, baud,
                System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
            sp.Handshake = System.IO.Ports.Handshake.None;
            sp.DtrEnable = true;
            sp.RtsEnable = true;
            sp.ReadTimeout = 250;
            sp.WriteTimeout = 250;
            try { sp.Open(); Thread.Sleep(120); } catch { }
            finally { try { if (sp.IsOpen) sp.Close(); } catch { } sp.Dispose(); }
        }

        private static string LogsDir()
        {
            try
            {
                // пытаемся использовать общий Logger, если есть
                var prop = typeof(Logger).GetProperty("Dir", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    var v = prop.GetValue(null, null) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            } catch { }
            // запасной путь
            try
            {
                var d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                Directory.CreateDirectory(d);
                return d;
            } catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        private static void Log(string msg)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
                string p = Path.Combine(LogsDir(), LOG_FILE);
                File.AppendAllText(p, line + Environment.NewLine, Encoding.UTF8);
            } catch { }
        }


        public void Dispose()
        {
            _running = false;
            try { if (_thread != null) _thread.Join(300); } catch { }

            try
            {
                if (_openedPortNum > 0) CloseSpecComPort(_openedPortNum);
                else CloseComPort();
            } catch { }

            _frmIdx = -1;
            _openedPortNum = 0;
        }
    }
}
