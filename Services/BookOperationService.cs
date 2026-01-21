using System;
using System.Linq;
using System.Threading.Tasks;
using LibraryTerminal.Core;
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
                var bookOpt = _irbisService.FindOneByBookRfid(bookRfid);
                if (!bookOpt.HasValue)
                    return OperationResult.Fail("Книга не найдена по метке");

                var book = bookOpt.Value;

                // Проверка статуса книги
                var statusFieldOpt = GetStatusField(book, bookRfid);
                if (!statusFieldOpt.HasValue)
                    return OperationResult.Fail("Эта метка не соответствует экземпляру");

                var statusField = statusFieldOpt.Value;
                var status = statusField.GetFirstSubFieldText('a') ?? string.Empty;
                if (status != "0")
                    return OperationResult.Fail("Эта книга уже выдана");

                // Выдача книги
                var issueResult = _irbisService.IssueByRfid(bookRfid);
                if (issueResult.IsFailure)
                    return OperationResult.Fail(issueResult.Error);

                var brief = issueResult.Value;

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
                var bookOpt = _irbisService.FindOneByBookRfid(bookRfid);
                if (!bookOpt.HasValue)
                    return OperationResult.Fail("Книга не найдена по метке");

                var book = bookOpt.Value;

                // Проверка места в шкафу
                var hasSpace = await _arduinoController.HasSpaceAsync();
                if (!hasSpace)
                    return OperationResult.Fail("Нет свободного места в шкафу");

                // Возврат книги
                var returnResult = _irbisService.ReturnByRfid(bookRfid);
                if (returnResult.IsFailure)
                    return OperationResult.Fail(returnResult.Error);

                var brief = returnResult.Value;

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

        private Option<RecordField> GetStatusField(IrbisRecord record, string scannedRfid)
        {
            var normalizedScannedOpt = IrbisServiceManaged.NormalizeId(scannedRfid);
            if (!normalizedScannedOpt.HasValue)
                return Option<RecordField>.None;

            var normalizedScanned = normalizedScannedOpt.Value;

            foreach (var field in record.Fields)
            {
                if (field.Tag != "910")
                    continue;

                var hValueOpt = IrbisServiceManaged.NormalizeId(field.GetFirstSubFieldText('h'));
                if (!hValueOpt.HasValue)
                    continue;

                var hValue = hValueOpt.Value;
                if (hValue == normalizedScanned || 
                    normalizedScanned.EndsWith(hValue) || 
                    hValue.EndsWith(normalizedScanned))
                {
                    return Option<RecordField>.Some(field);
                }
            }

            return Option<RecordField>.None;
        }
    }

    /// <summary>
    /// Результат операции
    /// </summary>
    public record OperationResult(
        bool Success,
        string ErrorMessage,
        string BookBrief,
        int BookMfn
    )
    {
        public static OperationResult Success(string brief, int mfn) =>
            new OperationResult(true, "", brief, mfn);

        public static OperationResult Fail(string errorMessage) =>
            new OperationResult(false, errorMessage, "", 0);
    }
}
