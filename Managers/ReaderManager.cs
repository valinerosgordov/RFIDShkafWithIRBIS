using System;
using System.Collections.Generic;
using System.Linq;

namespace LibraryTerminal
{
    /// <summary>
    /// Менеджер для управления всеми RFID-ридерами
    /// </summary>
    public class ReaderManager : IDisposable
    {
        private readonly List<IBookReader> _bookReaders;
        private readonly List<ICardReader> _cardReaders;

        public ReaderManager()
        {
            _bookReaders = new List<IBookReader>();
            _cardReaders = new List<ICardReader>();
        }

        /// <summary>
        /// Добавить ридер книг
        /// </summary>
        public void AddBookReader(IBookReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            _bookReaders.Add(reader);
        }

        /// <summary>
        /// Добавить ридер карт
        /// </summary>
        public void AddCardReader(ICardReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            _cardReaders.Add(reader);
        }

        /// <summary>
        /// Запустить все ридеры
        /// </summary>
        public void StartAll()
        {
            foreach (var reader in _bookReaders.Concat(_cardReaders.Cast<IDisposable>()))
            {
                try
                {
                    if (reader is IBookReader bookReader)
                        bookReader.Start();
                    else if (reader is ICardReader cardReader)
                        cardReader.Start();
                }
                catch (Exception ex)
                {
                    Logger.Append("reader_manager.log", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to start reader: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Остановить все ридеры
        /// </summary>
        public void StopAll()
        {
            foreach (var reader in _bookReaders.Concat(_cardReaders.Cast<IDisposable>()))
            {
                try
                {
                    reader?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Append("reader_manager.log", 
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error disposing reader: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            StopAll();
            _bookReaders.Clear();
            _cardReaders.Clear();
        }
    }
}
