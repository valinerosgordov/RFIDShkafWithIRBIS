using System.Threading.Tasks;
using LibraryTerminal.Core;
using ManagedClient;

namespace LibraryTerminal
{
    /// <summary>
    /// Интерфейс для работы с библиотечной информационной системой ИРБИС64
    /// </summary>
    public interface IIrbisService
    {
        /// <summary>
        /// MFN последнего найденного читателя
        /// </summary>
        int LastReaderMfn { get; }

        /// <summary>
        /// Подключиться к серверу ИРБИС
        /// </summary>
        void Connect(string connectionString);

        /// <summary>
        /// Использовать указанную базу данных
        /// </summary>
        void UseDatabase(string databaseName);

        /// <summary>
        /// Проверить карту читателя по UID
        /// </summary>
        /// <returns>True если читатель найден</returns>
        bool ValidateCard(string uid);

        /// <summary>
        /// Найти книгу по RFID-метке
        /// </summary>
        Option<IrbisRecord> FindOneByBookRfid(string rfid);

        /// <summary>
        /// Выдать книгу по RFID
        /// </summary>
        /// <returns>Краткое описание книги</returns>
        Result<string> IssueByRfid(string bookRfid);

        /// <summary>
        /// Вернуть книгу по RFID
        /// </summary>
        /// <returns>Краткое описание книги</returns>
        Result<string> ReturnByRfid(string bookRfid);
    }
}
