using System;
using System.Linq;
using System.Threading.Tasks;
using ManagedClient;

namespace LibraryTerminal
{
    /// <summary>
    /// Сервис для выполнения операций с книгами (выдача/возврат)
    /// </summary>
    public class BookOperationService
    {
        private readonly IIrbisService _irbisService;
        private readonly IArduinoController _arduinoController;

        public BookOperationService(IIrbisService irbisService, IArduinoController arduinoController)
        {
            _irbisService = irbisService ?? throw new ArgumentNullException(nameof(irbisService));
            _arduinoController = arduinoController ?? throw new ArgumentNullException(nameof(arduinoController));
        }

        /// <summary>
        /// Выдать книгу
        /// </summary>
        public async Task<OperationResult> TakeBookAsync(string bookRfid, int readerMfn)
        {
            if (string.IsNullOrWhiteSpace(bookRfid))
                return OperationResult.Fail("RFID метка книги не может быть пустой");

            if (readerMfn <= 0)
                return OperationResult.Fail("Не указан читатель");

            try
            {
                var book = _irbisService.FindOneByBookRfid(bookRfid);
                if (book == null)
                    return OperationResult.Fail("Книга не найдена по метке");

                // Проверка статуса книги
                var statusField = GetStatusField(book, bookRfid);
                if (statusField == null)
                    return OperationResult.Fail("Эта метка не соответствует экземпляру");

                var status = statusField.GetFirstSubFieldText('a') ?? string.Empty;
                if (status != "0")
                    return OperationResult.Fail("Эта книга уже выдана");

                // Выдача книги
                var brief = _irbisService.IssueByRfid(bookRfid);
                if (string.IsNullOrWhiteSpace(brief))
                    return OperationResult.Fail("Не удалось записать выдачу в ИРБИС");

                // Открыть шкаф
                await _arduinoController.OpenBinAsync();
                _arduinoController.SendOk();
                _arduinoController.SendBeep(120);

                return OperationResult.Success(brief, book.Mfn);
            }
            catch (Exception ex)
            {
                _arduinoController.SendError();
                return OperationResult.Fail($"Ошибка выдачи: {ex.Message}");
            }
        }

        /// <summary>
        /// Вернуть книгу
        /// </summary>
        public async Task<OperationResult> ReturnBookAsync(string bookRfid)
        {
            if (string.IsNullOrWhiteSpace(bookRfid))
                return OperationResult.Fail("RFID метка книги не может быть пустой");

            try
            {
                var book = _irbisService.FindOneByBookRfid(bookRfid);
                if (book == null)
                    return OperationResult.Fail("Книга не найдена по метке");

                // Проверка места в шкафу
                var hasSpace = await _arduinoController.HasSpaceAsync();
                if (!hasSpace)
                    return OperationResult.Fail("Нет свободного места в шкафу");

                // Возврат книги
                var brief = _irbisService.ReturnByRfid(bookRfid);
                if (string.IsNullOrWhiteSpace(brief))
                    return OperationResult.Fail("Не удалось записать возврат в ИРБИС");

                // Открыть шкаф
                await _arduinoController.OpenBinAsync();
                _arduinoController.SendOk();
                _arduinoController.SendBeep(120);

                return OperationResult.Success(brief, book.Mfn);
            }
            catch (Exception ex)
            {
                _arduinoController.SendError();
                return OperationResult.Fail($"Ошибка возврата: {ex.Message}");
            }
        }

        private RecordField GetStatusField(IrbisRecord record, string scannedRfid)
        {
            var normalizedScanned = IrbisServiceManaged.NormalizeId(scannedRfid);
            if (string.IsNullOrWhiteSpace(normalizedScanned))
                return null;

            foreach (var field in record.Fields)
            {
                if (field.Tag != "910")
                    continue;

                var hValue = IrbisServiceManaged.NormalizeId(field.GetFirstSubFieldText('h'));
                if (string.IsNullOrWhiteSpace(hValue))
                    continue;

                if (hValue == normalizedScanned || 
                    normalizedScanned.EndsWith(hValue) || 
                    hValue.EndsWith(normalizedScanned))
                {
                    return field;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Результат операции
    /// </summary>
    public class OperationResult
    {
        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; }
        public string BookBrief { get; private set; }
        public int BookMfn { get; private set; }

        private OperationResult(bool success, string message, string brief = "", int mfn = 0)
        {
            Success = success;
            ErrorMessage = message;
            BookBrief = brief;
            BookMfn = mfn;
        }

        public static OperationResult Success(string brief, int mfn)
        {
            return new OperationResult(true, "", brief, mfn);
        }

        public static OperationResult Fail(string errorMessage)
        {
            return new OperationResult(false, errorMessage);
        }
    }
}
