# Конкретные исправления кода

## Исправление 1: Race Condition в StartBookFlowIfFree

**Файл:** `MainForm.cs`

**Было:**
```csharp
private volatile bool _bookScanBusy = false;

private void StartBookFlowIfFree(string rawTagOrEpc, bool isReturn)
{
    // ...
    if (_bookScanBusy) return;
    _bookScanBusy = true;
    // ...
    var _ = (isReturn
        ? HandleReturnAsync(bookKey)
        : HandleTakeAsync(bookKey)
    ).ContinueWith(__ => { _bookScanBusy = false; });
}
```

**Стало:**
```csharp
private int _bookScanBusy = 0; // 0 = свободно, 1 = занято

private void StartBookFlowIfFree(string rawTagOrEpc, bool isReturn)
{
    var bookKey = ResolveBookKey(rawTagOrEpc);
    if (string.IsNullOrWhiteSpace(bookKey)) return;

    if (!isReturn && _screen == Screen.S3_WaitBookTake)
        SetBookInfo(lblBookInfoTake, "Идёт поиск книги…");
    if (isReturn && _screen == Screen.S5_WaitBookReturn)
        SetBookInfo(lblBookInfoReturn, "Идёт поиск книги…");

    var now = DateTime.UtcNow;
    if (_lastBookKeyProcessed == bookKey && (now - _lastBookAt).TotalMilliseconds < BookDebounceMs)
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
    
    task.ContinueWith(t => {
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
```

## Исправление 2: async void в OnShown

**Файл:** `MainForm.cs`

**Было:**
```csharp
protected override async void OnShown(EventArgs e)
{
    base.OnShown(e);
    var ok = await InitIrbisWithRetryAsync();
    try { Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IRBIS startup: {(ok ? "connected OK" : "FAILED")}"); } catch { }
}
```

**Стало:**
```csharp
protected override void OnShown(EventArgs e)
{
    base.OnShown(e);
    
    // Fire and forget с обработкой ошибок
    _ = InitIrbisWithRetryAsync().ContinueWith(t => {
        try
        {
            bool ok = t.IsCompletedSuccessfully && t.Result;
            Logger.Append("irbis.log", 
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IRBIS startup: {(ok ? "connected OK" : "FAILED")}");
            
            if (t.IsFaulted)
            {
                Logger.Append("irbis.log", 
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IRBIS startup exception: {t.Exception?.GetBaseException()?.Message}");
            }
        } catch { }
    }, TaskScheduler.FromCurrentSynchronizationContext());
}
```

## Исправление 3: Блокирующий Wait в async методе

**Файл:** `MainForm.cs` (класс `UhfReader09Reader`)

**Было:**
```csharp
private void PollLoop(CancellationToken ct, int periodMs)
{
    // ...
    if (periodMs > 0) { try { Task.Delay(periodMs, ct).Wait(ct); } catch { } }
}
```

**Стало:**
```csharp
private async Task PollLoopAsync(CancellationToken ct, int periodMs)
{
    var buf = new byte[8192];
    
    while (!ct.IsCancellationRequested)
    {
        try
        {
            int total = 0, cnt = 0;
            int ret = StaticClassReaderB.Inventory_G2(ref _addr, 0, 0, 0, buf, ref total, ref cnt, _comIdx);

            if (ret == 1 || ret == 2 || ret == 3)
            {
                for (int i = 0, seen = 0; i < total && seen < cnt; seen++)
                {
                    int len = buf[i];
                    if (len <= 0 || i + 1 + len > total) break;
                    OnEpc?.Invoke(BytesToHex(buf, i + 1, len));
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

// И в Start():
public bool Start(int baudIndex = 3, int pollMs = 100, int? forcedPort = null)
{
    // ...
    _cts = new CancellationTokenSource();
    _loop = Task.Run(() => PollLoopAsync(_cts.Token, pollMs), _cts.Token);
    return true;
}
```

## Исправление 4: Утечка Timer

**Файл:** `MainForm.cs`

**Было:**
```csharp
protected override void OnFormClosing(FormClosingEventArgs e)
{
    // ... dispose ридеров ...
    // Timer не освобождается!
    base.OnFormClosing(e);
}
```

**Стало:**
```csharp
protected override void OnFormClosing(FormClosingEventArgs e)
{
    // Останавливаем и освобождаем таймер
    try 
    { 
        _tick?.Stop(); 
        _tick?.Dispose(); 
        _tick = null;
    } catch { }
    
    try { if (_rruDll != null) _rruDll.OnEpcHex -= OnRruEpcDebug; } catch { }
    try { if (_rruDll != null) _rruDll.OnEpcHex -= OnRruEpc; } catch { }
    try { if (_rruDll != null) _rruDll.Dispose(); } catch { }

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
```

## Исправление 5: Проверка на disposed в BeginInvoke

**Файл:** `MainForm.cs`

**Было:**
```csharp
private void OnAnyCardUid(string rawUid, string source)
{
    if (InvokeRequired) { BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source); return; }
    var _ = OnAnyCardUidAsync(rawUid, source);
}
```

**Стало:**
```csharp
private void OnAnyCardUid(string rawUid, string source)
{
    if (IsDisposed || Disposing) return;
    
    if (InvokeRequired) 
    { 
        if (IsDisposed || Disposing) return;
        try
        {
            BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        return; 
    }
    
    var _ = OnAnyCardUidAsync(rawUid, source);
}
```

## Исправление 6: Проверка LastReaderMfn

**Файл:** `MainForm.cs`

**Было:**
```csharp
string readerBrief = await SafeGetReaderBriefAsync(_svc.LastReaderMfn);
string readerNameOnly = ExtractReaderName(readerBrief);
string readerLine = $"[MFN {_svc.LastReaderMfn}] {readerNameOnly}";
```

**Стало:**
```csharp
int readerMfn = _svc?.LastReaderMfn ?? 0;
if (readerMfn <= 0)
{
    Logger.Append("irbis.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnAnyCardUid: LastReaderMfn is invalid");
    return;
}

string readerBrief = await SafeGetReaderBriefAsync(readerMfn);
string readerNameOnly = ExtractReaderName(readerBrief);
string readerLine = $"[MFN {readerMfn}] {readerNameOnly}";
```

## Исправление 7: Безопасная работа с событиями

**Файл:** `Devices.cs`, `Rru9816Reader.cs`, `Acr1281PcscReader.cs`

**Было:**
```csharp
OnUid?.Invoke(uid);
OnEpcHex?.Invoke(BytesToHex(epcBytes));
```

**Стало:**
```csharp
// Сохраняем делегат в локальную переменную для thread-safety
var handler = OnUid;
if (handler != null)
{
    try { handler(uid); }
    catch (Exception ex)
    {
        try { Logger.Append("error.log", $"OnUid handler exception: {ex.Message}"); } catch { }
    }
}
```

## Исправление 8: Проверка IsOpen в ArduinoClientSerial

**Файл:** `Devices.cs`

**Было:**
```csharp
public bool HasSpace()
{
    var resp = Request("SPACE?", _syncTimeoutMs);
    // ...
}
```

**Стало:**
```csharp
public bool HasSpace()
{
    if (!IsOpen)
    {
        LogArduino("HAS_SPACE: port not open, returning false");
        return false;
    }
    
    try
    {
        var resp = Request("SPACE?", _syncTimeoutMs);
        var t = (resp ?? "").Trim().ToUpperInvariant();
        if (t.StartsWith("SPACE")) t = t.Substring(5).TrimStart(' ', ':', '=');
        return t.StartsWith("1");
    }
    catch (Exception ex)
    {
        LogArduino($"HAS_SPACE exception: {ex.Message}");
        return false;
    }
}
```

## Исправление 9: Улучшенная обработка исключений в SerialWorker

**Файл:** `SerialWorker.cs`

**Было:**
```csharp
catch (Exception ex)
{
    try { OnClosed(ex); } catch { }
    Stop();
    Start();
}
```

**Стало:**
```csharp
catch (Exception ex)
{
    try { OnClosed(ex); } catch { }
    // Не вызываем Stop/Start здесь - WorkerLoop сам переподключится
    // Просто логируем
    try 
    { 
        Logger.Append("serial.log", $"OnData exception: {ex.Message}"); 
    } catch { }
}
```

## Исправление 10: Проверка Timer перед использованием

**Файл:** `MainForm.cs`

**Было:**
```csharp
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
```

**Стало:**
```csharp
private void Switch(Screen s, Panel panel, int? timeoutSeconds)
{
    _screen = s;
    ShowScreen(panel);
    if (timeoutSeconds.HasValue)
    {
        _deadline = DateTime.Now.AddSeconds(timeoutSeconds.Value);
        if (!IsDisposed && !Disposing && _tick != null)
            _tick.Enabled = true;
    }
    else 
    { 
        _deadline = null; 
        if (!IsDisposed && !Disposing && _tick != null)
            _tick.Enabled = false; 
    }
}
```

## Дополнительные рекомендации

### Использование using для временных ресурсов

В `IrbisServiceManaged.cs` методы `SafeGetReaderBriefAsync` и `SafeGetBookBriefAsync` создают новый клиент:

```csharp
// Было:
using (var client = new ManagedClient64())
{
    // ...
}

// Рекомендация: Добавить проверку на null и улучшить обработку ошибок
ManagedClient64 client = null;
try
{
    client = new ManagedClient64();
    client.ParseConnectionString(GetConnString());
    client.Connect();
    // ...
}
catch (Exception ex)
{
    Logger.Append("irbis.log", $"Client creation failed: {ex.Message}");
    throw;
}
finally
{
    client?.Dispose();
}
```

### Добавление валидации входных данных

Во всех публичных методах добавить проверки:

```csharp
public bool ValidateCard(string uid)
{
    if (string.IsNullOrWhiteSpace(uid))
        throw new ArgumentException("UID cannot be null or empty", nameof(uid));
    
    // остальной код...
}
```
