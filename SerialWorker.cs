using System;
using System.IO.Ports;
using System.Threading;

namespace LibraryTerminal
{
    /// <summary>
    /// Базовая неблокирующая обёртка над SerialPort с авто-переподключением.
    /// От неё наследуются ArduinoClientSerial / BookReaderSerial / CardReaderSerial.
    /// </summary>
    public class SerialWorker : IDisposable
    {
        private SerialPort _sp;
        private Thread _reader;
        private volatile bool _running;
        private volatile bool _openedOnce;

        protected readonly string PortName;
        protected readonly int BaudRate;
        protected readonly int ReadTimeoutMs;
        protected readonly int WriteTimeoutMs;
        protected readonly int ReconnectMs;

        /// <summary>Разделитель строк при чтении/записи.</summary>
        public string NewLine { get; protected set; }

        /// <summary>Вызывается перед отправкой строки (для логгирования TX).</summary>
        public event Action<string> OnBeforeWrite;

        /// <summary>Событие с уже разобранной строкой RX. Альтернатива виртуальному OnLine.</summary>
        public event Action<string> OnLineEvent;

        /// <summary>Событие-синоним для совместимости со старым кодом.</summary>
        public event Action<string> OnLineReceived;

        public bool IsOpen => _sp != null && _sp.IsOpen;

        /// <summary>
        /// Конструктор базового воркера.
        /// </summary>
        /// <param name="portName">COM-порт (например, "COM3").</param>
        /// <param name="baudRate">Скорость, бит/с.</param>
        /// <param name="newline">Разделитель строк.</param>
        /// <param name="readTimeoutMs">Таймаут чтения, мс.</param>
        /// <param name="writeTimeoutMs">Таймаут записи, мс.</param>
        /// <param name="autoReconnectMs">Пауза перед переподключением, мс.</param>
        public SerialWorker(string portName, int baudRate, string newline, int readTimeoutMs, int writeTimeoutMs, int autoReconnectMs)
        {
            PortName = portName;
            BaudRate = baudRate;
            ReadTimeoutMs = readTimeoutMs <= 0 ? 500 : readTimeoutMs;
            WriteTimeoutMs = writeTimeoutMs <= 0 ? 500 : writeTimeoutMs;
            ReconnectMs = autoReconnectMs <= 0 ? 1500 : autoReconnectMs;
            NewLine = string.IsNullOrEmpty(newline) ? "\n" : newline;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = $"SerialWorker({PortName})" };
            _reader.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _sp?.Close(); } catch { }
        }

        public virtual void Dispose()
        {
            _running = false;
            try { _sp?.Close(); } catch { }
            try { _sp?.Dispose(); } catch { }
            _sp = null;
            _reader = null;
        }

        /// <summary>Отправка текста как есть (без добавления NewLine).</summary>
        public virtual void Write(string text)
        {
            try
            {
                if (!_running) return;
                EnsurePort();
                OnBeforeWrite?.Invoke(text);
                _sp.Write(text);
            } catch
            {
                // глушим — цикл чтения переподключит порт
            }
        }

        /// <summary>Отправка строки с добавлением NewLine.</summary>
        public virtual void WriteLine(string line) => Write(line + NewLine);

        /// <summary>Синоним для совместимости.</summary>
        public virtual void Send(string line) => WriteLine(line);

        // ====== protected virtual hooks (для наследников) ======
        protected virtual void OnOpened() { }
        protected virtual void OnClosed(Exception ex) { }
        protected virtual void OnLine(string line) { }

        // ====== внутренняя машинерия ======
        private void ReaderLoop()
        {
            while (_running)
            {
                try
                {
                    EnsurePort();

                    if (!_openedOnce && IsOpen)
                    {
                        _openedOnce = true;
                        SafeCallOpened();
                    }

                    var nl = NewLine;
                    var buf = "";

                    while (_running && IsOpen)
                    {
                        var ch = (char)_sp.ReadChar();
                        buf += ch;
                        if (buf.EndsWith(nl))
                        {
                            var line = buf.Substring(0, buf.Length - nl.Length);
                            buf = "";
                            SafeCallLine(line);
                        }
                    }
                } catch (Exception ex)
                {
                    try { _sp?.Close(); } catch { }
                    SafeCallClosed(ex);
                    if (!_running) break;
                    Thread.Sleep(ReconnectMs);
                    _openedOnce = false;
                }
            }
        }

        private void EnsurePort()
        {
            if (_sp != null && _sp.IsOpen) return;

            try { _sp?.Dispose(); } catch { }

            _sp = new SerialPort(PortName, BaudRate)
            {
                NewLine = this.NewLine,
                ReadTimeout = ReadTimeoutMs,
                WriteTimeout = WriteTimeoutMs,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                DtrEnable = false,
                RtsEnable = false
            };
            _sp.Open();
        }

        private void SafeCallOpened()
        {
            try { OnOpened(); } catch { }
        }

        private void SafeCallClosed(Exception ex)
        {
            try { OnClosed(ex); } catch { }
        }

        private void SafeCallLine(string line)
        {
            try
            {
                OnLine(line);
                OnLineEvent?.Invoke(line);
                OnLineReceived?.Invoke(line);
            } catch { }
        }
    }
}
