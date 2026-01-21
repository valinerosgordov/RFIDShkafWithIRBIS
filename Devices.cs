using System;
using System.Threading;
using System.Text.RegularExpressions;
using System.Configuration;

namespace LibraryTerminal
{
    // ===== ЧИТАТЕЛЬСКИЕ КАРТЫ (низкочастотный/PCSC-эмуляция по COM) кря кря кря =====
    internal class CardReaderSerial : SerialWorker, ICardReader
    {
        public event EventHandler<string> CardRead;
        
        [Obsolete("Use CardRead event instead")]
        public event Action<string> OnUid
        {
            add { CardRead += (s, uid) => value(uid); }
            remove { /* Not supported */ }
        }

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

            var handler = CardRead;
            if (handler != null)
            {
                try { handler(this, uid); }
                catch (Exception ex)
                {
                    try { Logger.Append("iqrfid.log", $"OnUid handler exception: {ex.Message}"); } catch { }
                }
            }
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
    internal class BookReaderSerial : SerialWorker, IBookReader
    {
        public event EventHandler<string> TagRead;
        
        [Obsolete("Use TagRead event instead")]
        public event Action<string> OnTag
        {
            add { TagRead += (s, tag) => value(tag); }
            remove { /* Not supported */ }
        }

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
            
            var handler = TagRead;
            if (handler != null)
            {
                try { handler(this, tag); }
                catch (Exception ex)
                {
                    try { Logger.Append("book_reader.log", $"OnTag handler exception: {ex.Message}"); } catch { }
                }
            }
        }
    }

    // ===== АРДУИНО-ШКАФ =====
    internal class ArduinoClientSerial : SerialWorker, IArduino, IArduinoController
    {
        private readonly int _syncTimeoutMs;
        private readonly AutoResetEvent _respEvent = new AutoResetEvent(false);
        private readonly object _respLock = new object();
        private string _lastResp;

        public bool IsConnected => IsOpen;

        public ArduinoClientSerial(string port, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int reconnectDelayMs)
            : base(port, baud, newline, readTimeoutMs, writeTimeoutMs, reconnectDelayMs)
        {
            _syncTimeoutMs = readTimeoutMs > 0 ? readTimeoutMs : 3000;
        }

        /// <summary>
        /// Настройка порта для Arduino: отключаем DTR/RTS, чтобы не перезагружать плату.
        /// </summary>
        protected override void ConfigurePort(System.IO.Ports.SerialPort port)
        {
            // Читаем настройки из конфига для Arduino
            var dtrStr = System.Configuration.ConfigurationManager.AppSettings["ArduinoDtr"] ?? "false";
            var rtsStr = System.Configuration.ConfigurationManager.AppSettings["ArduinoRts"] ?? "false";
            
            bool dtr; port.DtrEnable = bool.TryParse(dtrStr, out dtr) ? dtr : false;
            bool rts; port.RtsEnable = bool.TryParse(rtsStr, out rts) ? rts : false;
            
            SafeLog($"CONFIG: DTR={port.DtrEnable}, RTS={port.RtsEnable}");
        }

        /// <summary>
        /// Задержка после открытия порта - даёт Arduino время инициализироваться.
        /// </summary>
        protected override int GetOpenDelayMs()
        {
            var delayStr = System.Configuration.ConfigurationManager.AppSettings["ArduinoOpenDelayMs"] ?? "2000";
            return int.TryParse(delayStr, out var delay) ? delay : 2000;
        }

        protected override void OnOpened()
        {
            SafeLog("OPENED (Arduino ready)");
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

        // === Синхронные команды (с ожиданием ответа) ===
        public async Task<bool> HasSpaceAsync()
        {
            return await Task.Run(() => HasSpace());
        }

        public bool HasSpace()
        {
            if (!IsOpen)
            {
                SafeLog("HAS_SPACE: port not open, returning false");
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
                SafeLog($"HAS_SPACE exception: {ex.Message}");
                return false;
            }
        }

        public async Task OpenBinAsync()
        {
            await Task.Run(() => OpenBin());
        }

        public void OpenBin()
        {
            if (!IsOpen)
            {
                SafeLog("OPENBIN: port not open");
                throw new InvalidOperationException("Serial port is not open");
            }

            var resp = Request("OPENBIN", _syncTimeoutMs + 10000); // +10s на механику
            if (!"OK".Equals(resp?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cabinet responded: " + (resp ?? "<empty>"));
        }

        // === Реализация IArduinoController ===
        public void SendCommand(string cmd) => Send(cmd);

        // === Асинхронные команды (без ожидания ответа) - реализация IArduino ===
        public new void Send(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            SafeLog($">> {cmd} (async)");
            WriteLineSafe(cmd);
            
            // Задержка после отправки текстовой команды
            int cmdDelay = GetCommandDelayMs();
            if (cmdDelay > 0) System.Threading.Thread.Sleep(cmdDelay);
        }

        public void Ok() => Send("OK");
        public void SendOk() => Send("OK");

        public void Error() => Send("ERR");
        public void SendError() => Send("ERR");

        public void Beep(int ms = 120) => Send($"BEEP:{ms}");
        public void SendBeep(int milliseconds = 120) => Send($"BEEP:{milliseconds}");

        // === Бинарные команды управления шкафом (формат из control.pb) ===
        // Пакет: FF, CMD, fromX, fromY, toX, toY, sizeX, sizeY (8 байт)

        /// <summary>
        /// Базовый метод отправки бинарного пакета управления шкафом.
        /// Формат: FF, CMD, fromX, fromY, toX, toY, sizeX, sizeY (8 байт).
        /// </summary>
        private void SendControlPacket(byte command, byte fromX, byte fromY, byte toX, byte toY, byte sizeX, byte sizeY)
        {
            var buf = new byte[8];
            buf[0] = 0xFF;  // Reset byte
            buf[1] = command;
            buf[2] = fromX;
            buf[3] = fromY;
            buf[4] = toX;
            buf[5] = toY;
            buf[6] = sizeX;
            buf[7] = sizeY;

            string cmdName = GetCommandName(command);
            SafeLog($">> BIN {cmdName}: FF {command:X2} from({fromX},{fromY}) to({toX},{toY}) size({sizeX},{sizeY})");
            WriteRaw(buf, 0, buf.Length);
            
            // Задержка после отправки команды - предотвращает переполнение буфера Arduino
            int cmdDelay = GetCommandDelayMs();
            if (cmdDelay > 0) System.Threading.Thread.Sleep(cmdDelay);
        }
        
        private int GetCommandDelayMs()
        {
            var delayStr = System.Configuration.ConfigurationManager.AppSettings["ArduinoCommandDelayMs"] ?? "50";
            return int.TryParse(delayStr, out var delay) ? delay : 50;
        }

        private static string GetCommandName(byte cmd)
        {
            switch (cmd)
            {
                case 0x00: return "INIT";
                case 0x02: return "GIVEFRONT";
                case 0x03: return "TAKEFRONT";
                case 0x04: return "GIVEBACK";
                case 0x05: return "TAKEBACK";
                default: return $"CMD_{cmd:X2}";
            }
        }

        /// <summary>Инициализация шкафа (команда 0x00).</summary>
        public void Init(byte fromX, byte fromY, byte toX, byte toY, byte sizeX, byte sizeY)
        {
            SendControlPacket(0x00, fromX, fromY, toX, toY, sizeX, sizeY);
        }

        /// <summary>Выдача книги на переднюю сторону (команда 0x02).</summary>
        public void GiveFront(byte fromX, byte fromY, byte toX, byte toY, byte sizeX, byte sizeY)
        {
            SendControlPacket(0x02, fromX, fromY, toX, toY, sizeX, sizeY);
        }

        /// <summary>Взятие книги с передней стороны (команда 0x03).</summary>
        public void TakeFront(byte fromX, byte fromY, byte toX, byte toY, byte sizeX, byte sizeY)
        {
            SendControlPacket(0x03, fromX, fromY, toX, toY, sizeX, sizeY);
        }

        /// <summary>Выдача книги на заднюю сторону (команда 0x04).</summary>
        public void GiveBack(byte fromX, byte fromY, byte toX, byte toY, byte sizeX, byte sizeY)
        {
            SendControlPacket(0x04, fromX, fromY, toX, toY, sizeX, sizeY);
        }

        /// <summary>Взятие книги с задней стороны (команда 0x05).</summary>
        public void TakeBack(byte fromX, byte fromY, byte toX, byte toY, byte sizeX, byte sizeY)
        {
            SendControlPacket(0x05, fromX, fromY, toX, toY, sizeX, sizeY);
        }

        // ---- helpers ----
        private string Request(string cmd, int timeoutMs)
        {
            SafeLog($">> {cmd} (sync)");
            WriteLineSafe(cmd);
            
            // Задержка после отправки команды
            int cmdDelay = GetCommandDelayMs();
            if (cmdDelay > 0) System.Threading.Thread.Sleep(cmdDelay);
            
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
