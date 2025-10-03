using System.Configuration;

namespace LibraryTerminal
{
    public sealed class ArduinoSerial : IArduino
    {
        private readonly SerialWorker _sw;
        private readonly string _port;
        private readonly int _baud;
        private readonly string _nl;

        public bool IsConnected => _sw?.IsOpen == true;

        public ArduinoSerial(string port, int baud, string newline)
        {
            _port = port;
            _baud = baud;
            _nl = string.IsNullOrEmpty(newline) ? "\n" : newline;

            _sw = new SerialWorker(port, baud, readTimeoutMs: 1000, writeTimeoutMs: 1000,
                                   autoReconnectMs: 1500, newline: _nl);

            _sw.OnBeforeWrite += s => Logger.LogArduino($"[SERIAL:{_port}@{_baud}] TX: {San(s)}");
            _sw.OnLineReceived += s => Logger.LogArduino($"[SERIAL:{_port}@{_baud}] RX: {San(s)}");

            _sw.Start();
            Logger.LogArduino($"[SERIAL:{_port}@{_baud}] START");
        }

        public void Send(string cmd)
        {
            var line = cmd.EndsWith(_nl) ? cmd : (cmd + _nl);
            _sw.WriteLine(line);
        }

        public void Ok() => Send("OK");
        public void Error() => Send("ERR");
        public void Beep(int ms = 120) => Send($"BEEP:{ms}");

        public void Dispose()
        {
            try { _sw?.Dispose(); }
            finally { Logger.LogArduino($"[SERIAL:{_port}@{_baud}] STOP"); }
        }

        private static string San(string s) => s?.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
