using System;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace LibraryTerminal
{
    // ===== ЧИТАТЕЛЬСКИЕ КАРТЫ (низкочастотный/PCSC-эмуляция по COM) =====
    internal class CardReaderSerial : SerialWorker
    {
        public event Action<string> OnUid;

        private readonly int _debounceMs;
        private string _last;
        private DateTime _lastAt;

        public CardReaderSerial(string port, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int reconnectDelayMs, int debounceMs)
            : base(port, baud, newline, readTimeoutMs, writeTimeoutMs, reconnectDelayMs)
        {
            _debounceMs = debounceMs;
        }

        protected override void OnOpened() { }
        protected override void OnClosed(Exception ex) { }

        protected override void OnLine(string line)
        {
            var uid = line.Trim(); // при необходимости парсить
            var now = DateTime.UtcNow;
            if (_last == uid && (now - _lastAt).TotalMilliseconds < _debounceMs) return;
            _last = uid; _lastAt = now;
            OnUid?.Invoke(uid);
        }
    }

    // ===== КНИЖНЫЕ RFID (через COM) =====
    internal class BookReaderSerial : SerialWorker
    {
        public event Action<string> OnTag;

        private readonly int _debounceMs;
        private string _last;
        private DateTime _lastAt;

        public BookReaderSerial(string port, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int reconnectDelayMs, int debounceMs)
            : base(port, baud, newline, readTimeoutMs, writeTimeoutMs, reconnectDelayMs)
        {
            _debounceMs = debounceMs;
        }

        protected override void OnOpened() { }
        protected override void OnClosed(Exception ex) { }

        protected override void OnLine(string line)
        {
            var tag = line.Trim();
            var now = DateTime.UtcNow;
            if (_last == tag && (now - _lastAt).TotalMilliseconds < _debounceMs) return;
            _last = tag; _lastAt = now;
            OnTag?.Invoke(tag);
        }
    }

    // ===== АРДУИНО-ШКАФ =====
    internal class ArduinoClientSerial : SerialWorker
    {
        private readonly int _syncTimeoutMs;
        private readonly AutoResetEvent _respEvent = new AutoResetEvent(false);
        private readonly object _respLock = new object();
        private string _lastResp;

        public ArduinoClientSerial(string port, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int reconnectDelayMs)
            : base(port, baud, newline, readTimeoutMs, writeTimeoutMs, reconnectDelayMs)
        {
            _syncTimeoutMs = readTimeoutMs > 0 ? readTimeoutMs : 3000;
        }

        protected override void OnOpened()
        {
            SafeLog("OPENED");
        }

        protected override void OnClosed(Exception ex)
        {
            SafeLog("CLOSED" + (ex != null ? $": {ex.Message}" : ""));
        }

        protected override void OnLine(string line)
        {
            SafeLog($"<< {line}");
            lock (_respLock)
            {
                _lastResp = line;
                _respEvent.Set();
            }
        }

        public bool HasSpace()
        {
            var resp = Request("SPACE?", _syncTimeoutMs);
            var t = (resp ?? "").Trim().ToUpperInvariant();
            if (t.StartsWith("SPACE")) t = t.Substring(5).TrimStart(' ', ':', '=');
            return t.StartsWith("1");
        }

        public void OpenBin()
        {
            var resp = Request("OPENBIN", _syncTimeoutMs + 10000); // +10s на механику
            if (!"OK".Equals(resp?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cabinet responded: " + (resp ?? "<empty>"));
        }

        // ---- helpers ----
        private string Request(string cmd, int timeoutMs)
        {
            SafeLog($">> {cmd}");
            this.WriteLineSafe(cmd); // безопасно отправляем строку с NewLine (extension)
            if (!_respEvent.WaitOne(timeoutMs))
                throw new TimeoutException("No response for " + cmd);

            lock (_respLock)
            {
                var r = _lastResp;
                _lastResp = null;
                return r;
            }
        }

        private static void SafeLog(string msg)
        {
            try
            {
                Logger.Append("arduino.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
            } catch { }
        }
    }
}
