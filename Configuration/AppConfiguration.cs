using System;
using System.Configuration;

namespace LibraryTerminal
{
    /// <summary>
    /// Конфигурация приложения
    /// </summary>
    public class AppConfiguration
    {
        public IrbisConfiguration Irbis { get; set; }
        public ReaderConfiguration BookReader { get; set; }
        public ReaderConfiguration CardReader { get; set; }
        public ArduinoConfiguration Arduino { get; set; }
        public TimeoutConfiguration Timeouts { get; set; }
        public Rru9816Configuration Rru9816 { get; set; }
        public UhfReader09Configuration UhfReader09 { get; set; }
        public IqrfidConfiguration Iqrfid { get; set; }
        public Acr1281Configuration Acr1281 { get; set; }

        public static AppConfiguration Load()
        {
            return new AppConfiguration
            {
                Irbis = IrbisConfiguration.Load(),
                BookReader = ReaderConfiguration.LoadBookReader(),
                CardReader = ReaderConfiguration.LoadCardReader(),
                Arduino = ArduinoConfiguration.Load(),
                Timeouts = TimeoutConfiguration.Load(),
                Rru9816 = Rru9816Configuration.Load(),
                UhfReader09 = UhfReader09Configuration.Load(),
                Iqrfid = IqrfidConfiguration.Load(),
                Acr1281 = Acr1281Configuration.Load()
            };
        }

        private static string GetSetting(string key, string defaultValue = "")
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }

        private static bool GetBoolSetting(string key, bool defaultValue)
        {
            return bool.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }
    }

    public class IrbisConfiguration
    {
        public string ConnectionString { get; set; }
        public string BooksDatabase { get; set; }
        public string ReadersDatabase { get; set; }
        public string BookBriefFormat { get; set; }
        public string ReaderBriefFormat { get; set; }

        public static IrbisConfiguration Load()
        {
            var conn = ConfigurationManager.AppSettings["connection-string"] 
                ?? ConfigurationManager.AppSettings["ConnectionString"]
                ?? "host=172.29.67.70;port=6666;user=09f00st;password=f00st;db=KAT%SERV09%;";

            return new IrbisConfiguration
            {
                ConnectionString = conn,
                BooksDatabase = GetSetting("BooksDb", "KAT%SERV09%"),
                ReadersDatabase = GetSetting("ReadersDb", "RDR"),
                BookBriefFormat = GetSetting("BookBriefFormat", "@brief"),
                ReaderBriefFormat = GetSetting("BriefFormat", "@brief")
            };
        }

        private static string GetSetting(string key, string defaultValue)
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }
    }

    public class ReaderConfiguration
    {
        public bool Enabled { get; set; }
        public string TakePort { get; set; }
        public string ReturnPort { get; set; }
        public int BaudRate { get; set; }
        public string NewLine { get; set; }

        public static ReaderConfiguration LoadBookReader()
        {
            return new ReaderConfiguration
            {
                Enabled = GetBoolSetting("EnableBookScanners", false),
                TakePort = GetSetting("BookTakePort") ?? GetSetting("BookPort", ""),
                ReturnPort = GetSetting("BookReturnPort") ?? GetSetting("BookPort", ""),
                BaudRate = GetIntSetting("BaudBookTake", GetIntSetting("BaudBook", 9600)),
                NewLine = GetSetting("NewLineBookTake") ?? GetSetting("NewLineBook", "\r\n")
            };
        }

        public static ReaderConfiguration LoadCardReader()
        {
            return new ReaderConfiguration
            {
                Enabled = true, // Кардридеры всегда включены
                TakePort = GetSetting("IqrfidPort", ""),
                BaudRate = GetIntSetting("BaudIqrfid", 115200),
                NewLine = GetSetting("NewLineIqrfid", "\n")
            };
        }

        private static string GetSetting(string key, string defaultValue = "")
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }

        private static bool GetBoolSetting(string key, bool defaultValue)
        {
            return bool.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }
    }

    public class ArduinoConfiguration
    {
        public bool Enabled { get; set; }
        public string Port { get; set; }
        public int BaudRate { get; set; }
        public string NewLine { get; set; }
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }
        public int OpenDelayMs { get; set; }
        public int CommandDelayMs { get; set; }

        public static ArduinoConfiguration Load()
        {
            return new ArduinoConfiguration
            {
                Enabled = GetBoolSetting("EnableArduino", true),
                Port = GetSetting("ArduinoPort", ""),
                BaudRate = GetIntSetting("BaudArduino", 115200),
                NewLine = GetSetting("NewLineArduino", "\n"),
                DtrEnable = GetBoolSetting("ArduinoDtr", false),
                RtsEnable = GetBoolSetting("ArduinoRts", false),
                OpenDelayMs = GetIntSetting("ArduinoOpenDelayMs", 2000),
                CommandDelayMs = GetIntSetting("ArduinoCommandDelayMs", 50)
            };
        }

        private static string GetSetting(string key, string defaultValue = "")
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }

        private static bool GetBoolSetting(string key, bool defaultValue)
        {
            return bool.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }
    }

    public class TimeoutConfiguration
    {
        public int ReadTimeoutMs { get; set; }
        public int WriteTimeoutMs { get; set; }
        public int AutoReconnectMs { get; set; }
        public int DebounceMs { get; set; }
        public int BookDebounceMs { get; set; }

        public static TimeoutConfiguration Load()
        {
            return new TimeoutConfiguration
            {
                ReadTimeoutMs = GetIntSetting("ReadTimeoutMs", 5000),
                WriteTimeoutMs = GetIntSetting("WriteTimeoutMs", 1000),
                AutoReconnectMs = GetIntSetting("AutoReconnectMs", 3000),
                DebounceMs = GetIntSetting("DebounceMs", 250),
                BookDebounceMs = GetIntSetting("BookDebounceMs", 800)
            };
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(ConfigurationManager.AppSettings[key], out var value) ? value : defaultValue;
        }
    }

    public class Rru9816Configuration
    {
        public string Port { get; set; }
        public int BaudRate { get; set; }

        public static Rru9816Configuration Load()
        {
            return new Rru9816Configuration
            {
                Port = GetSetting("RruPort", "COM5"),
                BaudRate = GetIntSetting("RruBaudRate", 57600)
            };
        }

        private static string GetSetting(string key, string defaultValue = "")
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }
    }

    public class UhfReader09Configuration
    {
        public int BaudIndex { get; set; }
        public int PollMs { get; set; }
        public int CardUidLength { get; set; }

        public static UhfReader09Configuration Load()
        {
            return new UhfReader09Configuration
            {
                BaudIndex = GetIntSetting("UhfReader09BaudIndex", 3),
                PollMs = GetIntSetting("UhfReader09PollMs", 100),
                CardUidLength = GetIntSetting("UhfCardUidLength", 24)
            };
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(ConfigurationManager.AppSettings[key], out var value) ? value : defaultValue;
        }
    }

    public class IqrfidConfiguration
    {
        public string Port { get; set; }
        public int BaudRate { get; set; }
        public string NewLine { get; set; }
        public string InitCommand { get; set; }
        public int InitDelayBeforeMs { get; set; }
        public int InitDelayAfterMs { get; set; }

        public static IqrfidConfiguration Load()
        {
            return new IqrfidConfiguration
            {
                Port = GetSetting("IqrfidPort", ""),
                BaudRate = GetIntSetting("BaudIqrfid", 115200),
                NewLine = GetSetting("NewLineIqrfid", "\n"),
                InitCommand = GetSetting("IqrfidInitCmd", ""),
                InitDelayBeforeMs = GetIntSetting("IqrfidInitDelayBeforeMs", 100),
                InitDelayAfterMs = GetIntSetting("IqrfidInitDelayAfterMs", 100)
            };
        }

        private static string GetSetting(string key, string defaultValue = "")
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }
    }

    public class Acr1281Configuration
    {
        public string PreferredReaderName { get; set; }
        public int PollIntervalMs { get; set; }
        public int ConnectRetries { get; set; }
        public int BusyRetryDelayMs { get; set; }

        public static Acr1281Configuration Load()
        {
            return new Acr1281Configuration
            {
                PreferredReaderName = GetSetting("AcrReaderName", "PICC"),
                PollIntervalMs = GetIntSetting("AcrPollIntervalMs", 250),
                ConnectRetries = GetIntSetting("AcrConnectRetries", 10),
                BusyRetryDelayMs = GetIntSetting("AcrBusyRetryDelayMs", 300)
            };
        }

        private static string GetSetting(string key, string defaultValue = "")
        {
            return ConfigurationManager.AppSettings[key] ?? defaultValue;
        }

        private static int GetIntSetting(string key, int defaultValue)
        {
            return int.TryParse(GetSetting(key), out var value) ? value : defaultValue;
        }
    }
}
