using System;

namespace LibraryTerminal
{
    /// <summary>
    /// Интерфейс для RFID-ридеров читательских карт
    /// </summary>
    public interface ICardReader : IDisposable
    {
        /// <summary>
        /// Событие чтения UID карты
        /// </summary>
        event EventHandler<string> CardRead;

        /// <summary>
        /// Запустить ридер
        /// </summary>
        void Start();

        /// <summary>
        /// Остановить ридер
        /// </summary>
        void Stop();
    }
}
