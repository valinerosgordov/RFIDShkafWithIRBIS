using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing; // для шрифтов заголовка
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO.Ports;

// ИРБИС клиент для форматирования brief
using ManagedClient;

// PC/SC
using PCSC;
using PCSC.Exceptions;
using PCSC.Iso7816;

using WinFormsTimer = System.Windows.Forms.Timer;

// SDK UHFReader09
using ReaderB;

namespace LibraryTerminal
{
    // ======== ВСТРОЕННЫЙ АДАПТЕР ДЛЯ UHFReader09 (IQRFID-5102 / Chafon) ========
    /// <summary>
    /// Опрос ридера по SDK (Inventory_G2) и событие EPC как HEX без разделителей.
    /// Работает и с IQRFID-5102, и с Chafon, т.к. обе говорят через UHFReader09CSharp.dll.
    /// Сигнатура под «старую» DLL (короткая Inventory_G2).
    /// </summary>
    public sealed class UhfReader09Reader : IDisposable
    {
        public event Action<string> OnEpc;
        private byte _addr = 0xFF;
        private int _comIdx = 0;
        private bool _opened;
        private CancellationTokenSource _cts;
        private Task _loop;

        // 0=9600,1=19200,2=38400,3=57600,4=115200
        public bool Start(int baudIndex = 3, int pollMs = 100, int? forcedPort = null)
        {
            byte b = (byte)baudIndex;
            if (b > 2) b += 2;               // особенность API
            int port = forcedPort ?? 255;    // 255 = автоопределение

            int ret = StaticClassReaderB.AutoOpenComPort(ref port, ref _addr, b, ref _comIdx);
            _opened = (ret == 0);
            if (!_opened) return false;

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoop(_cts.Token, pollMs));
            return true;
        }

        private void PollLoop(CancellationToken ct, int periodMs)
        {
            var buf = new byte[8192];

            // СТАРАЯ СИГНАТУРА:
            // int Inventory_G2(ref byte ComAdr, byte AdrTID, byte LenTID, byte TIDFlag,
            //                  byte[] Data, ref int validDatalength, ref int CardNum, int frmcomportindex)
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int total = 0, cnt = 0;
                    int ret = StaticClassReaderB.Inventory_G2(ref _addr, 0, 0, 0, buf, ref total, ref cnt, _comIdx);

                    if (ret == 1 || ret == 2 || ret == 3)
                    {
                        // Буфер: повторяются блоки [len][EPC..]
                        for (int i = 0, seen = 0; i < total && seen < cnt; seen++)
                        {
                            int len = buf[i];
                            if (len <= 0 || i + 1 + len > total) break;

                            OnEpc?.Invoke(BytesToHex(buf, i + 1, len));
                            i += 1 + len;
                        }
                    }
                } catch { /* лог при желании */ }

                if (periodMs > 0) { try { Task.Delay(periodMs, ct).Wait(ct); } catch { } }
            }
        }

        private static string BytesToHex(byte[] data, int off, int len)
        {
            var c = new char[len * 2]; int k = 0;
            for (int i = 0; i < len; i++) { byte b = data[off + i]; c[k++] = N(b >> 4); c[k++] = N(b & 0xF); }
            return new string(c);
        }
        private static char N(int v) => (char)(v < 10 ? ('0' + v) : ('A' + v - 10));

        public void Stop()
        {
            try { _cts?.Cancel(); _loop?.Wait(300); } catch { }
            finally { _cts?.Dispose(); _cts = null; _loop = null; }
            if (_opened) { try { StaticClassReaderB.CloseComPort(); } catch { } _opened = false; }
        }

        public void Dispose() => Stop();
    }

    // ===== Глобальный логгер: пишет в LogsDir из App.config, иначе в .\Logs рядом с exe =====
    internal static class Logger
    {
        private static readonly string _dir = InitDir();

        private static string InitDir()
        {
            var fromCfg = ConfigurationManager.AppSettings["LogsDir"];
            string path;
            if (!string.IsNullOrWhiteSpace(fromCfg))
                path = Environment.ExpandEnvironmentVariables(fromCfg.Trim());
            else
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            try { Directory.CreateDirectory(path); } catch { }
            return path;
        }

        public static void LogArduino(string line)
        {
            try { Append("arduino.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}"); } catch { }
        }

        public static string Dir => _dir;

        public static string PathFor(string fileName) => Path.Combine(_dir, fileName);

        public static void Append(string fileName, string line)
        {
            try { File.AppendAllText(PathFor(fileName), line + Environment.NewLine, Encoding.UTF8); } catch { /* не рушим UI из-за логов */ }
        }
    }

    public partial class MainForm : Form
    {
        private enum Screen
        { S1_Menu, S2_WaitCardTake, S3_WaitBookTake, S4_WaitCardReturn, S5_WaitBookReturn, S6_Success, S7_BookRejected, S8_CardFail, S9_NoSpace }

        private enum Mode
        { None, Take, Return }

        private Screen _screen = Screen.S1_Menu;
        private Mode _mode = Mode.None;

        private const int TIMEOUT_SEC_SUCCESS = 10;
        private const int TIMEOUT_SEC_ERROR = 10;
        private const int TIMEOUT_SEC_NO_SPACE = 10;
        private const int TIMEOUT_SEC_NO_TAG = 10;

        private readonly WinFormsTimer _tick = new WinFormsTimer { Interval = 250 };
        private DateTime? _deadline = null;

        // эмуляторы и демо-режимы (DryRun УДАЛЁН)
        private static bool _emu, _emuUI, _dk;

        private static readonly bool USE_EMULATOR = bool.TryParse(ConfigurationManager.AppSettings["UseEmulator"], out _emu) && _emu;
        private static readonly bool DEMO_UI = bool.TryParse(ConfigurationManager.AppSettings["UseEmulator"], out _emuUI) && _emuUI;
        private static readonly bool DEMO_KEYS = bool.TryParse(ConfigurationManager.AppSettings["DemoKeys"], out _dk) && _dk;

        // флаги для гибкой инициализации железа в демо
        private static bool _forceCards;

        private static readonly bool FORCE_CARD_READERS_IN_EMU =
            bool.TryParse(ConfigurationManager.AppSettings["ForceCardReadersInEmu"], out _forceCards) && _forceCards;

        private static bool _enableBooks, _enableArduino;

        private static readonly bool ENABLE_BOOK_SCANNERS =
            bool.TryParse(ConfigurationManager.AppSettings["EnableBookScanners"], out _enableBooks) && _enableBooks; // по умолчанию false

        private static readonly bool ENABLE_ARDUINO =
            bool.TryParse(ConfigurationManager.AppSettings["EnableArduino"], out _enableArduino) && _enableArduino;   // по умолчанию false

        private const string STATUS_IN_STOCK = "0";
        private const string STATUS_ISSUED = "1";

        // IRBIS и оборудование
        private IrbisServiceManaged _svc;

        private BookReaderSerial _bookTake;
        private BookReaderSerial _bookReturn;
        private ArduinoClientSerial _ardu;

        private Acr1281PcscReader _acr;

        // старый UHF через стороннюю DLL (книги)
        private Rru9816Reader _rruDll;

        // ★ NEW: универсальный UHFReader09 SDK (карты)
        private UhfReader09Reader _uhf09;

        // ридер карт — CardReaderSerial (ASCII по COM, если нужен)
        private CardReaderSerial _iqrfid;

        private string _lastBookTag = null;
        private string _lastRruEpc = null;

        // --- GATE для книжных меток (single-shot) ---
        private volatile bool _bookScanBusy = false;     // идёт запрос в ИРБИС

        private DateTime _lastBookAt = DateTime.MinValue;
        private string _lastBookKeyProcessed = null;

        // антидребезг (повтор той же метки через X мс игнорируется)
        private static int BookDebounceMs =>
            int.TryParse(ConfigurationManager.AppSettings["BookDebounceMs"], out var v) ? v : 800;

        // ===== Лейблы для показа книги и MFN =====
        private Label lblBookInfoTake;
        private Label lblBookInfoReturn;

        // Заголовок с ФИО (на экранах сканирования книги)
        private Label lblReaderHeaderTake;
        private Label lblReaderHeaderReturn;

        // ★ NEW: MFN и brief последней найденной книги
        private int _lastBookMfn = 0;
        private string _lastBookBrief = "";

        // alias
        private Screen Screen_ScanTake { get { return Screen.S3_WaitBookTake; } }

        private static Task OffUi(Action a) => Task.Run(a);
        private static Task<T> OffUi<T>(Func<T> f) => Task.Run(f);

        private int _currentBookMfn;
        private string _currentBookBrief;
        private string _currentBookInv;

        // ======== ARDUИNO: лог и сахар-команды (всегда пишем в лог, даже без железа) ========
        private void LogArduino(string msg)
        {
            try { Logger.Append("arduino.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}"); } catch { }
        }

        private void ArduinoSend(string cmd)
        {
            LogArduino("TX: " + cmd);
            try { _ardu?.Send(cmd); } catch (Exception ex) { LogArduino("SEND_ERR: " + ex.Message); }
        }

        private void ArduinoOk() => ArduinoSend("OK");
        private void ArduinoError() => ArduinoSend("ERR");
        private void ArduinoBeep(int ms = 120) => ArduinoSend($"BEEP:{ms}");

        public MainForm()
        {
            InitializeComponent();
            this.KeyPreview = false;
        }

        private static readonly bool BYPASS_CARD =
            (ConfigurationManager.AppSettings["BypassCardForRruTest"] ?? "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        private static string GetConnString()
        {
            var cfg = ConfigurationManager.AppSettings["ConnectionString"] ?? ConfigurationManager.AppSettings["connection-string"];
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
            return "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";
        }

        private static string GetBooksDb()
        { return ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS"; }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var ok = await InitIrbisWithRetryAsync(); // тихий старт
            try { Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IRBIS startup: {(ok ? "connected OK" : "FAILED")}"); } catch { }
        }

        // ===== IQRFID: автодетект ASCII-порта (оставлено на случай других устройств) =====
        private async Task<(string port, int baud, string nl)> AutoDetectIqrfidAsync(int readTo, int writeTo, int reconnMs, int debounce)
        {
            var ports = SerialPort.GetPortNames().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            var bauds = new[] { 115200, 57600, 38400, 9600 };

            if (ports.Length == 0)
            {
                Logger.Append("iqrfid_auto.log", "Нет доступных COM-портов");
                return (null, 0, null);
            }

            Logger.Append("iqrfid_auto.log", "Старт автодетекта. Порты: " + string.Join(",", ports));

            foreach (var p in ports)
            {
                foreach (var baud in bauds)
                {
                    try
                    {
                        using (var sp = new SerialPort(p, baud))
                        {
                            sp.ReadTimeout = 300; sp.WriteTimeout = 300;
                            sp.Open();

                            var t0 = DateTime.UtcNow;
                            var buf = new byte[4096];
                            int total = 0; bool seenCr = false, seenLf = false;

                            while ((DateTime.UtcNow - t0).TotalMilliseconds < 2200)
                            {
                                try
                                {
                                    int n = sp.Read(buf, 0, buf.Length);
                                    if (n > 0)
                                    {
                                        total += n;
                                        for (int i = 0; i < n; i++)
                                        {
                                            if (buf[i] == 0x0D) seenCr = true;
                                            else if (buf[i] == 0x0A) seenLf = true;
                                        }
                                    }
                                } catch (TimeoutException) { /* ждём */ }
                            }

                            if (total > 0)
                            {
                                string nl = seenCr && seenLf ? "\r\n" : (seenLf ? "\n" : (seenCr ? "\r" : "\r\n"));
                                Logger.Append("iqrfid_auto.log", $"DETECTED: port={p} baud={baud} nl={(nl == "\r\n" ? "\\r\\n" : nl == "\n" ? "\\n" : "\\r")} bytes={total}");
                                return (p, baud, nl);
                            }
                        }
                    } catch (Exception ex)
                    {
                        Logger.Append("iqrfid_auto.log", $"ERR {p}@{baud}: {ex.Message}");
                    }
                }
            }

            Logger.Append("iqrfid_auto.log", "Автодетект не нашёл активного порта");
            return (null, 0, null);
        }

        private async Task InitIqrfidAutoOrFixedAsync(int readTo, int writeTo, int reconnMs, int debounce)
        {
            string iqPort = PortResolver.Resolve(ConfigurationManager.AppSettings["IqrfidPort"]); // "" -> null
            int iqBaud = int.Parse(ConfigurationManager.AppSettings["BaudIqrfid"] ?? "57600");
            string iqNL = ConfigurationManager.AppSettings["NewLineIqrfid"] ?? "\r\n";

            if (string.IsNullOrWhiteSpace(iqPort))
            {
                var found = await AutoDetectIqrfidAsync(readTo, writeTo, reconnMs, debounce);
                if (!string.IsNullOrWhiteSpace(found.port))
                {
                    iqPort = found.port; iqBaud = found.baud; iqNL = found.nl;
                    Logger.Append("iqrfid_auto.log", $"Use autodetected IQRFID: {iqPort} @ {iqBaud}, NL={(iqNL == "\r\n" ? "\\r\\n" : iqNL == "\n" ? "\\n" : "\\r")}");
                }
            }

            if (!string.IsNullOrWhiteSpace(iqPort))
            {
                _iqrfid = new CardReaderSerial(iqPort, iqBaud, iqNL, readTo, writeTo, reconnMs, debounce);

                // лог «сырья», чтобы видеть вход перед парсером UID
                _iqrfid.OnLineReceived += s =>
                    Logger.Append("iqrfid.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RAW: {s}");

                _iqrfid.OnUid += uid => OnAnyCardUid(uid, "IQRFID-5102");
                _iqrfid.Start();
            }
            else
            {
                MessageBox.Show(
                    "IQRFID-5102: порт не найден (автодетект не получил данных). " +
                    "Проверьте, что ридер в режиме COM (не HID) и виден в диспетчере устройств.",
                    "IQRFID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // диагностический пробник — если не открылись
            if ("true".Equals(ConfigurationManager.AppSettings["DiagIqrfidProbe"], StringComparison.OrdinalIgnoreCase)
                && (_iqrfid == null || !_iqrfid.IsOpen))
            {
                _ = ProbeIqrfidAsync(iqPort, iqBaud, iqNL);
            }
        }

        private async Task<bool> InitIrbisWithRetryAsync()
        {
            string conn = GetConnString();
            string db = GetBooksDb();
            if (_svc == null) _svc = new IrbisServiceManaged();

            Exception last = null;
            for (int i = 0; i < 5; i++)
            {
                try { await OffUi(delegate { _svc.Connect(conn); _svc.UseDatabase(db); }); return true; } catch (Exception ex) { last = ex; await Task.Delay(1500); }
            }
            try { Trace.WriteLine("IRBIS startup connect failed: " + (last != null ? last.Message : "")); } catch { }
            return false;
        }

        private async Task TestIrbisConnectionAsync()
        {
            try
            {
                string conn = GetConnString();
                string db = GetBooksDb();

                if (_svc == null) _svc = new IrbisServiceManaged();
                await OffUi(delegate { try { _svc.UseDatabase(db); } catch { _svc.Connect(conn); _svc.UseDatabase(db); } });

                MessageBox.Show("IRBIS: подключение успешно", "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex)
            {
                MessageBox.Show("IRBIS: ошибка подключения\n" + ex.Message, "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task EnsureIrbisConnectedAsync()
        {
            if (_svc == null) _svc = new IrbisServiceManaged();
            string conn = GetConnString();
            string db = GetBooksDb();

            await OffUi(delegate { try { _svc.UseDatabase(db); } catch { _svc.Connect(conn); _svc.UseDatabase(db); } });
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _tick.Tick += Tick_Tick;

            SetUiTexts();
            ShowScreen(panelMenu);

            // создаём инфолейблы программно, чтобы не трогать Designer
            InitBookInfoLabels();
            InitReaderHeaderLabels(); // <— заголовки с ФИО

            if (DEMO_UI) AddBackButtonForSim();

            try
            {
                int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
                int writeTo = int.Parse(ConfigurationManager.AppSettings["WriteTimeoutMs"] ?? "700");
                int reconnMs = int.Parse(ConfigurationManager.AppSettings["AutoReconnectMs"] ?? "1500");
                int debounce = int.Parse(ConfigurationManager.AppSettings["DebounceMs"] ?? "250");

                // --- COM: книжные ридеры + Arduino (опционально)
                try
                {
                    if (ENABLE_BOOK_SCANNERS)
                    {
                        string bookTakePort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookTakePort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                        string bookRetPort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookReturnPort"] ?? ConfigurationManager.AppSettings["BookPort"]);

                        int baudBookTake = int.Parse(ConfigurationManager.AppSettings["BaudBookTake"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                        int baudBookRet = int.Parse(ConfigurationManager.AppSettings["BaudBookReturn"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");

                        string nlBookTake = ConfigurationManager.AppSettings["NewLineBookTake"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                        string nlBookRet = ConfigurationManager.AppSettings["NewLineBookReturn"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";

                        if (!string.IsNullOrWhiteSpace(bookTakePort))
                        {
                            _bookTake = new BookReaderSerial(bookTakePort, baudBookTake, nlBookTake, readTo, writeTo, reconnMs, debounce);
                            _bookTake.OnTag += OnBookTagTake;
                            _bookTake.Start();
                        }

                        if (!string.IsNullOrWhiteSpace(bookRetPort))
                        {
                            if (_bookTake != null && bookRetPort == bookTakePort) _bookReturn = _bookTake;
                            else
                            {
                                _bookReturn = new BookReaderSerial(bookRetPort, baudBookRet, nlBookRet, readTo, writeTo, reconnMs, debounce);
                                _bookReturn.Start();
                            }
                            _bookReturn.OnTag += OnBookTagReturn;
                        }
                    }

                    if (ENABLE_ARDUINO)
                    {
                        string arduinoPort = PortResolver.Resolve(ConfigurationManager.AppSettings["ArduinoPort"]);
                        int baudArduino = int.Parse(ConfigurationManager.AppSettings["BaudArduino"] ?? "115200");
                        string nlArduino = ConfigurationManager.AppSettings["NewLineArduino"] ?? "\n";

                        if (!string.IsNullOrWhiteSpace(arduinoPort))
                        {
                            _ardu = new ArduinoClientSerial(arduinoPort, baudArduino, nlArduino, readTo, writeTo, reconnMs);
                            _ardu.Start();
                            LogArduino($"INIT: enable=True, port={arduinoPort}, baud={baudArduino}, nl={EscapeNL(nlArduino)}");
                        }
                        else
                        {
                            LogArduino("INIT: enable=True, but no port specified — working in NULL mode (only logging)");
                        }
                    }
                    else
                    {
                        LogArduino("INIT: enable=False — working in NULL mode (only logging)");
                    }
                } catch (Exception ex)
                {
                    LogArduino("START_ERR: " + ex.Message);
                    MessageBox.Show("Оборудование (COM): " + ex.Message, "COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- RRU9816 через DLL (книги)
                try
                {
                    string rruPort = ConfigurationManager.AppSettings["RruPort"] ?? "COM5";
                    int rruBa = int.Parse(ConfigurationManager.AppSettings["RruBaudRate"] ?? "57600");

                    _rruDll = null;

                    var rruDll = new Rru9816Reader(rruPort, rruBa, 0x00);
                    rruDll.OnEpcHex += OnRruEpc;       // бизнес-обработка КНИГ
                    rruDll.OnEpcHex += OnRruEpcDebug;  // отладка в лог
                    rruDll.Start();

                    var line = $"[RRU-DLL] Started on {(string.IsNullOrWhiteSpace(rruPort) ? "AUTO" : rruPort)} @ {rruBa} (adr=0x00)";
                    Console.WriteLine(line);
                    Debug.WriteLine(line);
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}");

                    _rruDll = rruDll;
                } catch (BadImageFormatException ex)
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BAD IMAGE: {ex.Message}");
                    MessageBox.Show(
                        "RRU9816: неверная разрядность процесса/DLL.\n" +
                        "Нужно собирать x86 и положить x86 DLL рядом с .exe.",
                        "RRU9816", MessageBoxButtons.OK, MessageBoxIcon.Error);
                } catch (DllNotFoundException ex)
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DLL NOT FOUND: {ex.Message}");
                    MessageBox.Show(
                        "RRU9816: не найдена RRU9816.dll или её зависимости (dmdll.dll/CustomControl.dll).\n" +
                        "Убедитесь, что x86 DLL лежат рядом с .exe (bin\\x86\\...).",
                        "RRU9816", MessageBoxButtons.OK, MessageBoxIcon.Error);
                } catch (Exception ex)
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RRU INIT EX: {ex}");
                    MessageBox.Show("RRU9816 (DLL): " + ex.Message, "RRU9816",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // ★ NEW: UHFReader09 SDK (карты — EPC трактуем как UID)
                try
                {
                    _uhf09 = new UhfReader09Reader();

                    // ВАЖНО: теперь шлём EPC в авторизацию читателя, а НЕ в книжный поток
                    _uhf09.OnEpc += OnUhfCardUid;  // ← новый обработчик
                    _uhf09.OnEpc += OnRruEpcDebug; // лог оставляем общий

                    // на твоих скринах: COM9 @ 57600 → baudIndex=3, период 100 мс
                    if (!_uhf09.Start(baudIndex: 3, pollMs: 100))
                    {
                        Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHFReader09: AutoOpenComPort FAILED");
                    }
                    else
                    {
                        Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHFReader09: started (baudIdx=3, poll=100ms)");
                    }
                } catch (BadImageFormatException ex)
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHF09 BAD IMAGE: {ex.Message}");
                    MessageBox.Show(
                        "UHFReader09: неверная разрядность процесса/DLL.\n" +
                        "Проверь, что проект собран под x86 и обе DLL (UHFReader09CSharp.dll, Basic.dll) лежат рядом с .exe.",
                        "UHFReader09", MessageBoxButtons.OK, MessageBoxIcon.Error);
                } catch (DllNotFoundException ex)
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHF09 DLL NOT FOUND: {ex.Message}");
                    MessageBox.Show(
                        "UHFReader09: не найдены DLL (UHFReader09CSharp.dll / Basic.dll).\n" +
                        "Скопируй их в папку рядом с .exe и установи Platform target = x86.",
                        "UHFReader09", MessageBoxButtons.OK, MessageBoxIcon.Error);
                } catch (Exception ex)
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHF09 INIT EX: {ex}");
                    MessageBox.Show("UHFReader09 (DLL): " + ex.Message, "UHFReader09",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- COM: IQRFID-5102 (ASCII карты) — автодетект/фикс (если нужно)
                try
                {
                    var _ = InitIqrfidAutoOrFixedAsync(readTo, writeTo, reconnMs, debounce);
                } catch (Exception ex)
                {
                    MessageBox.Show("IQRFID-5102: " + ex.Message, "IQRFID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- PC/SC: ACR1281
                try
                {
                    string preferred = ConfigurationManager.AppSettings["AcrReaderName"];
                    if (string.IsNullOrWhiteSpace(preferred))
                        preferred = FindPreferredPiccReaderName() ?? "";

                    if (string.IsNullOrWhiteSpace(preferred))
                        _acr = new Acr1281PcscReader();
                    else
                    {
                        try { _acr = new Acr1281PcscReader(preferred); } catch { _acr = new Acr1281PcscReader(); }
                    }

                    _acr.OnUid += delegate (string uid) { OnAnyCardUid(uid, "ACR1281"); };
                    _acr.Start();
                } catch (Exception ex)
                {
                    MessageBox.Show("PC/SC (ACR1281): " + ex.Message, "PC/SC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            } catch (Exception ex)
            {
                MessageBox.Show("Инициализация ридеров: " + ex.Message, "Init", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { if (_rruDll != null) _rruDll.OnEpcHex -= OnRruEpcDebug; } catch { }
            try { if (_rruDll != null) _rruDll.OnEpcHex -= OnRruEpc; } catch { }
            try { if (_rruDll != null) _rruDll.Dispose(); } catch { }

            // остановим UHFReader09
            try { if (_uhf09 != null) _uhf09.OnEpc -= OnRruEpcDebug; } catch { }
            try { if (_uhf09 != null) _uhf09.OnEpc -= OnUhfCardUid; } catch { }
            try { if (_uhf09 != null) _uhf09.Dispose(); } catch { }

            try { if (_bookReturn != null && _bookReturn != _bookTake) _bookReturn.Dispose(); } catch { }
            try { if (_bookTake != null) _bookTake.Dispose(); } catch { }
            try { if (_ardu != null) _ardu.Dispose(); } catch { }
            try { if (_acr != null) _acr.Dispose(); } catch { }
            try { if (_iqrfid != null) _iqrfid.Dispose(); } catch { }
            try { if (_svc != null) _svc.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        private void Switch(Screen s, Panel panel, int? timeoutSeconds)
        {
            _screen = s;
            ShowScreen(panel);
            if (timeoutSeconds.HasValue)
            {
                _deadline = DateTime.Now.AddSeconds(timeoutSeconds.Value);
                _tick.Enabled = true;
            }
            else { _deadline = null; _tick.Enabled = false; }
        }

        private void Switch(Screen s, Panel panel)
        { Switch(s, panel, null); }

        private void Tick_Tick(object sender, EventArgs e)
        {
            if (_deadline.HasValue && DateTime.Now >= _deadline.Value)
            {
                _deadline = null; _tick.Enabled = false; _mode = Mode.None;
                Switch(Screen.S1_Menu, panelMenu);
            }
        }

        private void ShowScreen(Panel p)
        {
            foreach (Control c in Controls) { var pn = c as Panel; if (pn != null) pn.Visible = false; }
            p.Dock = DockStyle.Fill; p.Visible = true; p.BringToFront();
        }

        // ---------- пункты меню ----------
        // Нижняя плашка для ручного ввода номера билета
      
        private void btnTakeBook_Click(object sender, EventArgs e)
        {
            _mode = Mode.Take;
            _lastBookBrief = "";
            lblReaderInfoTake.Visible = false;
            Switch(Screen.S2_WaitCardTake, panelWaitCardTake);
            SetBookInfo(lblBookInfoTake, "");
        }

        private void btnReturnBook_Click(object sender, EventArgs e)
        {
            _mode = Mode.Return;
            _lastBookBrief = "";
            lblReaderInfoReturn.Visible = false;
            Switch(Screen.S4_WaitCardReturn, panelWaitCardReturn);
            SetBookInfo(lblBookInfoReturn, "");
        }

        private string NormalizeUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return "";
            bool strip = "true".Equals(ConfigurationManager.AppSettings["UidStripDelimiters"] ?? "true",
                                       StringComparison.OrdinalIgnoreCase);
            if (strip) uid = uid.Replace(":", "").Replace(" ", "").Replace("-", "");
            bool upper = "true".Equals(ConfigurationManager.AppSettings["UidUpperHex"] ?? "true",
                                       StringComparison.OrdinalIgnoreCase);
            if (upper) uid = uid.ToUpperInvariant();
            return uid;
        }

        // ---------- обработка UID ----------
        private void OnAnyCardUid(string rawUid, string source)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source); return; }
            var _ = OnAnyCardUidAsync(rawUid, source);
        }

        private async Task OnAnyCardUidAsync(string rawUid, string source)
        {
            Logger.Append("uids.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {rawUid}");

            string uid = NormalizeUid(rawUid);

            // Срабатываем только когда мы на экранах ожидания карты
            if (!(_screen == Screen.S2_WaitCardTake || _screen == Screen.S4_WaitCardReturn))
                return;

            bool ok = await OffUi<bool>(delegate { return _svc.ValidateCard(uid); });
            if (!ok) { ArduinoError(); Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR); return; }

            string brief = await SafeGetReaderBriefAsync(_svc.LastReaderMfn);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                var src = (source ?? "").ToUpperInvariant();
                brief = $"[{src}] {brief}";
                lblReaderInfoTake.Text = brief;
                lblReaderInfoReturn.Text = brief;
                lblReaderInfoTake.Visible = true;
                lblReaderInfoReturn.Visible = true;
            }
            else
            {
                lblReaderInfoTake.Text = "Читатель идентифицирован (MFN: " + _svc.LastReaderMfn + ")";
                lblReaderInfoReturn.Text = lblReaderInfoTake.Text;
            }

            if (_screen == Screen.S2_WaitCardTake)
            {
                Switch(Screen.S3_WaitBookTake, panelScanBook);
                SetReaderHeader(lblReaderInfoTake.Text, isReturn: false);
                lblReaderInfoTake.Visible = true; // ← на всякий
            }
            else if (_screen == Screen.S4_WaitCardReturn)
            {
                Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
                SetReaderHeader(lblReaderInfoReturn.Text, isReturn: true);
                lblReaderInfoReturn.Visible = true; // ← на всякий
            }
        }

        // ---------- UHFReader09 как ридер карты ----------
        private void OnUhfCardUid(string epcHex)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnUhfCardUid), epcHex); return; }

            var uid = EpcToCardUid(epcHex);

            // авторизуем только когда экран ждёт карту
            if (_screen == Screen.S2_WaitCardTake || _screen == Screen.S4_WaitCardReturn)
                OnAnyCardUid(uid, "UHF09");

            // для отладки покажем, что считалось
            try
            {
                lblReaderInfoTake.Text = $"[UHF09] UID: {uid}";
                lblReaderInfoReturn.Text = $"[UHF09] UID: {uid}";
            } catch { }
        }

        // Правило преобразования EPC → UID (управляется через App.config: UhfCardUidLength)
        private static string EpcToCardUid(string epc)
        {
            if (string.IsNullOrWhiteSpace(epc)) return "";
            var hex = new string(epc.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

            // 0 = брать весь EPC; по умолчанию берём первые 24
            int want = int.TryParse(ConfigurationManager.AppSettings["UhfCardUidLength"], out var w) ? w : 24;
            if (want > 0 && hex.Length >= want) return hex.Substring(0, want);
            return hex;
        }

        // ===== КНИЖНЫЕ ПОТОКИ (как были) =====
        private void StartBookFlowIfFree(string rawTagOrEpc, bool isReturn)
        {
            var bookKey = ResolveBookKey(rawTagOrEpc);
            if (string.IsNullOrWhiteSpace(bookKey)) return;

            if (!isReturn && (_screen == Screen.S3_WaitBookTake || _screen == Screen_ScanTake))
                SetBookInfo(lblBookInfoTake, "Идёт поиск книги…");
            if (isReturn && _screen == Screen.S5_WaitBookReturn)
                SetBookInfo(lblBookInfoReturn, "Идёт поиск книги…");

            var now = DateTime.UtcNow;
            if (_lastBookKeyProcessed == bookKey && (now - _lastBookAt).TotalMilliseconds < BookDebounceMs)
                return;

            if (_bookScanBusy) return;

            _bookScanBusy = true;
            _lastBookKeyProcessed = bookKey;
            _lastBookAt = now;
            _lastBookTag = bookKey;

            var _ = (isReturn
                ? HandleReturnAsync(bookKey)
                : HandleTakeAsync(bookKey)
            ).ContinueWith(__ => { _bookScanBusy = false; });
        }

        private void OnBookTagTake(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagTake), tag); return; }
            if (_screen != Screen.S3_WaitBookTake) return;
            StartBookFlowIfFree(tag, isReturn: false);
        }

        private void OnBookTagReturn(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagReturn), tag); return; }
            if (_screen != Screen.S5_WaitBookReturn) return;
            StartBookFlowIfFree(tag, isReturn: true);
        }

        private void OnRruEpc(string epcHex)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnRruEpc), epcHex); return; }

            if (BYPASS_CARD && _screen == Screen.S2_WaitCardTake)
                Switch(Screen.S3_WaitBookTake, panelScanBook);
            if (BYPASS_CARD && _screen == Screen.S4_WaitCardReturn)
                Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);

            if (_screen == Screen_ScanTake || _screen == Screen.S3_WaitBookTake)
                StartBookFlowIfFree(epcHex, isReturn: false);
            else if (_screen == Screen.S5_WaitBookReturn)
                StartBookFlowIfFree(epcHex, isReturn: true);
        }

        private void OnRruEpcDebug(string epc)
        {
            if (IsDisposed) return;
            _lastRruEpc = epc;
            var line = "[RRU EPC] " + epc;
            try
            {
                Console.WriteLine(line);
                Debug.WriteLine(line);
                Logger.Append("rru.log", "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + line);
            } catch { }
        }

        private static string NormalizeHex24(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var hex = new string(s.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
            return (hex.Length >= 24) ? hex.Substring(0, 24) : null;
        }

        private static bool UseEpcBookKey()
        {
            var v = ConfigurationManager.AppSettings["UseEpcBookKey"];
            return !string.IsNullOrEmpty(v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveBookKey(string tagOrEpc)
        {
            var hex24 = NormalizeHex24(tagOrEpc);
            if (hex24 != null)
            {
                if (UseEpcBookKey())
                {
                    var epc = EpcParser.Parse(hex24);
                    if (epc != null && epc.Kind == TagKind.Book)
                        return string.Format("{0:D2}-{1}", epc.LibraryCode, epc.Serial);
                }
                return hex24; // как хранится в 910^h
            }
            return tagOrEpc != null ? tagOrEpc.Trim() : null;
        }

        private Task<bool> OpenBinAsync()
        {
            LogArduino("CMD: OPEN_BIN");
            if (_ardu == null) return Task.FromResult(true);
            return OffUi<bool>(delegate {
                try { _ardu.OpenBin(); return true; } catch (Exception ex) { LogArduino("OPEN_BIN_ERR: " + ex.Message); return false; }
            });
        }

        private Task<bool> HasSpaceAsync()
        {
            LogArduino("CMD: HAS_SPACE?");
            if (_ardu == null)
            {
                LogArduino("HAS_SPACE: assume TRUE (no hardware)");
                return Task.FromResult(true);
            }
            return OffUi<bool>(delegate {
                try { var ok = _ardu.HasSpace(); LogArduino("HAS_SPACE: " + (ok ? "TRUE" : "FALSE")); return ok; } catch (Exception ex) { LogArduino("HAS_SPACE_ERR: " + ex.Message); return false; }
            });
        }

        private static string EscapeNL(string s) => s?.Replace("\r", "\\r").Replace("\n", "\\n");

        // ====== ВЫДАЧА ======
        private async Task HandleTakeAsync(string bookTag)
        {
            try
            {
                await EnsureIrbisConnectedAsync();

                if (!BYPASS_CARD && (_svc == null || _svc.LastReaderMfn <= 0))
                {
                    lblError.Text = "Сначала выполните проверку читателя";
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: no reader (LastReaderMfn=0)");
                    ArduinoError();
                    Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                    return;
                }

                var rec = await OffUi<ManagedClient.IrbisRecord>(() => _svc.FindOneByBookRfid(bookTag));
                if (rec == null)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: rec=null for tag={bookTag}");
                    SetBookInfo(lblBookInfoTake, "Книга не найдена по метке.");
                    ArduinoError();
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                await ShowBookInfoOnLabel(rec, takeMode: true);
                _lastBookMfn = rec.Mfn;

                Log910Compare(rec, bookTag);

                var f910 = rec.Fields.Where(f => f.Tag == "910")
                    .FirstOrDefault(f => BookTagMatches910(bookTag, f.GetFirstSubFieldText('h')));
                if (f910 == null)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: 910^h not matched for tag={bookTag} MFN={rec.Mfn}");
                    SetBookInfo(lblBookInfoTake, "Эта метка не соответствует экземпляру.");
                    ArduinoError();
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                string status = f910.GetFirstSubFieldText('a') ?? string.Empty;
                bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
                if (!canIssue)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: already issued (a={status}) MFN={rec.Mfn}");
                    SetBookInfo(lblBookInfoTake, "Эта книга уже выдана.");
                    ArduinoError();
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                var brief = await OffUi(() => _svc.IssueByRfid(bookTag));
                if (string.IsNullOrWhiteSpace(brief))
                {
                    lblError.Text = "Не удалось записать выдачу в ИРБИС";
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: FAIL (IssueByRfid returned empty) tag={bookTag} mfn={rec.Mfn}");
                    ArduinoError();
                    Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                    return;
                }

                _lastBookBrief = brief.Replace("\r", " ").Replace("\n", " ").Trim();

                await OpenBinAsync();
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: OK tag={bookTag} mfn={rec.Mfn}");
                SetSuccessWithMfn("Книга выдана", rec.Mfn);
                ArduinoOk();
                ArduinoBeep(120);
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                lblError.Text = "Ошибка выдачи: " + ex.Message;
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: EX={ex.Message}");
                ArduinoError();
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // ====== ВОЗВРАТ ======
        private async Task HandleReturnAsync(string bookTag)
        {
            try
            {
                await EnsureIrbisConnectedAsync();

                var rec = await OffUi<ManagedClient.IrbisRecord>(() => _svc.FindOneByBookRfid(bookTag));

                if (rec == null)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: rec=null for tag={bookTag}");
                    SetBookInfo(lblBookInfoReturn, "Книга не найдена по метке.");
                    ArduinoError();
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                await ShowBookInfoOnLabel(rec, takeMode: false);
                _lastBookMfn = rec.Mfn;

                Log910Compare(rec, bookTag);

                bool hasSpace = await HasSpaceAsync();
                if (!hasSpace)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: no space in bin");
                    ArduinoError();
                    Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                    return;
                }

                var brief = await OffUi(() => _svc.ReturnByRfid(bookTag));
                if (string.IsNullOrWhiteSpace(brief))
                {
                    lblError.Text = "Не удалось записать возврат в ИРБИС";
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: FAIL (ReturnByRfid returned empty) tag={bookTag} mfn={rec.Mfn}");
                    ArduinoError();
                    Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                    return;
                }

                _lastBookBrief = brief.Replace("\r", " ").Replace("\n", " ").Trim();

                await OpenBinAsync();
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: OK tag={bookTag} mfn={rec.Mfn}");
                SetSuccessWithMfn("Книга принята", rec.Mfn);
                ArduinoOk();
                ArduinoBeep(120);
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                lblError.Text = "Ошибка возврата: " + ex.Message;
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: EX={ex.Message}");
                ArduinoError();
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // === АВТО (по статусу) ===
        private async Task HandleAutoAsync(string rawTag)
        {
            try
            {
                string tag = ResolveBookKey(rawTag);
                _lastBookTag = tag;

                await EnsureIrbisConnectedAsync();

                var rec = await OffUi<ManagedClient.IrbisRecord>(delegate { return _svc.FindOneByBookRfid(tag); });
                if (rec == null) { await HandleReturnAsync(tag); return; }

                Log910Compare(rec, tag);
                var f910 = rec.Fields
                    .Where(f => f.Tag == "910")
                    .FirstOrDefault(f => BookTagMatches910(tag, f.GetFirstSubFieldText('h')));

                if (f910 == null) { await HandleReturnAsync(tag); return; }

                string status = f910.GetFirstSubFieldText('a') ?? string.Empty;

                if (string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK)
                    await HandleTakeAsync(tag);
                else
                    await HandleReturnAsync(tag);
            } catch (Exception ex)
            {
                MessageBox.Show(this, "Авто-обработка книги: " + ex.Message, "Эмуляция книги", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ===== UI =====
        private void SetUiTexts()
        {
            lblTitleMenu.Text = "Библиотека\nФилиал №1";
            btnTakeBook.Text = "📕 Взять книгу";
            btnReturnBook.Text = "📗 Вернуть книгу";
            lblWaitCardTake.Text = "Приложите карту читателя (Петербуржца или читательский билет)";
            lblWaitCardReturn.Text = "Приложите карту читателя (Петербуржца или читательский билет)";
            lblScanBook.Text = "Поднесите книгу к считывателю";
            lblScanBookReturn.Text = "Поднесите возвращаемую книгу к считывателю";
            lblSuccess.Text = "Операция выполнена";
            lblNoTag.Text = "Метка книги не распознана. Попробуйте ещё раз";
            lblError.Text = "Карта не распознана или ошибка авторизации";
            lblOverflow.Text = "Нет свободного места в шкафу. Обратитесь к сотруднику";
        }

        private void AddBackButtonForSim()
        {
            var back = new Button { Text = "⟵ В меню", Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 120, Height = 36, Left = this.ClientSize.Width - 130, Top = 8 };
            back.Click += (s, e) => { _mode = Mode.None; Switch(Screen.S1_Menu, panelMenu); };
            foreach (Control c in Controls) { var p = c as Panel; if (p != null) p.Controls.Add(back); }
        }

        private void btnToMenu_Click(object sender, EventArgs e)
        { Switch(Screen.S1_Menu, panelMenu); }

        private async void btnCheckBook_Click(object sender, EventArgs e)
        { await ShowBookInfoAsync(); }

        private async void TestIrbisConnection(object sender, EventArgs e)
        { await TestIrbisConnectionAsync(); }

        // ======= PC/SC: утилиты =======
        private string FindPreferredPiccReaderName()
        {
            try
            {
                using (var ctx = ContextFactory.Instance.Establish(SCardScope.System))
                {
                    var readers = ctx.GetReaders();
                    if (readers == null || readers.Length == 0) return null;

                    var picc = readers.FirstOrDefault(r => r.IndexOf("PICC", StringComparison.OrdinalIgnoreCase) >= 0
                                                        || r.IndexOf("Contactless", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!string.IsNullOrWhiteSpace(picc)) return picc;

                    var anyAcr = readers.FirstOrDefault(r => r.IndexOf("ACR1281", StringComparison.OrdinalIgnoreCase) >= 0);
                    return anyAcr ?? readers.First();
                }
            } catch { return null; }
        }

        private static void DiagLog(string msg)
        {
            Logger.Append("pcsc_diag.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
        }

        private async Task DebugProbeAllReaders()
        {
            await Task.Yield();
            var sb = new StringBuilder();
            sb.AppendLine("=== PC/SC DIAG ===");
            DiagLog("=== START ===");

            try
            {
                using (var ctx = ContextFactory.Instance.Establish(SCardScope.System))
                {
                    var readers = ctx.GetReaders();
                    if (readers == null || readers.Length == 0)
                    {
                        MessageBox.Show("PC/SC: ридеры не найдены", "Диагностика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DiagLog("Нет ридеров."); return;
                    }

                    sb.AppendLine("Найдены ридеры:");
                    for (int i = 0; i < readers.Length; i++)
                    {
                        sb.AppendLine("  " + i + ": " + readers[i]);
                        DiagLog("Reader[" + i + "]: " + readers[i]);
                    }

                    sb.AppendLine();
                    sb.AppendLine("Пробую получить UID (APDU FF CA 00 00 00)...");
                    var apdu = new CommandApdu(IsoCase.Case2Short, SCardProtocol.Any) { CLA = 0xFF, INS = 0xCA, P1 = 0x00, P2 = 0x00, Le = 0x00 };

                    foreach (var reader in readers)
                    {
                        sb.AppendLine("--- " + reader + " ---");
                        DiagLog("Connect: " + reader);
                        try
                        {
                            using (var isoReader = new IsoReader(ctx, reader, SCardShareMode.Shared, SCardProtocol.Any, false))
                            {
                                var response = isoReader.Transmit(apdu);
                                var sw = (response.SW1 << 8) | response.SW2;
                                if (sw == 0x9000)
                                {
                                    var uid = BitConverter.ToString(response.GetData()).Replace("-", "");
                                    sb.AppendLine("UID: " + uid + " (OK)");
                                    DiagLog("UID OK: " + reader + " UID=" + uid);
                                }
                                else
                                {
                                    sb.AppendLine(string.Format("SW={0:X4} (нет карты или команда не поддерживается)"));
                                    DiagLog(string.Format("SW={0:X4} {1}", sw, reader));
                                }
                            }
                        } catch (PCSCException ex)
                        {
                            sb.AppendLine("PCSC: " + ex.SCardError + " (" + ex.Message + ")");
                            DiagLog("PCSC EX: " + reader + " -> " + ex.SCardError + " " + ex.Message);
                        } catch (Exception ex)
                        {
                            sb.AppendLine("ERR: " + ex.Message);
                            DiagLog("GEN EX: " + reader + " -> " + ex.Message);
                        }
                    }
                    sb.AppendLine();
                    sb.AppendLine("Подсказка: используем ридер с 'PICC' или 'Contactless'.");
                }
            } catch (Exception ex)
            {
                sb.AppendLine("FATAL: " + ex.Message);
                DiagLog("FATAL: " + ex);
            }
            finally { DiagLog("=== END ==="); }

            MessageBox.Show(sb.ToString(), "Диагностика PC/SC", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task<string> SafeGetReaderBriefAsync(int mfn)
        {
            try
            {
                if (mfn <= 0) return null;

                var rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
                var briefFmt = ConfigurationManager.AppSettings["BriefFormat"] ?? "@brief";

                return await OffUi<string>(delegate {
                    using (var client = new ManagedClient64())
                    {
                        client.ParseConnectionString(GetConnString());
                        client.Connect();
                        client.PushDatabase(rdrDb);
                        var brief = client.FormatRecord(briefFmt, mfn);
                        client.PopDatabase();
                        return string.IsNullOrWhiteSpace(brief) ? null : brief.Trim();
                    }
                });
            } catch { return null; }
        }

        private async Task<string> SafeGetBookBriefAsync(int mfn)
        {
            try
            {
                if (mfn <= 0) return null;

                var booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
                var briefFmt = ConfigurationManager.AppSettings["BookBriefFormat"] ?? "@brief";

                return await OffUi<string>(() => {
                    using (var client = new ManagedClient64())
                    {
                        client.ParseConnectionString(GetConnString());
                        client.Connect();
                        client.PushDatabase(booksDb);
                        var brief = client.FormatRecord(briefFmt, mfn);
                        client.PopDatabase();
                        return string.IsNullOrWhiteSpace(brief) ? null : brief.Trim();
                    }
                });
            } catch { return null; }
        }

        private async Task ShowBookInfoAsync(string bookTag = null)
        {
            try
            {
                await EnsureIrbisConnectedAsync();

                string tag = bookTag;
                if (string.IsNullOrWhiteSpace(tag)) tag = _lastBookTag;
                if (string.IsNullOrWhiteSpace(tag))
                {
                    tag = Ask("Проверка книги", "Введите RFID-метку книги (HEX/код):", "");
                    if (string.IsNullOrWhiteSpace(tag)) return;
                }

                tag = ResolveBookKey(tag);

                var rec = await OffUi<ManagedClient.IrbisRecord>(delegate { return _svc.FindOneByBookRfid(tag); });
                if (rec == null)
                {
                    MessageBox.Show(this, "Книга не найдена по метке: " + tag, "Проверка книги", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string brief = await SafeGetBookBriefAsync(rec.Mfn);
                if (string.IsNullOrWhiteSpace(brief))
                {
                    var title = rec.FM("200", 'a') ?? "(без заглавия)";
                    var shifr = rec.FM("903");
                    var invs = string.Join(", ", rec.FMA("910", 'b') ?? new string[0]);
                    brief = title + "\nШифр: " + shifr + "\nИнвентарные №: " + invs;
                }

                MessageBox.Show(this, ("[MFN " + rec.Mfn + "] " + brief.Trim()), "Книга", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex)
            {
                MessageBox.Show(this, "Ошибка проверки книги: " + ex.Message, "Проверка книги", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --- маленький InputBox ---
        private static string Ask(string title, string prompt, string def)
        {
            using (var f = new Form())
            using (var txt = new TextBox())
            using (var lbl = new Label())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent; f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new System.Drawing.Size(420, 120);

                lbl.Text = prompt; lbl.SetBounds(12, 12, 396, 20);
                txt.Text = def; txt.SetBounds(12, 36, 396, 23);
                ok.Text = "OK"; ok.DialogResult = DialogResult.OK; ok.SetBounds(232, 72, 80, 26);
                cancel.Text = "Отмена"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(328, 72, 80, 26);

                f.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;

                return f.ShowDialog() == DialogResult.OK ? txt.Text : null;
            }
        }

        private static string IrbisServiceManaged_Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace(" ", "").Replace("-", "").Replace(":", "");
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return s.ToUpperInvariant();
        }

        private static bool BookTagMatches910(string scanned, string hFromRecord)
        {
            var key = IrbisServiceManaged_Normalize(scanned);
            var nh = IrbisServiceManaged_Normalize(hFromRecord);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(nh)) return false;
            if (nh == key) return true;
            if (key.EndsWith(nh)) return true;
            if (nh.EndsWith(key)) return true;
            return false;
        }

        private static void Log910Compare(ManagedClient.IrbisRecord rec, string scanned)
        {
            try
            {
                var key = IrbisServiceManaged_Normalize(scanned);
                var hs = rec.Fields.Where(f => f.Tag == "910")
                    .Select(f => f.GetFirstSubFieldText('h'))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(IrbisServiceManaged_Normalize)
                    .ToArray();

                Logger.Append(
                    "irbis.log",
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                    "MFN=" + rec.Mfn + " key=" + key + " 910^h=[" + string.Join(",", hs) + "]"
                );
            } catch { }
        }

        // ====== ВСПОМОГОАТЕЛЬНОЕ ДЛЯ ЛЕЙБЛОВ КНИГИ ======
        private void InitBookInfoLabels()
        {
            lblBookInfoTake = new Label
            {
                AutoSize = false,
                Width = panelScanBook.Width - 40,
                Height = 48,
                Left = 20,
                Top = panelScanBook.Height - 60,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            panelScanBook.Controls.Add(lblBookInfoTake);

            lblBookInfoReturn = new Label
            {
                AutoSize = false,
                Width = panelScanBookReturn.Width - 40,
                Height = 48,
                Left = 20,
                Top = panelScanBookReturn.Height - 60,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            panelScanBookReturn.Controls.Add(lblBookInfoReturn);

            SetBookInfo(lblBookInfoTake, "");
            SetBookInfo(lblBookInfoReturn, "");
        }

        // ====== ШАПКА С ФИО НА ЭКРАНАХ СКАНИРОВАНИЯ ======
        private void InitReaderHeaderLabels()
        {
            lblReaderHeaderTake = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 48,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold)
            };
            panelScanBook.Controls.Add(lblReaderHeaderTake);
            panelScanBook.Controls.SetChildIndex(lblReaderHeaderTake, 0);
            lblReaderHeaderTake.Text = "";

            lblReaderHeaderReturn = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 48,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold)
            };
            panelScanBookReturn.Controls.Add(lblReaderHeaderReturn);
            panelScanBookReturn.Controls.SetChildIndex(lblReaderHeaderReturn, 0);
            lblReaderHeaderReturn.Text = "";
        }

        private void SetReaderHeader(string text, bool isReturn)
        {
            try
            {
                var lbl = isReturn ? lblReaderHeaderReturn : lblReaderHeaderTake;
                if (lbl != null) lbl.Text = text ?? "";
            } catch { }
        }

        private void ClearReaderHeaders()
        {
            try { if (lblReaderHeaderTake != null) lblReaderHeaderTake.Text = ""; } catch { }
            try { if (lblReaderHeaderReturn != null) lblReaderHeaderReturn.Text = ""; } catch { }
        }

        private void SetBookInfo(Label lbl, string text)
        {
            try { if (lbl != null) lbl.Text = text ?? ""; } catch { }
        }

        private async Task ShowBookInfoOnLabel(ManagedClient.IrbisRecord rec, bool takeMode)
        {
            try
            {
                string brief = await SafeGetBookBriefAsync(rec.Mfn);
                if (string.IsNullOrWhiteSpace(brief))
                {
                    var title = rec.FM("200", 'a') ?? "(без заглавия)";
                    var shifr = rec.FM("903");
                    var invs = string.Join(", ", rec.FMA("910", 'b') ?? new string[0]);
                    brief = title + "\nШифр: " + shifr + "\nИнвентарные №: " + invs;
                }
                var oneLine = brief.Replace("\r", " ").Replace("\n", " ").Trim();
                var info = $"[MFN {rec.Mfn}] {oneLine}";

                _lastBookBrief = oneLine;

                if (takeMode) SetBookInfo(lblBookInfoTake, info);
                else SetBookInfo(lblBookInfoReturn, info);

                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(takeMode ? "TAKE" : "RETURN")}: {info}");
            } catch
            {
                _lastBookBrief = "";
                if (takeMode) SetBookInfo(lblBookInfoTake, $"[MFN {rec.Mfn}]");
                else SetBookInfo(lblBookInfoReturn, $"[MFN {rec.Mfn}]");
            }
        }


        private void SetSuccessWithMfn(string action, int mfn)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_lastBookBrief))
                    lblSuccess.Text = $"{action}\r\nMFN книги: {mfn}\r\n{_lastBookBrief}";
                else
                    lblSuccess.Text = $"{action}\r\nMFN книги: {mfn}";
            } catch
            {
                lblSuccess.Text = $"{action} (MFN {mfn})";
            }
        }


        private void ShowStatus(string text)
        {
            try { this.Text = string.IsNullOrWhiteSpace(text) ? "Терминал библиотеки" : $"Терминал — {text}"; } catch { }
        }

        private async Task ProbeIqrfidAsync(string portName, int baud, string nl, int ms = 3000)
        {
            try
            {
                using (var sp = new System.IO.Ports.SerialPort(portName, baud))
                {
                    if (!string.IsNullOrEmpty(nl)) sp.NewLine = nl;
                    sp.ReadTimeout = 500;
                    sp.Open();
                    var t0 = DateTime.UtcNow;
                    var buf = new byte[4096];

                    while ((DateTime.UtcNow - t0).TotalMilliseconds < ms)
                    {
                        try
                        {
                            int n = sp.Read(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                var hex = BitConverter.ToString(buf, 0, n);
                                Logger.Append("iqrfid_probe.log", $"[{DateTime.Now:HH:mm:ss.fff}] BYTES {n}: {hex}");
                            }
                        } catch (TimeoutException) { /* ок, ждём дальше */ }
                    }
                }
                Logger.Append("iqrfid_probe.log", "PROBE DONE");
            } catch (Exception ex)
            {
                Logger.Append("iqrfid_probe.log", "PROBE ERR: " + ex.Message);
            }
        }
    }
}
