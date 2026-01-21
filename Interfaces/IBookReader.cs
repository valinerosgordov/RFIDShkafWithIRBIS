using System;

namespace LibraryTerminal
{
    /// <summary>
    /// Интерфейс для RFID-ридеров книг
    /// </summary>
    public interface IBookReader : IDisposable
    {
        /// <summary>
        /// Событие чтения RFID-метки книги
        /// </summary>
        event EventHandler<string> TagRead;

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
