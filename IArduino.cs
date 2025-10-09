namespace LibraryTerminal
{
    public interface IArduino : System.IDisposable
    {
        bool IsConnected { get; }

        void Send(string cmd);

        void Ok();        // успех

        void Error();     // ошибка

        void Beep(int ms = 120);
    }
}