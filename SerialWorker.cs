using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LibraryTerminal
{
    internal class SerialWorker : IDisposable
    {
        // Глобальная защита от двойного открытия одного COM в рамках процесса
        private static readonly ConcurrentDictionary<string, object> _portGuards =
            new ConcurrentDictionary<string, object>();

        private readonly string _portName;
        private readonly int _baud;
        private readonly string _newline;
        private readonly int _readTimeoutMs;
        private readonly int _writeTimeoutMs;
        private readonly int _reconnectDelayMs;

        private SerialPort _sp;
        private CancellationTokenSource _cts;
        private Task _readerTask;

        private readonly StringBuilder _lineBuf = new StringBuilder(256);
        private readonly object _bufLock = new object();
        private const int IdleFlushMs = 250;
        private DateTime _lastByteAt = DateTime.MinValue;

        // Публичные "хуки" как делегаты
        public Action<string> OnBeforeWrite;   // вызывается перед записью строки
        public Action<string> OnLineReceived;  // сырая строка до OnLine()

        // Публичные свойства (диагностика/совместимость)
        public string PortName { get { return _portName; } }
        public int BaudRate { get { return _baud; } }
        public string NewLine { get { return _newline; } }
        public int ReadTimeoutMs { get { return _readTimeoutMs; } }
        public int WriteTimeoutMs { get { return _writeTimeoutMs; } }
        public int AutoReconnectMs { get { return _reconnectDelayMs; } }
        public int ReconnectMs { get { return _reconnectDelayMs; } } // алиас
        public bool IsOpen { get { return _sp != null && _sp.IsOpen; } }

        public SerialWorker(string port, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int autoReconnectMs)
        {
            _portName = (port ?? "").Trim();
            _baud = baud > 0 ? baud : 115200;
            _newline = string.IsNullOrEmpty(newline) ? "\n" : newline;
            _readTimeoutMs = readTimeoutMs > 0 ? readTimeoutMs : 5000;
            _writeTimeoutMs = writeTimeoutMs > 0 ? writeTimeoutMs : 1000;
            _reconnectDelayMs = autoReconnectMs > 0 ? autoReconnectMs : 3000;
        }

        // Виртуальные колбэки
        protected virtual void OnLine(string line) { }
        protected virtual void OnOpened() { }
        protected virtual void OnClosed(Exception ex) { }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_portName))
                throw new ArgumentNullException("PortName", "PortName is empty");

            var key = _portName.ToUpperInvariant();
            var guard = _portGuards.GetOrAdd(key, k => new object());

            lock (guard)
            {
                Stop();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _readerTask = Task.Run(new Action(() => WorkerLoop(ct)), ct);
            }
        }

        public void Stop()
        {
            var sp = _sp;
            _sp = null;

            if (_cts != null) _cts.Cancel();

            if (sp != null)
            {
                try { sp.DataReceived -= OnData; } catch { }
                try { if (sp.IsOpen) sp.Close(); } catch { }
                try { sp.Dispose(); } catch { }
            }

            try { if (_cts != null) _cts.Dispose(); } catch { }
            _cts = null;

            var t = _readerTask;
            _readerTask = null;
            try { if (t != null) t.Wait(2000); } catch { }
        }

        private void WorkerLoop(CancellationToken ct)
        {
            var guard = _portGuards[_portName.ToUpperInvariant()];

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    lock (guard)
                    {
                        OpenPort();
                    }

                    OnOpened();

                    while (!ct.IsCancellationRequested)
                    {
                        Thread.Sleep(50);

                        // idle-flush: выдаём строку, если долго нет новых байт
                        lock (_bufLock)
                        {
                            if (_lineBuf.Length > 0 && (DateTime.UtcNow - _lastByteAt).TotalMilliseconds >= IdleFlushMs)
                            {
                                var s = _lineBuf.ToString();
                                _lineBuf.Clear();
                                SafeDeliver(s);
                            }
                        }
                    }
                } catch (UnauthorizedAccessException ex)
                {
                    OnClosed(ex);
                    Thread.Sleep(_reconnectDelayMs);
                } catch (Exception ex)
                {
                    OnClosed(ex);
                    Thread.Sleep(_reconnectDelayMs);
                }
                finally
                {
                    try
                    {
                        lock (guard)
                        {
                            var sp = _sp;
                            _sp = null;
                            if (sp != null)
                            {
                                try { sp.DataReceived -= OnData; } catch { }
                                try { if (sp.IsOpen) sp.Close(); } catch { }
                                try { sp.Dispose(); } catch { }
                            }
                        }
                    } catch { }
                }
            }
        }

        private void OpenPort()
        {
            // Закрыть предыдущий экземпляр, если был
            var prev = _sp;
            _sp = null;
            if (prev != null)
            {
                try { prev.DataReceived -= OnData; } catch { }
                try { if (prev.IsOpen) prev.Close(); } catch { }
                try { prev.Dispose(); } catch { }
            }

            // Создать и настроить порт
            var sp = new SerialPort(_portName, _baud);
            sp.ReadTimeout = _readTimeoutMs;
            sp.WriteTimeout = _writeTimeoutMs;
            sp.NewLine = _newline;      // строковый разделитель для WriteLine
            sp.Encoding = Encoding.ASCII;

            // === Настройки из App.config (повторяют демо/устройство) ===
            var parityStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidParity"] ?? "None";
            var stopBitsStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidStopBits"] ?? "One";
            var dataBitsStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidDataBits"] ?? "8";
            var handshakeStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidHandshake"] ?? "None";
            var dtrStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidDtr"] ?? "true";
            var rtsStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidRts"] ?? "true";

            sp.Parity = (Parity)Enum.Parse(typeof(Parity), parityStr, true);
            sp.StopBits = (StopBits)Enum.Parse(typeof(StopBits), stopBitsStr, true);
            int db; sp.DataBits = int.TryParse(dataBitsStr, out db) ? db : 8;
            sp.Handshake = (Handshake)Enum.Parse(typeof(Handshake), handshakeStr, true);

            bool dtr; sp.DtrEnable = bool.TryParse(dtrStr, out dtr) ? dtr : true;
            bool rts; sp.RtsEnable = bool.TryParse(rtsStr, out rts) ? rts : true;

            // чтобы событие DataReceived срабатывало на каждом байте
            sp.ReceivedBytesThreshold = 1;
            // === конец блока настроек ===

            // Открыть и очистить буферы
            sp.Open();
            try { sp.DiscardInBuffer(); } catch { }
            try { sp.DiscardOutBuffer(); } catch { }

            // Подписка на чтение
            sp.DataReceived += OnData;

            _sp = sp;
        }

        private void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var spLocal = _sp;
                if (spLocal == null) return;

                var chunk = spLocal.ReadExisting();
                if (string.IsNullOrEmpty(chunk)) return;

                lock (_bufLock)
                {
                    _lastByteAt = DateTime.UtcNow;
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        char c = chunk[i];
                        // завершаем строку по \n или \r (а также по первому символу сконфигурированного NewLine)
                        if (c == '\n' || c == '\r' || (_newline.Length > 0 && c == _newline[0]))
                        {
                            var line = _lineBuf.ToString();
                            _lineBuf.Clear();

                            // сначала внешние хендлеры, потом виртуальный
                            var lr = OnLineReceived; if (lr != null) { try { lr(line); } catch { } }
                            SafeDeliver(line);
                        }
                        else
                        {
                            _lineBuf.Append(c);
                        }
                    }
                }
            } catch (TimeoutException) { } catch (Exception ex)
            {
                try { OnClosed(ex); } catch { }
                Stop();
                Start();
            }
        }

        private void SafeDeliver(string rawLine)
        {
            var line = (rawLine ?? "").Trim();
            if (line.Length == 0) return;
            try { OnLine(line); } catch { }
        }

        public void WriteLine(string text)
        {
            var bw = OnBeforeWrite; if (bw != null) { try { bw(text); } catch { } }
            WriteLineSafe(text);
        }

        // совместимость со старым кодом (например, ArduinoClientSerial.Send(...))
        public void Send(string text)
        {
            WriteLine(text);
        }

        protected void WriteLineSafe(string text)
        {
            var spLocal = _sp;
            if (spLocal == null || !spLocal.IsOpen) return;
            try { spLocal.WriteLine(text ?? ""); } catch { }
        }

        public void Write(string text)
        {
            var spLocal = _sp;
            if (spLocal == null || !spLocal.IsOpen) return;
            try { spLocal.Write(text ?? ""); } catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
