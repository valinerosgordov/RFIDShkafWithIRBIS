using System;
using System.IO.Ports;
using System.Threading;

namespace LibraryTerminal
{
    internal abstract class SerialWorker : IDisposable
    {
        protected readonly string _portName;
        protected readonly int _baud;
        protected readonly string _newline;
        protected readonly int _readTimeout;
        protected readonly int _writeTimeout;
        protected readonly int _reconnectDelayMs;

        protected SerialPort _sp;
        private Thread _reader;
        private volatile bool _stop;
        private readonly object _sync = new object();

        protected SerialWorker(string portName, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int reconnectDelayMs)
        {
            _portName = portName;
            _baud = baud;
            _newline = DecodeNewline(newline);
            _readTimeout = readTimeoutMs;
            _writeTimeout = writeTimeoutMs;
            _reconnectDelayMs = reconnectDelayMs;
        }

        public void Start()
        {
            _stop = false;
            _reader = new Thread(Loop) { IsBackground = true, Name = GetType().Name + "_Reader" };
            _reader.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _sp?.Close(); } catch { }
            try { _reader?.Join(1000); } catch { }
        }

        public void Dispose()
        {
            Stop();
            try { _sp?.Dispose(); } catch { }
        }

        protected virtual void OnOpened()
        { }

        protected virtual void OnClosed(Exception ex)
        { }

        protected abstract void OnLine(string line);

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    EnsureOpen();
                    ReadLines();
                } catch (Exception ex)
                {
                    OnClosed(ex);
                    try { _sp?.Close(); } catch { }
                    if (_stop) break;
                    Thread.Sleep(_reconnectDelayMs);
                }
            }
        }

        private void EnsureOpen()
        {
            if (_sp != null && _sp.IsOpen) return;

            lock (_sync)
            {
                if (_sp != null && _sp.IsOpen) return;

                _sp = new SerialPort(_portName, _baud)
                {
                    NewLine = _newline,
                    ReadTimeout = _readTimeout,
                    WriteTimeout = _writeTimeout,
                    Encoding = System.Text.Encoding.ASCII,
                    DtrEnable = true,
                    RtsEnable = true
                };
                _sp.Open();
                OnOpened();
            }
        }

        private void ReadLines()
        {
            while (!_stop && _sp != null && _sp.IsOpen)
            {
                string line = null;
                try
                {
                    line = _sp.ReadLine();
                } catch (TimeoutException)
                {
                    continue;
                }
                if (line == null) continue;
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                try { OnLine(trimmed); } catch { }
            }
        }

        public void WriteLineSafe(string cmd)
        {
            lock (_sync)
            {
                if (_sp == null || !_sp.IsOpen) return;
                try { _sp.WriteLine(cmd); } catch { }
            }
        }

        private static string DecodeNewline(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\n";
            return s.Replace("\\r", "\r").Replace("\\n", "\n");
        }
    }
}