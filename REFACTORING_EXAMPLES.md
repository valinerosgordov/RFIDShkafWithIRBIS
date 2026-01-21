# Примеры рефакторинга

## Пример 1: Выделение интерфейсов

### Было:
```csharp
public partial class MainForm : Form
{
    private IrbisServiceManaged _svc;
    private ArduinoClientSerial _ardu;
    private Acr1281PcscReader _acr;
    
    private async Task HandleTakeAsync(string bookTag)
    {
        var rec = await OffUi<ManagedClient.IrbisRecord>(() => _svc.FindOneByBookRfid(bookTag));
        // ...
    }
}
```

### Стало:
```csharp
// Интерфейсы
public interface IIrbisService
{
    Task<BookRecord> FindBookByRfidAsync(string rfid);
    Task<bool> ValidateCardAsync(string uid);
    Task<OperationResult> IssueBookAsync(string rfid, int readerMfn);
    Task<OperationResult> ReturnBookAsync(string rfid);
    int LastReaderMfn { get; }
}

public interface IArduinoController
{
    Task<bool> HasSpaceAsync();
    Task OpenBinAsync();
    void SendCommand(string command);
    void SendOk();
    void SendError();
    void SendBeep(int milliseconds);
}

// MainForm
public partial class MainForm : Form
{
    private readonly IIrbisService _irbisService;
    private readonly IArduinoController _arduinoController;
    private readonly IBookReaderManager _bookReaderManager;
    private readonly ICardReaderManager _cardReaderManager;
    
    public MainForm(
        IIrbisService irbisService,
        IArduinoController arduinoController,
        IBookReaderManager bookReaderManager,
        ICardReaderManager cardReaderManager)
    {
        _irbisService = irbisService;
        _arduinoController = arduinoController;
        _bookReaderManager = bookReaderManager;
        _cardReaderManager = cardReaderManager;
    }
    
    private async Task HandleTakeAsync(string bookTag)
    {
        var book = await _irbisService.FindBookByRfidAsync(bookTag);
        // ...
    }
}
```

## Пример 2: Разделение MainForm на классы

### Было:
```csharp
public partial class MainForm : Form
{
    // 1400+ строк кода
    private void MainForm_Load(object sender, EventArgs e)
    {
        // 200+ строк инициализации
    }
    
    private async Task HandleTakeAsync(string bookTag) { /* ... */ }
    private async Task HandleReturnAsync(string bookTag) { /* ... */ }
    // ... еще 50+ методов
}
```

### Стало:
```csharp
// ScreenManager - управление экранами
public class ScreenManager
{
    private readonly Dictionary<Screen, Panel> _screens;
    private Screen _currentScreen;
    
    public void NavigateTo(Screen screen, int? timeoutSeconds = null)
    {
        HideAllScreens();
        _screens[screen].Visible = true;
        _currentScreen = screen;
        
        if (timeoutSeconds.HasValue)
            StartTimeout(timeoutSeconds.Value);
    }
    
    public Screen CurrentScreen => _currentScreen;
}

// BookOperationController - бизнес-логика операций
public class BookOperationController
{
    private readonly IIrbisService _irbisService;
    private readonly IArduinoController _arduinoController;
    
    public async Task<OperationResult> TakeBookAsync(string bookRfid, int readerMfn)
    {
        var book = await _irbisService.FindBookByRfidAsync(bookRfid);
        if (book == null)
            return OperationResult.Fail("Книга не найдена");
        
        if (!book.CanBeIssued)
            return OperationResult.Fail("Книга уже выдана");
        
        var result = await _irbisService.IssueBookAsync(bookRfid, readerMfn);
        if (result.Success)
        {
            await _arduinoController.OpenBinAsync();
        }
        
        return result;
    }
}

// ReaderManager - управление ридерами
public class ReaderManager : IReaderManager
{
    private readonly List<IBookReader> _bookReaders;
    private readonly List<ICardReader> _cardReaders;
    
    public void StartAll()
    {
        foreach (var reader in _bookReaders.Concat(_cardReaders.Cast<IReader>()))
        {
            reader.Start();
        }
    }
}

// MainForm - только UI
public partial class MainForm : Form
{
    private readonly ScreenManager _screenManager;
    private readonly BookOperationController _operationController;
    private readonly IReaderManager _readerManager;
    
    private void MainForm_Load(object sender, EventArgs e)
    {
        InitializeUI();
        _readerManager.StartAll();
    }
    
    private async void OnBookTagRead(object sender, string tag)
    {
        var result = await _operationController.TakeBookAsync(tag, _currentReaderMfn);
        if (result.Success)
            _screenManager.NavigateTo(Screen.Success);
        else
            _screenManager.NavigateTo(Screen.Error, result.ErrorMessage);
    }
}
```

## Пример 3: Configuration Service

### Было:
```csharp
// Конфигурация разбросана по коду
string conn = ConfigurationManager.AppSettings["connection-string"] ?? "...";
string db = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
string rruPort = ConfigurationManager.AppSettings["RruPort"] ?? "COM5";
```

### Стало:
```csharp
// ConfigurationService
public class AppConfiguration
{
    public IrbisConfiguration Irbis { get; }
    public ReaderConfiguration BookReader { get; }
    public ReaderConfiguration CardReader { get; }
    public ArduinoConfiguration Arduino { get; }
    public TimeoutConfiguration Timeouts { get; }
    
    public static AppConfiguration Load()
    {
        return new AppConfiguration
        {
            Irbis = new IrbisConfiguration
            {
                ConnectionString = GetSetting("connection-string", "host=127.0.0.1;port=6666;..."),
                BooksDatabase = GetSetting("BooksDb", "IBIS"),
                ReadersDatabase = GetSetting("ReadersDb", "RDR")
            },
            BookReader = new ReaderConfiguration
            {
                Port = GetSetting("BookTakePort", ""),
                BaudRate = GetIntSetting("BaudBookTake", 9600),
                Enabled = GetBoolSetting("EnableBookScanners", false)
            },
            // ...
        };
    }
    
    private static string GetSetting(string key, string defaultValue)
    {
        return ConfigurationManager.AppSettings[key] ?? defaultValue;
    }
}

// Использование
public class ReaderManager
{
    private readonly AppConfiguration _config;
    
    public ReaderManager(AppConfiguration config)
    {
        _config = config;
    }
    
    public void Initialize()
    {
        if (_config.BookReader.Enabled)
        {
            var reader = new BookReaderSerial(
                _config.BookReader.Port,
                _config.BookReader.BaudRate,
                // ...
            );
        }
    }
}
```

## Пример 4: Улучшение именования

### Было:
```csharp
private IrbisServiceManaged _svc;
private ArduinoClientSerial _ardu;
private Acr1281PcscReader _acr;
private Rru9816Reader _rruDll;
private UhfReader09Reader _uhf09;
private CardReaderSerial _iqrfid;

private Label lblBookInfoTake;
private Button btnTakeBook;
private Panel panelMenu;

private static Task OffUi(Action a) => Task.Run(a);
private static char N(int v) => (char)(v < 10 ? ('0' + v) : ('A' + v - 10));
```

### Стало:
```csharp
private readonly IIrbisService _irbisService;
private readonly IArduinoController _arduinoController;
private readonly ICardReader _acr1281CardReader;
private readonly IBookReader _rru9816BookReader;
private readonly ICardReader _uhfReader09CardReader;
private readonly ICardReader _iqrfidCardReader;

private Label _bookInfoTakeLabel;
private Button _takeBookButton;
private Panel _menuPanel;

private static Task RunOnBackgroundThreadAsync(Action action) => Task.Run(action);
private static char ConvertHexDigitToChar(int value) => (char)(value < 10 ? ('0' + value) : ('A' + value - 10));
```

## Пример 5: Устранение дублирования

### Было:
```csharp
private void OnAnyCardUid(string rawUid, string source)
{
    if (IsDisposed || Disposing) return;
    if (InvokeRequired)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        return;
    }
    var _ = OnAnyCardUidAsync(rawUid, source);
}

private void OnBookTagTake(string tag)
{
    if (IsDisposed || Disposing) return;
    if (InvokeRequired)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(new Action<string>(OnBookTagTake), tag); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        return;
    }
    if (_screen != Screen.S3_WaitBookTake) return;
    StartBookFlowIfFree(tag, isReturn: false);
}
```

### Стало:
```csharp
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

private void OnAnyCardUid(string rawUid, string source)
{
    InvokeIfRequired(() => OnAnyCardUidInternal(rawUid, source));
}

private void OnBookTagTake(string tag)
{
    InvokeIfRequired(() => OnBookTagTakeInternal(tag));
}

private void OnBookTagTakeInternal(string tag)
{
    if (_screen != Screen.S3_WaitBookTake) return;
    StartBookFlowIfFree(tag, isReturn: false);
}
```

## Пример 6: Разбиение длинного метода

### Было:
```csharp
private void MainForm_Load(object sender, EventArgs e)
{
    _tick.Tick += Tick_Tick;
    SetUiTexts();
    ShowScreen(panelMenu);
    InitBookInfoLabels();
    InitReaderHeaderLabels();
    if (DEMO_UI) AddBackButtonForSim();
    
    try
    {
        int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
        int writeTo = int.Parse(ConfigurationManager.AppSettings["WriteTimeoutMs"] ?? "700");
        int reconnMs = int.Parse(ConfigurationManager.AppSettings["AutoReconnectMs"] ?? "1500");
        int debounce = int.Parse(ConfigurationManager.AppSettings["DebounceMs"] ?? "250");
        
        // --- COM: книжные ридеры + Arduino
        try
        {
            if (ENABLE_BOOK_SCANNERS)
            {
                string bookTakePort = PortResolver.Resolve(...);
                // ... 50+ строк
            }
            
            if (ENABLE_ARDUINO)
            {
                // ... 30+ строк
            }
        } catch (Exception ex) { /* ... */ }
        
        // --- RRU9816 через DLL
        try { /* ... 40+ строк */ } catch { /* ... */ }
        
        // --- UHFReader09 SDK
        try { /* ... 40+ строк */ } catch { /* ... */ }
        
        // --- IQRFID
        try { /* ... 20+ строк */ } catch { /* ... */ }
        
        // --- PC/SC: ACR1281
        try { /* ... 20+ строк */ } catch { /* ... */ }
    } catch (Exception ex) { /* ... */ }
}
```

### Стало:
```csharp
private void MainForm_Load(object sender, EventArgs e)
{
    InitializeTimer();
    InitializeUI();
    InitializeReaders();
}

private void InitializeTimer()
{
    _tick.Tick += Tick_Tick;
}

private void InitializeUI()
{
    SetUiTexts();
    ShowScreen(_menuPanel);
    InitBookInfoLabels();
    InitReaderHeaderLabels();
    
    if (DEMO_UI)
        AddBackButtonForSim();
}

private void InitializeReaders()
{
    var config = _configurationService.Load();
    var timeoutConfig = config.Timeouts;
    
    try
    {
        InitializeBookReaders(config.BookReader, timeoutConfig);
        InitializeArduino(config.Arduino, timeoutConfig);
        InitializeRru9816Reader(config.Rru9816);
        InitializeUhfReader09(config.UhfReader09);
        InitializeIqrfidReader(config.Iqrfid, timeoutConfig);
        InitializeAcr1281Reader(config.Acr1281);
    }
    catch (Exception ex)
    {
        Logger.Error($"Failed to initialize readers: {ex.Message}");
        ShowError("Ошибка инициализации оборудования");
    }
}

private void InitializeBookReaders(ReaderConfiguration config, TimeoutConfiguration timeouts)
{
    if (!config.Enabled) return;
    
    try
    {
        var takeReader = CreateBookReader(config.TakePort, config, timeouts);
        takeReader.OnTagRead += OnBookTagTake;
        takeReader.Start();
        _bookReaderManager.AddReader(takeReader);
        
        if (config.TakePort != config.ReturnPort)
        {
            var returnReader = CreateBookReader(config.ReturnPort, config, timeouts);
            returnReader.OnTagRead += OnBookTagReturn;
            returnReader.Start();
            _bookReaderManager.AddReader(returnReader);
        }
    }
    catch (Exception ex)
    {
        Logger.Error($"Failed to initialize book readers: {ex.Message}");
        ShowWarning("Ошибка инициализации книжных ридеров");
    }
}
```

## Пример 7: Валидация и доменная модель

### Было:
```csharp
private async Task HandleTakeAsync(string bookTag)
{
    var rec = await OffUi<ManagedClient.IrbisRecord>(() => _svc.FindOneByBookRfid(bookTag));
    if (rec == null) { /* ошибка */ return; }
    
    var f910 = rec.Fields.Where(f => f.Tag == "910")
        .FirstOrDefault(f => BookTagMatches910(bookTag, f.GetFirstSubFieldText('h')));
    if (f910 == null) { /* ошибка */ return; }
    
    string status = f910.GetFirstSubFieldText('a') ?? string.Empty;
    bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
    if (!canIssue) { /* ошибка */ return; }
}
```

### Стало:
```csharp
// Доменная модель
public class Book
{
    public int Mfn { get; }
    public string Rfid { get; }
    public BookStatus Status { get; }
    public string Title { get; }
    public string Author { get; }
    
    public bool CanBeIssued => Status == BookStatus.InStock;
    public bool CanBeReturned => Status == BookStatus.Issued;
}

public enum BookStatus
{
    InStock,
    Issued,
    Reserved,
    Lost
}

// Сервис
public class BookService
{
    public async Task<Book> FindBookByRfidAsync(string rfid)
    {
        var validator = new RfidValidator();
        var validationResult = validator.Validate(rfid);
        if (!validationResult.IsValid)
            throw new InvalidRfidException(validationResult.ErrorMessage);
        
        var record = await _irbisService.FindBookRecordAsync(rfid);
        if (record == null)
            return null;
        
        return MapToBook(record);
    }
    
    private Book MapToBook(IrbisRecord record)
    {
        var statusField = record.Fields.FirstOrDefault(f => f.Tag == "910");
        var status = ParseBookStatus(statusField?.GetSubField('a'));
        
        return new Book
        {
            Mfn = record.Mfn,
            Rfid = ExtractRfid(record),
            Status = status,
            Title = record.GetField("200")?.GetSubField('a') ?? "",
            Author = record.GetField("200")?.GetSubField('f') ?? ""
        };
    }
}

// Использование
private async Task HandleTakeAsync(string bookTag)
{
    try
    {
        var book = await _bookService.FindBookByRfidAsync(bookTag);
        if (book == null)
        {
            ShowError("Книга не найдена");
            return;
        }
        
        if (!book.CanBeIssued)
        {
            ShowError("Книга уже выдана");
            return;
        }
        
        await _bookService.IssueBookAsync(book, _currentReader);
        await _arduinoController.OpenBinAsync();
        ShowSuccess();
    }
    catch (InvalidRfidException ex)
    {
        ShowError($"Неверный формат RFID: {ex.Message}");
    }
    catch (Exception ex)
    {
        Logger.Error($"Error in HandleTakeAsync: {ex}");
        ShowError("Произошла ошибка при выдаче книги");
    }
}
```

## Пример 8: Улучшение обработки ошибок

### Было:
```csharp
private async Task<bool> InitIrbisWithRetryAsync()
{
    string conn = GetConnString();
    string db = GetBooksDb();
    if (_svc == null) _svc = new IrbisServiceManaged();
    
    Exception last = null;
    for (int i = 0; i < 5; i++)
    {
        try { await OffUi(delegate { _svc.Connect(conn); _svc.UseDatabase(db); }); return true; } 
        catch (Exception ex) { last = ex; await Task.Delay(1500); }
    }
    try { Trace.WriteLine("IRBIS startup connect failed: " + (last != null ? last.Message : "")); } catch { }
    return false;
}
```

### Стало:
```csharp
public class ConnectionRetryPolicy
{
    private const int MAX_RETRIES = 5;
    private const int RETRY_DELAY_MS = 1500;
    
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        Func<Exception, bool> shouldRetry,
        ILogger logger)
    {
        Exception lastException = null;
        
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                if (!shouldRetry(ex))
                {
                    logger.Error($"Operation failed and should not be retried: {ex.Message}");
                    throw;
                }
                
                logger.Warning($"Operation attempt {attempt}/{MAX_RETRIES} failed: {ex.Message}");
                
                if (attempt < MAX_RETRIES)
                {
                    await Task.Delay(RETRY_DELAY_MS);
                }
            }
        }
        
        logger.Error($"Operation failed after {MAX_RETRIES} attempts: {lastException?.Message}");
        throw new OperationFailedException("Operation failed after all retries", lastException);
    }
}

// Использование
private async Task<bool> InitIrbisWithRetryAsync()
{
    var retryPolicy = new ConnectionRetryPolicy();
    
    try
    {
        await retryPolicy.ExecuteWithRetryAsync(
            async () =>
            {
                await _irbisService.ConnectAsync(_config.Irbis.ConnectionString);
                await _irbisService.UseDatabaseAsync(_config.Irbis.BooksDatabase);
                return true;
            },
            ex => ex is NetworkException || ex is TimeoutException,
            Logger);
        
        return true;
    }
    catch (OperationFailedException)
    {
        return false;
    }
}
```
