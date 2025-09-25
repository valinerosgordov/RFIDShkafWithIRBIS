using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LibraryTerminal
{
    /// <summary>
    /// RRU9816 via RRU9816.dll — опрос EPC через Inventory_G2 (command mode).
    /// По умолчанию: COM5 @ 57600, addr=0x00.
    /// </summary>
    public sealed class Rru9816Reader : IDisposable
    {
        public event Action<string> OnEpcHex;

        private volatile bool _running;
        private Thread _thread;

        private int _frmIdx = -1;
        private int _openedPortNum = 5;    // COM5
        private byte _adrInUse = 0x00; // 0x00/0xFF
        private byte _baudCode = 3;    // 0:9600 1:19200 2:38400 3:57600 4/5:115200

        private const string DLL = "RRU9816.dll";
        private const string LOG_FILE = "rru_attempts.log";

        // ===== P/Invoke (ровно как в демке) =====
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "OpenComPort")]
        private static extern int OpenComPort(int Port, ref byte ComAddr, byte Baud, ref int PortHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "CloseComPort")]
        private static extern int CloseComPort();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "Inventory_G2")]
        private static extern int Inventory_G2(
            ref byte ComAdr,
            byte QValue, byte Session,
            byte MaskMem, byte[] MaskAdr, byte MaskLen, byte[] MaskData, byte MaskFlag,
            byte AdrTID, byte LenTID, byte TIDFlag,
            byte Target, byte InAnt, byte Scantime, byte FastFlag,
            byte[] pEPCList, ref byte Ant, ref int Totallen, ref int CardNum,
            int frmComPortindex);
        // =======================================

        public Rru9816Reader() { }
        public Rru9816Reader(string comPort, int baud, byte comAdr)
        {
            int p = ExtractPortNumber(comPort);
            if (p > 0) _openedPortNum = p;
            _baudCode = BaudToCode(baud);
            _adrInUse = (comAdr == 0x00 || comAdr == 0xFF) ? comAdr : (byte)0x00;
        }

        public void Start()
        {
            if (_running) return;

            _frmIdx = -1;
            if (_adrInUse != 0x00 && _adrInUse != 0xFF) _adrInUse = 0x00;

            string portName = "COM" + _openedPortNum;
            int baudReal = CodeToBaud(_baudCode);

            Log($"OpenComPort: {portName} @ {baudReal}, ADR=0x{_adrInUse:X2}");
            NudgeLines(portName, baudReal);

            int rc = OpenComPort(_openedPortNum, ref _adrInUse, _baudCode, ref _frmIdx);
            Log($"OpenComPort -> ret={rc}, frmIdx={_frmIdx}, ADR(now)=0x{_adrInUse:X2}");
            if (rc != 0) throw new InvalidOperationException($"RRU9816: OpenComPort failed (ret={rc})");

            _running = true;
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "RRU9816-Poll" };
            _thread.Start();

            Log($"RRU ready (command-mode): {portName} @ {baudReal}, adr=0x{_adrInUse:X2}");
        }

        private void ReadLoop()
        {
            // Буферы и параметры — как в Form1.cs демо
            byte[] EPC = new byte[50000];
            byte Ant = 0;
            int CardNum = 0;
            int Totallen = 0;

            // Выбор EPC (без маски и без TID)
            byte QValue = 4;     // как на скрине демо
            byte Session = 0;
            byte MaskMem = 0;
            byte[] MaskAdr = new byte[2]; // не используется
            byte MaskLen = 0;
            byte[] MaskData = new byte[100];
            byte MaskFlag = 0;

            byte AdrTID = 0;
            byte LenTID = 6;       // демка ставит 6, но TIDFlag=0 => игнорируется
            byte TIDFlag = 0;

            byte Target = 0;     // A
            byte Scantime = 10;    // 10 * 100ms = ~1s, как в демке
            byte FastFlag = 0;

            byte[] antMasks = { 0x01, 0x0F, 0x80 }; // ANT1 → all → auto
            int mi = 0, silent = 0;

            while (_running)
            {
                try
                {
                    // сбрасываем выходные
                    Ant = 0; CardNum = 0; Totallen = 0;
                    byte inAnt = antMasks[mi];

                    int ret = Inventory_G2(
                        ref _adrInUse,
                        QValue, Session,
                        MaskMem, MaskAdr, MaskLen, MaskData, MaskFlag,
                        AdrTID, LenTID, TIDFlag,
                        Target, inAnt, Scantime, FastFlag,
                        EPC, ref Ant, ref Totallen, ref CardNum,
                        _frmIdx);

                    if (Totallen > 0 && CardNum > 0)
                    {
                        ParseAndRaise(EPC, Totallen, CardNum);
                        silent = 0;
                    }
                    else
                    {
                        if (++silent >= 25) // ~2 сек для диагностики
                        {
                            Log($"Inventory_G2: ret={ret}, Totallen={Totallen}, CardNum={CardNum}, InAnt=0x{inAnt:X2}");
                            silent = 0;
                            mi = (mi + 1) % antMasks.Length;
                        }
                    }
                } catch (Exception ex) { Log("ReadLoop EX: " + ex.Message); }

                Thread.Sleep(60);
            }
        }

        // Парсинг ровно как в демке: daw[m] = длина EPC; далее EPC[Len] + 1 байт RSSI
        private void ParseAndRaise(byte[] epcRaw, int totalLen, int cardNum)
        {
            try
            {
                int m = 0;
                for (int idx = 0; idx < cardNum && m < totalLen; idx++)
                {
                    int epcLenPlus1 = epcRaw[m] + 1;          // Len + 1 (как в демке)
                    int start = m + 1;                         // смещаемся за байт длины
                    int epcLen = epcLenPlus1 - 1;              // последний байт — RSSI
                    if (start + epcLen <= totalLen)
                    {
                        var epcBytes = new byte[epcLen];
                        Buffer.BlockCopy(epcRaw, start, epcBytes, 0, epcLen);
                        OnEpcHex?.Invoke(BytesToHex(epcBytes));
                    }
                    m += epcLenPlus1 + 1; // +1 за байт длины
                }
            } catch { /* игнор разовых ошибок парсинга */ }
        }

        // ==== utils ====
        private static int ExtractPortNumber(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName)) return 0;
            var s = portName.Trim().ToUpperInvariant();
            if (s.StartsWith("COM")) s = s.Substring(3);
            int n; return int.TryParse(s, out n) ? n : 0;
        }
        private static byte BaudToCode(int baud)
        {
            switch (baud)
            {
                case 9600: return 0;
                case 19200: return 1;
                case 38400: return 2;
                case 57600: return 3;
                default: return 3;
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
                default: return 57600;
            }
        }
        private static void NudgeLines(string portName, int baud)
        {
            var sp = new System.IO.Ports.SerialPort(portName, baud,
                System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One)
            {
                Handshake = System.IO.Ports.Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 250,
                WriteTimeout = 250
            };
            try { sp.Open(); Thread.Sleep(120); } catch { }
            finally { try { if (sp.IsOpen) sp.Close(); } catch { } sp.Dispose(); }
        }
        private static string LogsDir()
        {
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
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
                string p = Path.Combine(LogsDir(), LOG_FILE);
                File.AppendAllText(p, line + Environment.NewLine, Encoding.UTF8);
            } catch { }
        }
        private static string BytesToHex(byte[] arr)
        {
            var sb = new StringBuilder(arr.Length * 2);
            for (int i = 0; i < arr.Length; i++) sb.Append(arr[i].ToString("X2"));
            return sb.ToString();
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(300); } catch { }
            try { CloseComPort(); } catch { }
            _frmIdx = -1;
        }
    }
}
