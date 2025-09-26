using System;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;

// ИРБИС клиент для форматирования brief
using ManagedClient;

// PC/SC
using PCSC;
using PCSC.Exceptions;
using PCSC.Iso7816;

using WinFormsTimer = System.Windows.Forms.Timer;

namespace LibraryTerminal
{
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

        private const int TIMEOUT_SEC_SUCCESS = 20;
        private const int TIMEOUT_SEC_ERROR = 20;
        private const int TIMEOUT_SEC_NO_SPACE = 20;
        private const int TIMEOUT_SEC_NO_TAG = 20;

        private readonly WinFormsTimer _tick = new WinFormsTimer { Interval = 250 };
        private DateTime? _deadline = null;

        private static bool _emu, _dry, _emuUI, _dk;
        private static readonly bool USE_EMULATOR = bool.TryParse(ConfigurationManager.AppSettings["UseEmulator"], out _emu) && _emu;
        private static readonly bool DRY_RUN = bool.TryParse(ConfigurationManager.AppSettings["DryRun"], out _dry) && _dry;
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
        private Rru9816Reader _rruDll; // чтение UHF через вендорскую DLL

        private BookReaderSerial _iqrfid;

        private string _lastBookTag = null;
        private string _lastRruEpc = null;

        // --- GATE для книжных меток (single-shot) ---
        private volatile bool _bookScanBusy = false;     // идёт запрос в ИРБИС
        private DateTime _lastBookAt = DateTime.MinValue;
        private string _lastBookKeyProcessed = null;

        // антидребезг (повтор той же метки через X мс игнорируется)
        private static int BookDebounceMs =>
            int.TryParse(ConfigurationManager.AppSettings["BookDebounceMs"], out var v) ? v : 800;

        // Эмулятор UI
        private Panel _emuPanel;

        private TextBox _emuUid;
        private TextBox _emuRfid;
        private Button _btnEmuCard;
        private Button _btnEmuBookTake;
        private Button _btnEmuBookReturn;
        private CheckBox _chkDryRun;

        // ===== Новые лейблы для показа книги и MFN =====
        private Label lblBookInfoTake;
        private Label lblBookInfoReturn;

        // ★ NEW: MFN последней найденной книги
        private int _lastBookMfn = 0;

        // alias, чтобы не ошибиться именем
        private Screen Screen_ScanTake { get { return Screen.S3_WaitBookTake; } }

        private static Task OffUi(Action a) { return Task.Run(a); }
        private static Task<T> OffUi<T>(Func<T> f) { return Task.Run(f); }

        public MainForm()
        {
            InitializeComponent();
            this.KeyPreview = true;
            this.KeyDown += async (s, e) => { if (e.KeyCode == Keys.F2) { await DebugProbeAllReaders(); e.Handled = true; } };
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
            var _ = await InitIrbisWithRetryAsync(); // тихий старт
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

            if (DEMO_UI) AddBackButtonForSim();

            // в эмуляторе можно поднять только карточные ридеры
            if (USE_EMULATOR)
            {
                InitializeEmulatorPanel();
                if (!FORCE_CARD_READERS_IN_EMU)
                {
                    return;
                }
            }

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
                        }
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("Оборудование (COM): " + ex.Message, "COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- RRU9816 через DLL
                try
                {
                    string rruPort = ConfigurationManager.AppSettings["RruPort"] ?? "COM5";
                    int rruBaud = int.Parse(ConfigurationManager.AppSettings["RruBaudRate"] ?? "57600");

                    _rruDll = null;

                    var rruDll = new Rru9816Reader(rruPort, rruBaud, 0x00);
                    rruDll.OnEpcHex += OnRruEpc;       // бизнес-обработка
                    rruDll.OnEpcHex += OnRruEpcDebug;  // отладка в лог
                    rruDll.Start();

                    var line = $"[RRU-DLL] Started on {(string.IsNullOrWhiteSpace(rruPort) ? "AUTO" : rruPort)} @ {rruBaud} (adr=0x00)";
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

                // --- COM: IQRFID-5102 (карты)
                try
                {
                    string iqPort = PortResolver.Resolve(ConfigurationManager.AppSettings["IqrfidPort"]);
                    int iqBaud = int.Parse(ConfigurationManager.AppSettings["BaudIqrfid"] ?? "9600");
                    string iqNL = ConfigurationManager.AppSettings["NewLineIqrfid"] ?? "\r\n";

                    if (!string.IsNullOrWhiteSpace(iqPort))
                    {
                        _iqrfid = new BookReaderSerial(iqPort, iqBaud, iqNL, readTo, writeTo, reconnMs, debounce);
                        _iqrfid.OnTag += delegate (string uid) { OnAnyCardUid(uid, "IQRFID-5102"); };
                        _iqrfid.Start();
                    }
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

            try { if (_bookReturn != null && _bookReturn != _bookTake) _bookReturn.Dispose(); } catch { }
            try { if (_bookTake != null) _bookTake.Dispose(); } catch { }
            try { if (_ardu != null) _ardu.Dispose(); } catch { }
            try { if (_acr != null) _acr.Dispose(); } catch { }
            try { if (_iqrfid != null) _iqrfid.Dispose(); } catch { }
            try { if (_svc != null) _svc.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (DEMO_KEYS)
            {
                if (keyData == Keys.D1) { OnAnyCardUid("SIM_CARD", "SIM"); return true; }
                if (keyData == Keys.D2) { OnBookTagTake("SIM_BOOK_OK"); return true; }
                if (keyData == Keys.D3) { OnBookTagTake("SIM_BOOK_BAD"); return true; }
                if (keyData == Keys.D4) { OnBookTagReturn("SIM_BOOK_FULL"); return true; }
            }

            if (keyData == Keys.F9) { var _ = TestIrbisConnectionAsync(); return true; }
            if (keyData == Keys.F8) { var _ = ShowBookInfoAsync(); return true; }

            if (USE_EMULATOR && _emuPanel != null && _emuPanel.Visible)
            {
                if (keyData == (Keys.Control | Keys.K)) { if (_btnEmuCard != null) _btnEmuCard.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.T)) { if (_btnEmuBookTake != null) _btnEmuBookTake.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.R)) { if (_btnEmuBookReturn != null) _btnEmuBookReturn.PerformClick(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
        private void Switch(Screen s, Panel panel) { Switch(s, panel, null); }

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

        private void btnTakeBook_Click(object sender, EventArgs e)
        {
            _mode = Mode.Take;
            if (BYPASS_CARD)
            {
                lblReaderInfoTake.Text = "ТЕСТОВЫЙ РЕЖИМ: без карты";
                Switch(Screen.S3_WaitBookTake, panelScanBook);
            }
            else
            {
                Switch(Screen.S2_WaitCardTake, panelWaitCardTake);
            }
            // очищаем инфо
            SetBookInfo(lblBookInfoTake, "");
        }
        private void btnReturnBook_Click(object sender, EventArgs e)
        {
            _mode = Mode.Return;
            if (BYPASS_CARD)
            {
                lblReaderInfoReturn.Text = "ТЕСТОВЫЙ РЕЖИМ: без карты";
                Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
            }
            else
            {
                Switch(Screen.S4_WaitCardReturn, panelWaitCardReturn);
            }
            SetBookInfo(lblBookInfoReturn, "");
        }

        // ---------- обработка UID ----------
        private void OnAnyCardUid(string rawUid, string source)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source); return; }
            var _ = OnAnyCardUidAsync(rawUid, source);
        }

        private async Task OnAnyCardUidAsync(string rawUid, string source)
        {
            Logger.Append("uids.log", "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + source + ": " + rawUid);


            string uid = NormalizeUid(rawUid);

            bool ok = await OffUi<bool>(delegate { return _svc.ValidateCard(uid); });
            if (!ok) { Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR); return; }

            string brief = await SafeGetReaderBriefAsync(_svc.LastReaderMfn);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                var src = (source ?? "").ToUpperInvariant();
                brief = $"[{src}] {brief}";
                lblReaderInfoTake.Text = brief;
                lblReaderInfoReturn.Text = brief;
            }
            else
            {
                lblReaderInfoTake.Text = "Читатель идентифицирован (MFN: " + _svc.LastReaderMfn + ")";
                lblReaderInfoReturn.Text = lblReaderInfoTake.Text;
            }

            if (_screen == Screen.S2_WaitCardTake) Switch(Screen.S3_WaitBookTake, panelScanBook);
            else if (_screen == Screen.S4_WaitCardReturn) Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
        }

        private string NormalizeUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return "";
            bool strip = "true".Equals(ConfigurationManager.AppSettings["UidStripDelimiters"] ?? "true", StringComparison.OrdinalIgnoreCase);
            if (strip) uid = uid.Replace(":", "").Replace(" ", "").Replace("-", "");
            bool upper = "true".Equals(ConfigurationManager.AppSettings["UidUpperHex"] ?? "true", StringComparison.OrdinalIgnoreCase);
            if (upper) uid = uid.ToUpperInvariant();
            return uid;
        }

        // ---------- книги ----------

        // ЕДИНЫЙ ШЛЮЗ: пропускает только одну метку за раз, с антидребезгом
        private void StartBookFlowIfFree(string rawTagOrEpc, bool isReturn)
        {
            var bookKey = ResolveBookKey(rawTagOrEpc);
            if (string.IsNullOrWhiteSpace(bookKey)) return;

            // сразу визуально сообщим, что ищем книгу
            if (!isReturn && (_screen == Screen.S3_WaitBookTake || _screen == Screen_ScanTake))
                SetBookInfo(lblBookInfoTake, "Идёт поиск книги…");
            if (isReturn && _screen == Screen.S5_WaitBookReturn)
                SetBookInfo(lblBookInfoReturn, "Идёт поиск книги…");

            // антидребезг той же метки
            var now = DateTime.UtcNow;
            if (_lastBookKeyProcessed == bookKey && (now - _lastBookAt).TotalMilliseconds < BookDebounceMs)
                return;

            // если уже идёт обработка — игнорируем новые события
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

            // если включён BYPASS_CARD — переводим на нужный экран
            if (BYPASS_CARD && _screen == Screen.S2_WaitCardTake)
                Switch(Screen.S3_WaitBookTake, panelScanBook);
            if (BYPASS_CARD && _screen == Screen.S4_WaitCardReturn)
                Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);

            if (_screen == Screen_ScanTake || _screen == Screen.S3_WaitBookTake)
                StartBookFlowIfFree(epcHex, isReturn: false);
            else if (_screen == Screen.S5_WaitBookReturn)
                StartBookFlowIfFree(epcHex, isReturn: true);
        }

        // отладка: пишем в Debug и в файл rru.log
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
        { return _ardu == null ? Task.FromResult(true) : OffUi<bool>(delegate { _ardu.OpenBin(); return true; }); }
        private Task<bool> HasSpaceAsync()
        { return _ardu == null ? Task.FromResult(true) : OffUi<bool>(delegate { return _ardu.HasSpace(); }); }

        // ====== ВЫДАЧА ======
        private async Task HandleTakeAsync(string bookTag)
        {
            try
            {
                await EnsureIrbisConnectedAsync();

                if (!BYPASS_CARD && (_svc == null || _svc.LastReaderMfn <= 0))
                {
                    lblError.Text = "Сначала приложите карту читателя";
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: no reader (LastReaderMfn=0)");
                    Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                    return;
                }

                // 1) Поиск книги по метке
                var rec = await OffUi<ManagedClient.IrbisRecord>(() => _svc.FindOneByBookRfid(bookTag));
                if (rec == null)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: rec=null for tag={bookTag}");
                    SetBookInfo(lblBookInfoTake, "Книга не найдена по метке.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // >>> Показать MFN + краткое описание
                await ShowBookInfoOnLabel(rec, takeMode: true);

                // ★ NEW: запоминаем MFN для последующих сообщений
                _lastBookMfn = rec.Mfn;

                // 2) Диагностика значений 910^h
                Log910Compare(rec, bookTag);

                // 3) Ищем нужное 910 по совпадению h
                var f910 = rec.Fields.Where(f => f.Tag == "910")
                    .FirstOrDefault(f => BookTagMatches910(bookTag, f.GetFirstSubFieldText('h')));
                if (f910 == null)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: 910^h not matched for tag={bookTag} MFN={rec.Mfn}");
                    SetBookInfo(lblBookInfoTake, "Эта метка не соответствует экземпляру.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 4) Проверяем статус
                string status = f910.GetFirstSubFieldText('a') ?? string.Empty;
                bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
                if (!canIssue)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: already issued (a={status}) MFN={rec.Mfn}");
                    SetBookInfo(lblBookInfoTake, "Эта книга уже выдана.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 5) Dry-run?
                if ((_chkDryRun != null && _chkDryRun.Checked) || DRY_RUN)
                {
                    SetSuccessWithMfn("Dry-run: найдены читатель и книга (без записи в БД)", rec.Mfn);
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                // 6) Запись в RDR 40
                bool ok40 = await OffUi(() =>
                    _svc.AppendRdr40OnIssue(
                        _svc.LastReaderMfn,
                        rec,
                        bookTag,
                        ConfigurationManager.AppSettings["MaskMrg"] ?? "09",
                        _svc.CurrentLogin ?? "terminal",
                        ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS"
                    )
                );
                if (!ok40)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: AppendRdr40 FAILED MFN(reader)={_svc.LastReaderMfn}");
                    SetBookInfo(lblBookInfoTake, "Не удалось записать выдачу.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 7) Обновляем 910^a
                bool okSet = await OffUi(() => _svc.UpdateBook910StatusByRfidStrict(rec, bookTag, STATUS_ISSUED, null));
                if (!okSet)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: Update910 to '1' FAILED MFN={rec.Mfn}");
                    SetBookInfo(lblBookInfoTake, "Не удалось обновить статус экземпляра.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                await OpenBinAsync();
                SetSuccessWithMfn("Книга выдана", rec.Mfn);
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                lblError.Text = "Ошибка выдачи: " + ex.Message;
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: EX={ex.Message}");
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
                    if (USE_EMULATOR) { Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG); return; }

                    Switch(Screen.S7_BookRejected, panelNoTag, null);
                    var hop = new WinFormsTimer { Interval = 2000 };
                    hop.Tick += (s, e2) => { hop.Stop(); hop.Dispose(); Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE); };
                    hop.Start();
                    return;
                }

                // >>> Показать MFN + краткое описание
                await ShowBookInfoOnLabel(rec, takeMode: false);

                // ★ NEW: запоминаем MFN
                _lastBookMfn = rec.Mfn;

                Log910Compare(rec, bookTag);

                bool hasSpace = await HasSpaceAsync();
                if (!hasSpace)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: no space in bin");
                    Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                    return;
                }

                if ((_chkDryRun != null && _chkDryRun.Checked) || DRY_RUN)
                {
                    SetSuccessWithMfn("Dry-run: книга найдена (возврат без записи в БД)", rec.Mfn);
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                // Закрываем 40
                bool ok40 = await OffUi(() =>
                    _svc.CompleteRdr40OnReturn(
                        bookTag,
                        ConfigurationManager.AppSettings["MaskMrg"] ?? "09",
                        _svc.CurrentLogin ?? "terminal"
                    )
                );
                if (!ok40)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: CompleteRdr40 FAILED");
                    SetBookInfo(lblBookInfoReturn, "Не удалось закрыть выдачу у читателя.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 910^a = 0
                bool okSet = await OffUi(() => _svc.UpdateBook910StatusByRfidStrict(rec, bookTag, STATUS_IN_STOCK, null));
                if (!okSet)
                {
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: Update910 to '0' FAILED MFN={rec.Mfn}");
                    SetBookInfo(lblBookInfoReturn, "Не удалось обновить статус экземпляра.");
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                await OpenBinAsync();
                SetSuccessWithMfn("Книга принята", rec.Mfn);
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                lblError.Text = "Ошибка возврата: " + ex.Message;
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: EX={ex.Message}");
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

                // Диагностика и выбор 910
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

        private void btnToMenu_Click(object sender, EventArgs e) { Switch(Screen.S1_Menu, panelMenu); }
        private async void btnCheckBook_Click(object sender, EventArgs e) { await ShowBookInfoAsync(); }
        private async void TestIrbisConnection(object sender, EventArgs e) { await TestIrbisConnectionAsync(); }

        // ======= Эмулятор: панель =======
        private void InitializeEmulatorPanel()
        {
            _emuPanel = new Panel { Height = 72, Dock = DockStyle.Bottom };
            _emuUid = new TextBox { Left = 8, Top = 8, Width = 260 };
            _emuRfid = new TextBox { Left = 8, Top = 38, Width = 260 };

            _btnEmuCard = new Button { Left = 276, Top = 6, Width = 180, Height = 26, Text = "Эмулировать КАРТУ" };
            _btnEmuBookTake = new Button { Left = 276, Top = 36, Width = 180, Height = 26, Text = "Эмулировать ВЫДАЧУ" };
            _btnEmuBookReturn = new Button { Left = 462, Top = 36, Width = 200, Height = 26, Text = "Эмулировать ВОЗВРАТ" };

            _chkDryRun = new CheckBox { Left = 462, Top = 8, Width = 160, Text = "Dry-run (без записи)" };
            _chkDryRun.Checked = DRY_RUN;

            _btnEmuCard.Click += async (_, __) => {
                var uidRaw = _emuUid.Text != null ? _emuUid.Text.Trim() : null;
                if (string.IsNullOrEmpty(uidRaw)) { MessageBox.Show("Введите UID карты"); return; }
                try
                {
                    await EnsureIrbisConnectedAsync();
                    var uid = NormalizeUid(uidRaw);
                    bool ok = await OffUi<bool>(delegate { return _svc.ValidateCard(uid); });
                    if (!ok) { MessageBox.Show(this, "Читатель с UID '" + uid + "' не найден.", "Проверка карты", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    string brief = await SafeGetReaderBriefAsync(_svc.LastReaderMfn);
                    if (string.IsNullOrWhiteSpace(brief)) brief = "Читатель найден. MFN: " + _svc.LastReaderMfn;
                    brief = brief.Replace("\r", "").Replace("\n", " ");
                    MessageBox.Show(this, "OK: " + brief, "Проверка карты", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    lblReaderInfoTake.Text = brief; lblReaderInfoReturn.Text = brief;
                    if (_screen == Screen.S2_WaitCardTake || _screen == Screen.S4_WaitCardReturn) { await OnAnyCardUidAsync(uid, "EMU"); }
                } catch (Exception ex) { MessageBox.Show(this, "Ошибка проверки карты: " + ex.Message, "Проверка карты", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };

            _btnEmuBookTake.Click += async (_, __) => {
                var tagRaw = _emuRfid.Text != null ? _emuRfid.Text.Trim() : null;
                if (string.IsNullOrEmpty(tagRaw)) { MessageBox.Show("Введите RFID книги"); return; }
                await EnsureIrbisConnectedAsync();
                var tag = ResolveBookKey(tagRaw);
                await HandleTakeAsync(tag);
            };
            _btnEmuBookReturn.Click += async (_, __) => {
                var tagRaw = _emuRfid.Text != null ? _emuRfid.Text.Trim() : null;
                if (string.IsNullOrEmpty(tagRaw)) { MessageBox.Show("Введите RFID книги"); return; }
                await EnsureIrbisConnectedAsync();
                var tag = ResolveBookKey(tagRaw);
                await HandleReturnAsync(tag);
            };

            _emuPanel.Controls.AddRange(new Control[] { _emuUid, _emuRfid, _btnEmuCard, _btnEmuBookTake, _btnEmuBookReturn, _chkDryRun });
            this.Controls.Add(_emuPanel);
        }

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
            Logger.Append("pcsc_diag.log",
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg);
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
                                    sb.AppendLine(string.Format("SW={0:X4} (нет карты или команда не поддерживается)", sw));
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

            MessageBox.Show(sb.ToString(), "Диагностика PC/SC (F2)", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task<string> SafeGetReaderBriefAsync(int mfn)
        {
            try
            {
                if (mfn <= 0) return null;
                return await OffUi<string>(delegate {
                    using (var client = new ManagedClient64())
                    {
                        client.ParseConnectionString(GetConnString());
                        client.Connect();
                        client.PushDatabase("RDR");
                        var brief = client.FormatRecord("@brief", mfn);
                        client.PopDatabase();
                        return brief;
                    }
                });
            } catch { return null; }
        }

        private async Task<string> SafeGetBookBriefAsync(int mfn)
        {
            try
            {
                if (mfn <= 0) return null;
                return await OffUi<string>(delegate {
                    using (var client = new ManagedClient64())
                    {
                        client.ParseConnectionString(GetConnString());
                        client.Connect();
                        client.PushDatabase(GetBooksDb());
                        var brief = client.FormatRecord("@brief", mfn);
                        client.PopDatabase();
                        return brief;
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

                Logger.Append("irbis.log",
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                    "MFN=" + rec.Mfn + " key=" + key + " 910^h=[" + string.Join(",", hs) + "]");
            } catch { }
        }

        // ====== ВСПОМОГАТЕЛЬНОЕ ДЛЯ ЛЕЙБЛОВ КНИГИ ======
        private void InitBookInfoLabels()
        {
            // создаём лейбл для экрана выдачи
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

            // лейбл для экрана возврата
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

                if (takeMode) SetBookInfo(lblBookInfoTake, info);
                else SetBookInfo(lblBookInfoReturn, info);

                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(takeMode ? "TAKE" : "RETURN")}: {info}");
            } catch
            {
                if (takeMode) SetBookInfo(lblBookInfoTake, $"[MFN {rec.Mfn}]");
                else SetBookInfo(lblBookInfoReturn, $"[MFN {rec.Mfn}]");
            }
        }

        // ★ NEW: общий хелпер для текста успеха с MFN
        private void SetSuccessWithMfn(string action, int mfn)
        {
            try
            {
                lblSuccess.Text = $"{action}\r\nMFN книги: {mfn}";
            } catch
            {
                lblSuccess.Text = $"{action} (MFN {mfn})";
            }
        }
    }
}
