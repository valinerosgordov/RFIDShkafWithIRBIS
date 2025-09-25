using System;

namespace LibraryTerminal
{
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

        protected override void OnOpened()
        { }

        protected override void OnClosed(Exception ex)
        { }

        protected override void OnLine(string line)
        {
            var uid = line; // при необходимости распарси здесь

            var now = DateTime.UtcNow;
            if (_last == uid && (now - _lastAt).TotalMilliseconds < _debounceMs)
                return;

            _last = uid;
            _lastAt = now;
            OnUid?.Invoke(uid);
        }
    }

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
            var tag = line; // при необходимости распарси здесь

            var now = DateTime.UtcNow;
            if (_last == tag && (now - _lastAt).TotalMilliseconds < _debounceMs)
                return;

            _last = tag;
            _lastAt = now;
            OnTag?.Invoke(tag);
        }
    }

    internal class ArduinoClientSerial : SerialWorker
    {
        public ArduinoClientSerial(string port, int baud, string newline, int readTimeoutMs, int writeTimeoutMs, int reconnectDelayMs)
            : base(port, baud, newline, readTimeoutMs, writeTimeoutMs, reconnectDelayMs)
        { }

        protected override void OnOpened()
        { }

        protected override void OnClosed(Exception ex)
        { }

        protected override void OnLine(string line)
        {
            // обработка ответов Arduino при желании
        }

        public bool HasSpace()
        {
            WriteLineSafe("SPACE?");
            return true; // при желании дописать синхронное ожидание OK
        }

        public void OpenBin() => WriteLineSafe("OPEN");
    }
}