using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LibraryTerminal
{
    internal static class NativePath
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static void PinOutputDir()
        {
            SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }
    }

    internal static class PlatformGuard
    {
        public static void EnsureX86OrThrow()
        {
            if (Environment.Is64BitProcess)
            {
                throw new BadImageFormatException(
                    "Приложение запущено как x64, а нативные библиотеки ридера 32-битные. " +
                    "Соберите и запустите конфигурацию x86 (и положите x86 DLL рядом с .exe).");
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // путь поиска нативных DLL (RRU9816.dll и пр.)
            NativePath.PinOutputDir();

            // защита от случайного запуска в x64
            PlatformGuard.EnsureX86OrThrow();

            // (необязательно) глобальные хэндлеры ошибок
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => {
                try { Logger.Append("app_crash.log", "[ThreadException] " + e.Exception); } catch { }
                MessageBox.Show("Необработанное исключение: " + e.Exception.Message, "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                try { Logger.Append("app_crash.log", "[UnhandledException] " + (e.ExceptionObject?.ToString() ?? "(null)")); } catch { }
            };

            // культуры
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
