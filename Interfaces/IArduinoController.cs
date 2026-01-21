using System.Threading.Tasks;

namespace LibraryTerminal
{
    /// <summary>
    /// Интерфейс для управления Arduino-шкафом
    /// </summary>
    public interface IArduinoController
    {
        /// <summary>
        /// Проверка подключения
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Проверить наличие свободного места в шкафу
        /// </summary>
        Task<bool> HasSpaceAsync();

        /// <summary>
        /// Открыть ячейку шкафа
        /// </summary>
        Task OpenBinAsync();

        /// <summary>
        /// Проверить наличие свободного места (синхронная версия)
        /// </summary>
        bool HasSpace();

        /// <summary>
        /// Отправить команду Arduino
        /// </summary>
        void SendCommand(string command);

        /// <summary>
        /// Отправить команду OK
        /// </summary>
        void SendOk();

        /// <summary>
        /// Отправить команду ERR
        /// </summary>
        void SendError();

        /// <summary>
        /// Отправить команду BEEP
        /// </summary>
        void SendBeep(int milliseconds = 120);
    }
}
