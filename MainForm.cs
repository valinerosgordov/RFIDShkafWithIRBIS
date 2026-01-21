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
    // ======== ВСТРОЕННЫЙ АДАПТЕР ДЛЯ UHFReader09 (IQRFID-5102 / Chafон) ========
    /// <summary>
    /// Опрос ридера по SDK (Inventory_G2) и событие EPC как HEX без разделителей.
    /// Работает и с IQRFID-5102, и с Chafon, т.к. обе говорят через UHFReader09CSharp.dll.
    /// Сигнатура под «старую» DLL (короткая Inventory_G2).
    /// </summary>
    public sealed class UhfReader09Reader : ICardReader, IDisposable
    {
        public event EventHandler<string> CardRead;
        
        [Obsolete("Use CardRead event instead")]
        public event Action<string> OnEpc
        {
            add { CardRead += (s, epc) => value(epc); }
            remove { /* Not supported */ }
        }
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
            _loop = Task.Run(() => PollLoopAsync(_cts.Token, pollMs), _cts.Token);
            return true;
        }

        private async Task PollLoopAsync(CancellationToken ct, int periodMs)
        {
            var buf = new byte[8192];

            // старая сигнатура Inventory_G2
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

                            var handler = CardRead;
                            handler?.Invoke(this, BytesToHex(buf, i + 1, len));
                            i += 1 + len;
                        }
                    }
                } catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    try { Logger.Append("rru.log", $"PollLoop error: {ex.Message}"); } catch { }
                }

                if (periodMs > 0)
                {
                    try { await Task.Delay(periodMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
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

    // ===== Глобальный логгер =====
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

        public static void Append(string fileName, string line)
        {
            try { File.AppendAllText(Path.Combine(_dir, fileName), line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }

    public partial class MainForm : Form
    {
        // Используем новый enum из ScreenManager
        private LibraryTerminal.Screen _currentScreenType;
        private Mode _operationMode = Mode.None;

        private const int TIMEOUT_SECONDS_SUCCESS = 10;
        private const int TIMEOUT_SECONDS_ERROR = 10;
        private const int TIMEOUT_SECONDS_NO_SPACE = 10;
        private const int TIMEOUT_SECONDS_NO_TAG = 10;

        // Константы статусов книг (используются в BookOperationService)
        private const string STATUS_IN_STOCK = "0";
        private const string STATUS_ISSUED = "1";

        // Сервисы и менеджеры
        private readonly IIrbisService _irbisService;
        private IArduinoController _arduinoController;
        private BookOperationService _bookOperationService;
        private readonly ScreenManager _screenManager;
        private readonly ReaderManager _readerManager;
        private readonly AppConfiguration _configuration;

        // Ридеры (для обратной совместимости и прямого доступа при необходимости)
        private BookReaderSerial _bookTakeReader;
        private BookReaderSerial _bookReturnReader;
        private ArduinoClientSerial _arduinoClient;
        private Acr1281PcscReader _acr1281CardReader;
        private Rru9816Reader _rru9816BookReader;
        private UhfReader09Reader _uhfReader09CardReader;
        private CardReaderSerial _iqrfidCardReader;

        private string _lastBookTag = null;

        private int _bookScanBusy = 0; // 0 = свободно, 1 = занято (используем Interlocked)
        private DateTime _lastBookAt = DateTime.MinValue;
        private string _lastBookKeyProcessed = null;

        private static int BookDebounceMs =>
            int.TryParse(ConfigurationManager.AppSettings["BookDebounceMs"], out var v) ? v : 800;

        // UI элементы - исправлено именование (убрана венгерская нотация)
        private Label _bookInfoTakeLabel;
        private Label _bookInfoReturnLabel;
        private Label _readerHeaderTakeLabel;
        private Label _readerHeaderReturnLabel;

        // Кэш последней книги
        private int _lastBookMfn = 0;
        private string _lastBookBrief = "";

        private static Task RunOnBackgroundThreadAsync(Action action) => Task.Run(action);
        private static Task<T> RunOnBackgroundThreadAsync<T>(Func<T> function) => Task.Run(function);

        // ======== ARDUINO: команды (обёртки для обратной совместимости) ========
        private void LogArduino(string message)
        {
            try { Logger.Append("arduino.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}"); } catch { }
        }

        private void ArduinoSend(string command)
        {
            LogArduino("TX: " + command);
            try { _arduinoController?.SendCommand(command); } catch (Exception ex) { LogArduino("SEND_ERR: " + ex.Message); }
        }

        private void ArduinoOk() => _arduinoController?.SendOk();
        private void ArduinoError() => _arduinoController?.SendError();
        private void ArduinoBeep(int milliseconds = 120) => _arduinoController?.SendBeep(milliseconds);

        public MainForm()
        {
            InitializeComponent();
            this.KeyPreview = false;

            // Инициализация конфигурации
            _configuration = AppConfiguration.Load();

            // Инициализация сервисов
            _irbisService = new IrbisServiceManaged();
            _arduinoController = null; // Будет инициализирован в MainForm_Load
            _bookOperationService = null; // Будет инициализирован после Arduino
            _screenManager = new ScreenManager(this, TIMEOUT_SECONDS_SUCCESS);
            _readerManager = new ReaderManager();

            // Подписка на события ScreenManager
            _screenManager.ScreenChanged += OnScreenChanged;
            _screenManager.TimeoutReached += OnScreenTimeout;
        }

        // центрирование кнопок главного меню
        private void CenterMainButtons()
        {
            if (panelMenu == null || btnTakeBook == null || btnReturnBook == null) return;

            int buttonWidth = Math.Max(btnTakeBook.Width, btnReturnBook.Width);
            btnTakeBook.Width = btnReturnBook.Width = buttonWidth;

            const int SPACING = 16;
            int leftPosition = Math.Max(0, (panelMenu.ClientSize.Width - buttonWidth) / 2);
            int headerOffset = (lblTitleMenu != null ? lblTitleMenu.Bottom + 20 : 100);
            int totalHeight = btnTakeBook.Height + SPACING + btnReturnBook.Height;
            int topStart = Math.Max(headerOffset, (panelMenu.ClientSize.Height - totalHeight) / 2);

            btnTakeBook.Location = new Point(leftPosition, topStart);
            btnReturnBook.Location = new Point(leftPosition, btnTakeBook.Bottom + SPACING);
        }

        // BYPASS_CARD удалён - теперь используется локально в OnRruEpcInternal

        // Удалены методы GetConnString и GetBooksDb - теперь используется AppConfiguration

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            
            // Fire and forget с обработкой ошибок
            _ = InitializeIrbisWithRetryAsync().ContinueWith(t =>
            {
                try
                {
                    bool success = t.IsCompletedSuccessfully && t.Result;
                    Logger.Append("irbis.log",
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IRBIS startup: {(success ? "connected OK" : "FAILED")}");

                    if (t.IsFaulted)
                    {
                        Logger.Append("irbis.log",
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IRBIS startup exception: {t.Exception?.GetBaseException()?.Message}");
                    }
                } catch { }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ===== IQRFID автодетект (если нужен) =====
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
                                } catch (TimeoutException) { }
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
            string iqPort = PortResolver.Resolve(ConfigurationManager.AppSettings["IqrfidPort"]);
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
                _iqrfidCardReader = new CardReaderSerial(iqPort, iqBaud, iqNL, readTo, writeTo, reconnMs, debounce);

                _iqrfidCardReader.OnLineReceived += s =>
                    Logger.Append("iqrfid.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RAW: {s}");

                _iqrfidCardReader.CardRead += (s, uid) => OnAnyCardUid(uid, "IQRFID-5102");
                _readerManager.AddCardReader(_iqrfidCardReader);
            }
            else
            {
                MessageBox.Show(
                    "IQRFID-5102: порт не найден (автодетект не получил данных). " +
                    "Проверьте, что ридер в режиме COM (не HID) и виден в диспетчере устройств.",
                    "IQRFID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if ("true".Equals(ConfigurationManager.AppSettings["DiagIqrfidProbe"], StringComparison.OrdinalIgnoreCase)
                && (_iqrfidCardReader == null || !_iqrfidCardReader.IsOpen))
            {
                _ = ProbeIqrfidAsync(iqPort, iqBaud, iqNL);
            }
        }

        private async Task<bool> InitializeIrbisWithRetryAsync()
        {
            const int MAX_RETRIES = 5;
            const int RETRY_DELAY_MS = 1500;

            Exception lastException = null;
            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    await RunOnBackgroundThreadAsync(() =>
                    {
                        _irbisService.Connect(_configuration.Irbis.ConnectionString);
                        _irbisService.UseDatabase(_configuration.Irbis.BooksDatabase);
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (attempt < MAX_RETRIES)
                        await Task.Delay(RETRY_DELAY_MS);
                }
            }

            try
            {
                Trace.WriteLine("IRBIS startup connect failed: " + (lastException?.Message ?? "Unknown error"));
            } catch { }
            return false;
        }

        private async Task EnsureIrbisConnectedAsync()
        {
            await RunOnBackgroundThreadAsync(() =>
            {
                try
                {
                    _irbisService.UseDatabase(_configuration.Irbis.BooksDatabase);
                }
                catch
                {
                    _irbisService.Connect(_configuration.Irbis.ConnectionString);
                    _irbisService.UseDatabase(_configuration.Irbis.BooksDatabase);
                }
            });
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            InitializeUI();
            InitializeReaders();
        }

        private void InitializeUI()
        {
            SetUiTexts();
            
            // Регистрация экранов в ScreenManager
            _screenManager.RegisterScreen(LibraryTerminal.Screen.MainMenu, panelMenu);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.WaitingCardForTake, panelWaitCardTake);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.WaitingBookForTake, panelScanBook);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.WaitingCardForReturn, panelWaitCardReturn);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.WaitingBookForReturn, panelScanBookReturn);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.Success, panelSuccess);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.BookRejected, panelNoTag);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.CardValidationFailed, panelError);
            _screenManager.RegisterScreen(LibraryTerminal.Screen.NoSpaceAvailable, panelOverflow);
            
            NavigateToScreen(LibraryTerminal.Screen.MainMenu);

            // инфолейблы
            InitBookInfoLabels();
            InitReaderHeaderLabels();

            bool demoUi = bool.TryParse(ConfigurationManager.AppSettings["UseEmulator"], out var _) && _;
            if (demoUi) AddBackButtonForSim();
        }

        private void InitializeReaders()
        {
            var timeouts = _configuration.Timeouts;

            try
            {
                InitializeBookReaders(timeouts);
                InitializeArduino(timeouts);
                InitializeRru9816Reader();
                InitializeUhfReader09();
                InitializeIqrfidReader(timeouts);
                InitializeAcr1281Reader();

                // Инициализация BookOperationService после Arduino
                if (_arduinoController != null)
                {
                    _bookOperationService = new BookOperationService(_irbisService, _arduinoController);
                }

                _readerManager.StartAll();
            }
            catch (Exception ex)
            {
                Logger.Append("init.log", $"Failed to initialize readers: {ex.Message}");
                MessageBox.Show("Ошибка инициализации оборудования: " + ex.Message, "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeBookReaders(TimeoutConfiguration timeouts)
        {
            if (!_configuration.BookReader.Enabled) return;

            try
            {
                var config = _configuration.BookReader;
                string takePort = PortResolver.Resolve(config.TakePort);
                string returnPort = PortResolver.Resolve(config.ReturnPort);

                if (!string.IsNullOrWhiteSpace(takePort))
                {
                    _bookTakeReader = new BookReaderSerial(
                        takePort, config.BaudRate, config.NewLine,
                        timeouts.ReadTimeoutMs, timeouts.WriteTimeoutMs,
                        timeouts.AutoReconnectMs, timeouts.DebounceMs);
                    _bookTakeReader.TagRead += (s, tag) => OnBookTagTake(tag);
                    _readerManager.AddBookReader(_bookTakeReader);
                }

                if (!string.IsNullOrWhiteSpace(returnPort))
                {
                    if (_bookTakeReader != null && returnPort == takePort)
                    {
                        _bookReturnReader = _bookTakeReader;
                    }
                    else
                    {
                        _bookReturnReader = new BookReaderSerial(
                            returnPort, config.BaudRate, config.NewLine,
                            timeouts.ReadTimeoutMs, timeouts.WriteTimeoutMs,
                            timeouts.AutoReconnectMs, timeouts.DebounceMs);
                        _readerManager.AddBookReader(_bookReturnReader);
                    }
                    _bookReturnReader.TagRead += (s, tag) => OnBookTagReturn(tag);
                }
            }
            catch (Exception ex)
            {
                Logger.Append("book_reader.log", $"Failed to initialize book readers: {ex.Message}");
                MessageBox.Show("Ошибка инициализации книжных ридеров: " + ex.Message, "Книжные ридеры",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeArduino(TimeoutConfiguration timeouts)
        {
            if (!_configuration.Arduino.Enabled) return;

            try
            {
                var config = _configuration.Arduino;
                string port = PortResolver.Resolve(config.Port);

                if (!string.IsNullOrWhiteSpace(port))
                {
                    _arduinoClient = new ArduinoClientSerial(
                        port, config.BaudRate, config.NewLine,
                        timeouts.ReadTimeoutMs, timeouts.WriteTimeoutMs,
                        timeouts.AutoReconnectMs);
                    _arduinoClient.Start();
                    _arduinoController = _arduinoClient;

                    // Инициализация BookOperationService
                    var field = typeof(MainForm).GetField("_bookOperationService",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    field?.SetValue(this, new BookOperationService(_irbisService, _arduinoController));

                    LogArduino($"INIT: enable=True, port={port}, baud={config.BaudRate}, nl={EscapeNL(config.NewLine)}");
                }
                else
                {
                    LogArduino("INIT: enable=True, but no port specified — working in NULL mode (only logging)");
                }
            }
            catch (Exception ex)
            {
                LogArduino("START_ERR: " + ex.Message);
                MessageBox.Show("Оборудование (COM): " + ex.Message, "COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeRru9816Reader()
        {
            try
            {
                var config = _configuration.Rru9816;
                _rru9816BookReader = new Rru9816Reader(config.Port, config.BaudRate, 0x00);
                _rru9816BookReader.OnEpcHex += OnRruEpc;
                _rru9816BookReader.OnEpcHex += OnRruEpcDebug;
                _rru9816BookReader.Start();
                _readerManager.AddBookReader(_rru9816BookReader);

                var line = $"[RRU-DLL] Started on {(string.IsNullOrWhiteSpace(config.Port) ? "AUTO" : config.Port)} @ {config.BaudRate} (adr=0x00)";
                Console.WriteLine(line);
                Debug.WriteLine(line);
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}");
            }
            catch (BadImageFormatException ex)
            {
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BAD IMAGE: {ex.Message}");
                MessageBox.Show(
                    "RRU9816: неверная разрядность процесса/DLL.\n" +
                    "Нужно собирать x86 и положить x86 DLL рядом с .exe.",
                    "RRU9816", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (DllNotFoundException ex)
            {
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DLL NOT FOUND: {ex.Message}");
                MessageBox.Show(
                    "RRU9816: не найдена RRU9816.dll или её зависимости (dmdll.dll/CustomControl.dll).\n" +
                    "Убедитесь, что x86 DLL лежат рядом с .exe (bin\\x86\\...).",
                    "RRU9816", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RRU INIT EX: {ex}");
                MessageBox.Show("RRU9816 (DLL): " + ex.Message, "RRU9816",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeUhfReader09()
        {
            try
            {
                var config = _configuration.UhfReader09;
                _uhfReader09CardReader = new UhfReader09Reader();
                _uhfReader09CardReader.OnEpc += OnUhfCardUid;
                _uhfReader09CardReader.OnEpc += OnRruEpcDebug;

                if (!_uhfReader09CardReader.Start(baudIndex: config.BaudIndex, pollMs: config.PollMs))
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHFReader09: AutoOpenComPort FAILED");
                }
                else
                {
                    Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHFReader09: started (baudIdx={config.BaudIndex}, poll={config.PollMs}ms)");
                }
            }
            catch (BadImageFormatException ex)
            {
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHF09 BAD IMAGE: {ex.Message}");
                MessageBox.Show(
                    "UHFReader09: неверная разрядность процесса/DLL.\n" +
                    "Проверь, что проект собран под x86 и обе DLL (UHFReader09CSharp.dll, Basic.dll) лежат рядом с .exe.",
                    "UHFReader09", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (DllNotFoundException ex)
            {
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHF09 DLL NOT FOUND: {ex.Message}");
                MessageBox.Show(
                    "UHFReader09: не найдены DLL (UHFReader09CSharp.dll / Basic.dll).\n" +
                    "Скопируй их в папку рядом с .exe и установи Platform target = x86.",
                    "UHFReader09", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Logger.Append("rru.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UHF09 INIT EX: {ex}");
                MessageBox.Show("UHFReader09 (DLL): " + ex.Message, "UHFReader09",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeIqrfidReader(TimeoutConfiguration timeouts)
        {
            try
            {
                var _ = InitIqrfidAutoOrFixedAsync(
                    timeouts.ReadTimeoutMs, timeouts.WriteTimeoutMs,
                    timeouts.AutoReconnectMs, timeouts.DebounceMs);
            }
            catch (Exception ex)
            {
                MessageBox.Show("IQRFID-5102: " + ex.Message, "IQRFID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeAcr1281Reader()
        {
            try
            {
                var config = _configuration.Acr1281;
                string preferred = config.PreferredReaderName;
                if (string.IsNullOrWhiteSpace(preferred))
                    preferred = FindPreferredPiccReaderName() ?? "";

                if (string.IsNullOrWhiteSpace(preferred))
                    _acr1281CardReader = new Acr1281PcscReader();
                else
                {
                    try { _acr1281CardReader = new Acr1281PcscReader(preferred); }
                    catch { _acr1281CardReader = new Acr1281PcscReader(); }
                }

                _acr1281CardReader.CardRead += (s, uid) => OnAnyCardUid(uid, "ACR1281");
                _acr1281CardReader.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("PC/SC (ACR1281): " + ex.Message, "PC/SC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Останавливаем ScreenManager
            try { _screenManager?.Dispose(); } catch { }

            // Отписываемся от событий
            try { if (_rru9816BookReader != null) _rru9816BookReader.OnEpcHex -= OnRruEpcDebug; } catch { }
            try { if (_rru9816BookReader != null) _rru9816BookReader.OnEpcHex -= OnRruEpc; } catch { }
            try { if (_uhfReader09CardReader != null) _uhfReader09CardReader.OnEpc -= OnRruEpcDebug; } catch { }
            try { if (_uhfReader09CardReader != null) _uhfReader09CardReader.OnEpc -= OnUhfCardUid; } catch { }

            // Освобождаем ресурсы через ReaderManager
            try { _readerManager?.Dispose(); } catch { }

            // Освобождаем остальные ресурсы
            try { if (_bookReturnReader != null && _bookReturnReader != _bookTakeReader) _bookReturnReader.Dispose(); } catch { }
            try { if (_bookTakeReader != null) _bookTakeReader.Dispose(); } catch { }
            try { if (_arduinoClient != null) _arduinoClient.Dispose(); } catch { }
            try { if (_acr1281CardReader != null) _acr1281CardReader.Dispose(); } catch { }
            try { if (_iqrfidCardReader != null) _iqrfidCardReader.Dispose(); } catch { }
            try { if (_irbisService != null) _irbisService.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        private void NavigateToScreen(LibraryTerminal.Screen screen, int? timeoutSeconds = null)
        {
            _currentScreenType = screen;
            _screenManager.NavigateTo(screen, timeoutSeconds);
        }

        private void OnScreenChanged(object sender, LibraryTerminal.Screen screen)
        {
            // Дополнительная логика при смене экрана при необходимости
        }

        private void OnScreenTimeout(object sender, EventArgs e)
        {
            _operationMode = Mode.None;
            NavigateToScreen(LibraryTerminal.Screen.MainMenu);
        }

        // ---------- пункты меню ----------
        private void btnTakeBook_Click(object sender, EventArgs e)
        {
            _operationMode = Mode.Take;
            _lastBookBrief = "";
            lblReaderInfoTake.Visible = false;
            NavigateToScreen(LibraryTerminal.Screen.WaitingCardForTake);
            SetBookInfo(_bookInfoTakeLabel, "");
        }

        private void btnReturnBook_Click(object sender, EventArgs e)
        {
            _operationMode = Mode.Return;
            _lastBookBrief = "";
            lblReaderInfoReturn.Visible = false;
            NavigateToScreen(LibraryTerminal.Screen.WaitingCardForReturn);
            SetBookInfo(_bookInfoReturnLabel, "");
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
            InvokeIfRequired(() => OnAnyCardUidInternal(rawUid, source));
        }

        private async void OnAnyCardUidInternal(string rawUid, string source)
        {
            Logger.Append("uids.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {rawUid}");

            string uid = NormalizeUid(rawUid);

            // Обрабатываем только на экранах ожидания карты
            if (_currentScreenType != LibraryTerminal.Screen.WaitingCardForTake &&
                _currentScreenType != LibraryTerminal.Screen.WaitingCardForReturn)
                return;

            bool isValid = await RunOnBackgroundThreadAsync(() => _irbisService.ValidateCard(uid));
            if (!isValid)
            {
                ArduinoError();
                NavigateToScreen(LibraryTerminal.Screen.CardValidationFailed, TIMEOUT_SECONDS_ERROR);
                return;
            }

            // Короткий вывод: [MFN читателя] ФИО (без UID и без префиксов)
            int readerMfn = _irbisService.LastReaderMfn;
            if (readerMfn <= 0)
            {
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnAnyCardUid: LastReaderMfn is invalid");
                ArduinoError();
                NavigateToScreen(LibraryTerminal.Screen.CardValidationFailed, TIMEOUT_SECONDS_ERROR);
                return;
            }

            string readerBrief = await SafeGetReaderBriefAsync(readerMfn);
            string readerNameOnly = ExtractReaderName(readerBrief);
            string readerLine = $"[MFN {readerMfn}] {readerNameOnly}";

            lblReaderInfoTake.Text = readerLine;
            lblReaderInfoReturn.Text = readerLine;
            lblReaderInfoTake.Visible = true;
            lblReaderInfoReturn.Visible = true;

            if (_currentScreenType == LibraryTerminal.Screen.WaitingCardForTake)
            {
                NavigateToScreen(LibraryTerminal.Screen.WaitingBookForTake);
                SetReaderHeader(readerLine, isReturn: false);
                lblReaderInfoTake.Visible = true;
            }
            else if (_currentScreenType == LibraryTerminal.Screen.WaitingCardForReturn)
            {
                NavigateToScreen(LibraryTerminal.Screen.WaitingBookForReturn);
                SetReaderHeader(readerLine, isReturn: true);
                lblReaderInfoReturn.Visible = true;
            }
        }

        private void InvokeIfRequired(Action action)
        {
            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                if (IsDisposed || Disposing) return;
                try
                {
                    BeginInvoke(action);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }

            action();
        }

        // UHFReader09 как ридер карты — без вывода UID в UI
        private void OnUhfCardUid(string epcHex)
        {
            InvokeIfRequired(() => OnUhfCardUidInternal(epcHex));
        }

        private void OnUhfCardUidInternal(string epcHex)
        {
            var uid = EpcToCardUid(epcHex);

            if (_currentScreenType == LibraryTerminal.Screen.WaitingCardForTake ||
                _currentScreenType == LibraryTerminal.Screen.WaitingCardForReturn)
                OnAnyCardUid(uid, "UHF09");

            // НИЧЕГО не рисуем в lblReaderInfo* — по требованию «убрать UID»
        }

        // EPC → UID (обрезка по длине из App.config: UhfCardUidLength)
        private static string EpcToCardUid(string epc)
        {
            if (string.IsNullOrWhiteSpace(epc)) return "";
            var hex = new string(epc.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
            int want = int.TryParse(ConfigurationManager.AppSettings["UhfCardUidLength"], out var w) ? w : 24;
            if (want > 0 && hex.Length >= want) return hex.Substring(0, want);
            return hex;
        }

        // ===== КНИЖНЫЕ ПОТОКИ =====
        private void StartBookFlowIfFree(string rawTagOrEpc, bool isReturn)
        {
            var bookKey = ResolveBookKey(rawTagOrEpc);
            if (string.IsNullOrWhiteSpace(bookKey)) return;

            if (!isReturn && _currentScreenType == LibraryTerminal.Screen.WaitingBookForTake)
                SetBookInfo(_bookInfoTakeLabel, "Идёт поиск книги…");
            if (isReturn && _currentScreenType == LibraryTerminal.Screen.WaitingBookForReturn)
                SetBookInfo(_bookInfoReturnLabel, "Идёт поиск книги…");

            var now = DateTime.UtcNow;
            int bookDebounceMs = _configuration?.Timeouts?.BookDebounceMs ?? 800;
            if (_lastBookKeyProcessed == bookKey && (now - _lastBookAt).TotalMilliseconds < bookDebounceMs)
                return;

            // Атомарная проверка и установка
            if (Interlocked.CompareExchange(ref _bookScanBusy, 1, 0) != 0)
                return;

            _lastBookKeyProcessed = bookKey;
            _lastBookAt = now;
            _lastBookTag = bookKey;

            var task = isReturn
                ? HandleReturnAsync(bookKey)
                : HandleTakeAsync(bookKey);

            task.ContinueWith(t =>
            {
                Interlocked.Exchange(ref _bookScanBusy, 0);
                if (t.IsFaulted)
                {
                    try
                    {
                        Logger.Append("irbis.log",
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Book flow exception: {t.Exception?.GetBaseException()?.Message}");
                    } catch { }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private void OnBookTagTake(string tag)
        {
            InvokeIfRequired(() => OnBookTagTakeInternal(tag));
        }

        private void OnBookTagTakeInternal(string tag)
        {
            if (_currentScreenType != LibraryTerminal.Screen.WaitingBookForTake) return;
            StartBookFlowIfFree(tag, isReturn: false);
        }

        private void OnBookTagReturn(string tag)
        {
            InvokeIfRequired(() => OnBookTagReturnInternal(tag));
        }

        private void OnBookTagReturnInternal(string tag)
        {
            if (_currentScreenType != LibraryTerminal.Screen.WaitingBookForReturn) return;
            StartBookFlowIfFree(tag, isReturn: true);
        }

        private void OnRruEpc(string epcHex)
        {
            InvokeIfRequired(() => OnRruEpcInternal(epcHex));
        }

        private void OnRruEpcInternal(string epcHex)
        {

            bool bypassCard = (ConfigurationManager.AppSettings["BypassCardForRruTest"] ?? "false")
                .Equals("true", StringComparison.OrdinalIgnoreCase);

            if (bypassCard && _currentScreenType == LibraryTerminal.Screen.WaitingCardForTake)
                NavigateToScreen(LibraryTerminal.Screen.WaitingBookForTake);
            if (bypassCard && _currentScreenType == LibraryTerminal.Screen.WaitingCardForReturn)
                NavigateToScreen(LibraryTerminal.Screen.WaitingBookForReturn);

            if (_currentScreenType == LibraryTerminal.Screen.WaitingBookForTake)
                StartBookFlowIfFree(epcHex, isReturn: false);
            else if (_currentScreenType == LibraryTerminal.Screen.WaitingBookForReturn)
                StartBookFlowIfFree(epcHex, isReturn: true);
        }

        private void OnRruEpcDebug(string epc)
        {
            if (IsDisposed) return;
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

        // Методы OpenBinAsync и HasSpaceAsync теперь используют _arduinoController через BookOperationService

        private static string EscapeNL(string s) => s?.Replace("\r", "\\r").Replace("\n", "\\n");

        // ====== ВЫДАЧА ======
        private async Task HandleTakeAsync(string bookTag)
        {
            try
            {
                await EnsureIrbisConnectedAsync();

                if (!BYPASS_CARD && (_irbisService == null || _irbisService.LastReaderMfn <= 0))
                {
                    lblError.Text = "Сначала выполните проверку читателя";
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: no reader (LastReaderMfn=0)");
                    ArduinoError();
                    NavigateToScreen(LibraryTerminal.Screen.CardValidationFailed, TIMEOUT_SECONDS_ERROR);
                    return;
                }

                // Показываем информацию о книге
                var rec = await RunOnBackgroundThreadAsync(() => _irbisService.FindOneByBookRfid(bookTag));
                if (rec != null)
                {
                    await ShowBookInfoOnLabel(rec, takeMode: true);
                    _lastBookMfn = rec.Mfn;
                }

                // Используем BookOperationService для выдачи
                if (_bookOperationService == null)
                {
                    throw new InvalidOperationException("BookOperationService не инициализирован");
                }
                var result = await _bookOperationService.TakeBookAsync(bookTag, _irbisService.LastReaderMfn);

                if (result.Success)
                {
                    _lastBookBrief = result.BookBrief.Replace("\r", " ").Replace("\n", " ").Trim();
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: OK tag={bookTag} mfn={result.BookMfn}");
                    SetSuccessWithMfn("Книга выдана", result.BookMfn);
                    NavigateToScreen(LibraryTerminal.Screen.Success, TIMEOUT_SECONDS_SUCCESS);
                }
                else
                {
                    SetBookInfo(_bookInfoTakeLabel, result.ErrorMessage);
                    ArduinoError();
                    NavigateToScreen(LibraryTerminal.Screen.BookRejected, TIMEOUT_SECONDS_NO_TAG);
                }
            }
            catch (Exception ex)
            {
                lblError.Text = "Ошибка выдачи: " + ex.Message;
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TAKE: EX={ex.Message}");
                ArduinoError();
                NavigateToScreen(LibraryTerminal.Screen.CardValidationFailed, TIMEOUT_SECONDS_ERROR);
            }
        }

        // ====== ВОЗВРАТ ======
        private async Task HandleReturnAsync(string bookTag)
        {
            try
            {
                await EnsureIrbisConnectedAsync();

                // Показываем информацию о книге
                var rec = await RunOnBackgroundThreadAsync(() => _irbisService.FindOneByBookRfid(bookTag));
                if (rec != null)
                {
                    await ShowBookInfoOnLabel(rec, takeMode: false);
                    _lastBookMfn = rec.Mfn;
                }

                // Используем BookOperationService для возврата
                if (_bookOperationService == null)
                {
                    throw new InvalidOperationException("BookOperationService не инициализирован");
                }
                var result = await _bookOperationService.ReturnBookAsync(bookTag);

                if (result.Success)
                {
                    _lastBookBrief = result.BookBrief.Replace("\r", " ").Replace("\n", " ").Trim();
                    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: OK tag={bookTag} mfn={result.BookMfn}");
                    SetSuccessWithMfn("Книга принята", result.BookMfn);
                    NavigateToScreen(LibraryTerminal.Screen.Success, TIMEOUT_SECONDS_SUCCESS);
                }
                else
                {
                    if (result.ErrorMessage.Contains("места"))
                    {
                        NavigateToScreen(LibraryTerminal.Screen.NoSpaceAvailable, TIMEOUT_SECONDS_NO_SPACE);
                    }
                    else
                    {
                        SetBookInfo(_bookInfoReturnLabel, result.ErrorMessage);
                        NavigateToScreen(LibraryTerminal.Screen.BookRejected, TIMEOUT_SECONDS_NO_TAG);
                    }
                }
            }
            catch (Exception ex)
            {
                lblError.Text = "Ошибка возврата: " + ex.Message;
                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RETURN: EX={ex.Message}");
                ArduinoError();
                NavigateToScreen(LibraryTerminal.Screen.CardValidationFailed, TIMEOUT_SECONDS_ERROR);
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
            back.Click += (s, e) => { _operationMode = Mode.None; NavigateToScreen(LibraryTerminal.Screen.MainMenu); };
            foreach (Control c in Controls) { var p = c as Panel; if (p != null) p.Controls.Add(back); }
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

                var booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "KAT%SERV09%";
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



        private static bool BookTagMatches910(string scanned, string hFromRecord)
        {
            var key = IrbisServiceManaged.NormalizeId(scanned);
            var nh = IrbisServiceManaged.NormalizeId(hFromRecord);
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
                var key = IrbisServiceManaged.NormalizeId(scanned);
                var hs = rec.Fields.Where(f => f.Tag == "910")
                    .Select(f => f.GetFirstSubFieldText('h'))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(IrbisServiceManaged.NormalizeId)
                    .ToArray();

                Logger.Append(
                    "irbis.log",
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                    "MFN=" + rec.Mfn + " key=" + key + " 910^h=[" + string.Join(",", hs) + "]"
                );
            } catch { }
        }

        // ====== Лейблы книги ======
        private void InitBookInfoLabels()
        {
            _bookInfoTakeLabel = new Label
            {
                AutoSize = false,
                Width = panelScanBook.Width - 40,
                Height = 48,
                Left = 20,
                Top = panelScanBook.Height - 60,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            panelScanBook.Controls.Add(_bookInfoTakeLabel);

            _bookInfoReturnLabel = new Label
            {
                AutoSize = false,
                Width = panelScanBookReturn.Width - 40,
                Height = 48,
                Left = 20,
                Top = panelScanBookReturn.Height - 60,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            panelScanBookReturn.Controls.Add(_bookInfoReturnLabel);

            SetBookInfo(_bookInfoTakeLabel, "");
            SetBookInfo(_bookInfoReturnLabel, "");
        }

        // ====== Шапка с ФИО ======
        private void InitReaderHeaderLabels()
        {
            _readerHeaderTakeLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 48,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold)
            };
            panelScanBook.Controls.Add(_readerHeaderTakeLabel);
            panelScanBook.Controls.SetChildIndex(_readerHeaderTakeLabel, 0);
            _readerHeaderTakeLabel.Text = "";

            _readerHeaderReturnLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 48,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold)
            };
            panelScanBookReturn.Controls.Add(_readerHeaderReturnLabel);
            panelScanBookReturn.Controls.SetChildIndex(_readerHeaderReturnLabel, 0);
            _readerHeaderReturnLabel.Text = "";
        }

        private void SetReaderHeader(string text, bool isReturn)
        {
            try
            {
                var label = isReturn ? _readerHeaderReturnLabel : _readerHeaderTakeLabel;
                if (label != null) label.Text = text ?? "";
            } catch { }
        }

        private void SetBookInfo(Label label, string text)
        {
            try { if (label != null) label.Text = text ?? ""; } catch { }
        }

        // --------- ВАЖНО: теперь строго «Название / Автор» ---------
        private string GetTitleAndAuthorOnly(string brief)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(brief)) return "";
                var s = brief.Replace("\r", " ").Replace("\n", " ").Trim();

                // Ищем разделитель авторов " / "
                int slash = s.IndexOf(" / ");
                string titlePart = s;
                string authors = "";
                if (slash >= 0)
                {
                    titlePart = s.Substring(0, slash);
                    string rest = s.Substring(slash + 3);
                    int semi = rest.IndexOf(" ;");
                    int comma = rest.IndexOf(", ");
                    int end = -1;
                    if (semi >= 0) end = semi;
                    else if (comma >= 0) end = comma;
                    authors = (end >= 0 ? rest.Substring(0, end) : rest).Trim();
                }

                // Убираем ведущего автора из начала (шаблон "Автор. Название")
                int dot = titlePart.IndexOf(". ");
                if (dot >= 0 && dot + 2 < titlePart.Length)
                    titlePart = titlePart.Substring(dot + 2).Trim();

                if (string.IsNullOrWhiteSpace(titlePart))
                    titlePart = s;

                return !string.IsNullOrWhiteSpace(authors)
                    ? $"{titlePart} / {authors}"
                    : titlePart;
            } catch
            {
                return brief ?? "";
            }
        }

        // Из brief читателя берём только ФИО (первую «разумную» часть)
        private string ExtractReaderName(string brief)
        {
            if (string.IsNullOrWhiteSpace(brief)) return "Читатель идентифицирован";
            var s = brief.Replace("\r", " ").Replace("\n", " ").Trim();

            // Часто ФИО до первой «,» или «;»
            int cut = s.IndexOf(',');
            if (cut < 0) cut = s.IndexOf(';');
            if (cut > 0) s = s.Substring(0, cut);

            // защита от очень длинных строк
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Trim();
        }

        private async Task ShowBookInfoOnLabel(ManagedClient.IrbisRecord rec, bool takeMode)
        {
            try
            {
                string brief = await SafeGetBookBriefAsync(rec.Mfn);
                if (string.IsNullOrWhiteSpace(brief))
                {
                    var title = rec.FM("200", 'a') ?? "(без заглавия)";
                    brief = title;
                }
                var minimal = GetTitleAndAuthorOnly(brief);
                var info = $"[MFN {rec.Mfn}] {minimal}";

                _lastBookBrief = minimal;

                if (takeMode) SetBookInfo(_bookInfoTakeLabel, info);
                else SetBookInfo(_bookInfoReturnLabel, info);

                Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(takeMode ? "TAKE" : "RETURN")}: {info}");
            } catch
            {
                _lastBookBrief = "";
                if (takeMode) SetBookInfo(_bookInfoTakeLabel, $"[MFN {rec.Mfn}]");
                else SetBookInfo(_bookInfoReturnLabel, $"[MFN {rec.Mfn}]");
            }
        }

        private void SetSuccessWithMfn(string action, int mfn)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_lastBookBrief))
                {
                    // тут уже minimal
                    lblSuccess.Text = $"{action}\r\nMFN книги: {mfn}\r\n{_lastBookBrief}";
                }
                else
                {
                    lblSuccess.Text = $"{action}\r\nMFN книги: {mfn}";
                }
            } catch
            {
                lblSuccess.Text = $"{action}\r\nMFN книги: {mfn}";
            }
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
                        } catch (TimeoutException) { }
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
