namespace LibraryTerminal
{
    public sealed class ArduinoNull : IArduino
    {
        public bool IsConnected => false;

        public void Send(string cmd)
        {
            Logger.LogArduino($"[NULL] TX: {cmd}");
        }

        public void Ok() => Send("OK");
        public void Error() => Send("ERR");
        public void Beep(int ms = 120) => Send($"BEEP:{ms}");
        public void Dispose() { }
    }
}
