using System;
using System.Threading;
using System.Text.RegularExpressions;
using System.Configuration;

namespace LibraryTerminal
{
    // ===== ЧИТАТЕЛЬСКИЕ КАРТЫ (низкочастотный/PCSC-эмуляция по COM) =====
    internal class CardReaderSerial : SerialWorker
    {
        public event Action<string> OnUid;

        private readonly int _debounceMs;
        private string _last;
        private DateTime _lastAt;

        public CardReaderSerial(
            string port,
            int baud,
            string newline,
            int readTimeoutMs,
            int writeTimeoutMs,
            int autoReconnectMs,
            int debounceMs)
            : base(port, baud, newline, readTimeoutMs, writeTimeoutMs, autoReconnectMs)
        {
            _debounceMs = debounceMs > 0 ? debounceMs : 250;
        }

        // === Инициализация ридера после открытия порта (по желанию из App.config) ===
        protected override void OnOpened()
        {
            // отправим иниц. команду, если задана
            var initCmd = System.Configuration.ConfigurationManager.AppSettings["IqrfidInitCmd"];
            int initDelayMs;
            if (!string.IsNullOrWhiteSpace(initCmd))
            {
                var beforeStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidInitDelayBeforeMs"] ?? "100";
                var afterStr = System.Configuration.ConfigurationManager.AppSettings["IqrfidInitDelayAfterMs"] ?? "100";
                if (int.TryParse(beforeStr, out initDelayMs) && initDelayMs > 0) Thread.Sleep(initDelayMs);

                // отправляем как строку (уйдёт с NewLine SerialPort'а)
                try { WriteLineSafe(initCmd); } catch { }

                if (int.TryParse(afterStr, out initDelayMs) && initDelayMs > 0) Thread.Sleep(initDelayMs);
            }

            SafeLog("IQRFID OPENED");
        }

        protected override void OnClosed(Exception ex)
        {
            SafeLog("IQRFID CLOSED" + (ex != null ? (": " + ex.Message) : ""));
        }

        // === Пришло что-то из COM — парсим UID читательского билета ===
        protected override void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // схлопываем разделители, если ридер присылает "04 A1-B2:C3"
            var collapsed = System.Text.RegularExpressions.Regex.Replace(line, @"[\s:\-]", "");

            // минимальная длина UID из конфига, по умолчанию 6
            int minHexLen = 6;
            int.TryParse(System.Configuration.ConfigurationManager.AppSettings["MinUidHexLen"] ?? "6", out minHexLen);

            string uid = null;
            // если вся строка — hex (минимум minHexLen) — берём её, иначе вытащим первую hex-подстроку
            if (collapsed.Length >= minHexLen && IsHex(collapsed))
            {
                uid = collapsed;
            }
            else
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(?i)\b([0-9A-F]{" + minHexLen + @",})\b");
                if (m.Success) uid = m.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(uid)) return;

            // нормализация под конфиг
            bool strip = "true".Equals(System.Configuration.ConfigurationManager.AppSettings["UidStripDelimiters"] ?? "true",
                                       StringComparison.OrdinalIgnoreCase);
            if (strip) uid = System.Text.RegularExpressions.Regex.Replace(uid, @"[\s:\-]", "");

            uid = System.Text.RegularExpressions.Regex.Replace(uid, @"[^0-9A-Fa-f]", "");

            bool upper = "true".Equals(System.Configuration.ConfigurationManager.AppSettings["UidUpperHex"] ?? "true",
                                       StringComparison.OrdinalIgnoreCase);
            if (upper) uid = uid.ToUpperInvariant();

            // антидребезг
            var now = DateTime.UtcNow;
            if (_last == uid && (now - _lastAt).TotalMilliseconds < _debounceMs) return;
            _last = uid; _lastAt = now;

            try { OnUid?.Invoke(uid); } catch { }
            SafeLog("UID: " + uid);

            // локальная проверка
            bool IsHex(string s)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        return false;
                }
                return true;
            }
        }

        private static void SafeLog(string msg)
        {
            try
            {
                Logger.Append("iqrfid.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
            } catch { }
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

        protected override void OnOpened()
        { }

        protected override void OnClosed(Exception ex)
        { }

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
