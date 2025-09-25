using System;
using System.Configuration;
using System.Linq;
using System.Text;

using ManagedClient;

using MarcRecord = ManagedClient.IrbisRecord;
using RecordField = ManagedClient.RecordField;

namespace LibraryTerminal
{
    public sealed class IrbisServiceManaged : IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;
        private string _lastConnectionString;

        public string CurrentLogin { get; private set; }
        public int LastReaderMfn { get; private set; }

        // === Подключение ===
        public void Connect()
        {
            var cs = ConfigurationManager.AppSettings["connection-string"]
                     ?? "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";
            Connect(cs);
        }

        public void Connect(string connectionString)
        {
            try { _client?.Disconnect(); } catch { }
            _client = new ManagedClient64();

            _client.ParseConnectionString(connectionString);
            _lastConnectionString = connectionString;

            try { _client.Connect(); } catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "IRBIS: не удалось подключиться. " + ExplainConn(connectionString) + " Error: " + ex.Message, ex);
            }

            _client.Timeout = 20000;

            if (!_client.Connected)
                throw new InvalidOperationException("IRBIS: не удалось подключиться.");

            CurrentLogin = _client.Username;
            _currentDb = _client.Database;
        }

        private void EnsureConnected()
        {
            if (_client != null && _client.Connected) return;

            string cs = _lastConnectionString
                     ?? ConfigurationManager.AppSettings["connection-string"]
                     ?? "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";

            if (_client == null) _client = new ManagedClient64();
            _client.ParseConnectionString(cs);

            try { _client.Connect(); } catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "IRBIS: нет подключения. " + ExplainConn(cs) + " Error: " + ex.Message, ex);
            }

            _client.Timeout = 20000;
            _lastConnectionString = cs;
            CurrentLogin = _client.Username;
            if (string.IsNullOrEmpty(_currentDb)) _currentDb = _client.Database;
        }
        
        private static string ExplainConn(string cs)
        {
            string host = "?", db = "?", user = "?"; int port = 0;
            foreach (var part in cs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                var v = kv[1].Trim();
                if (k == "host") host = v;
                else if (k == "port") int.TryParse(v, out port);
                else if (k == "user") user = v;
                else if (k == "db" || k == "database") db = v;
            }
            return "Host=" + host + ", Port=" + port + ", User=" + user + ", Db=" + db + ".";
        }

        public void UseDatabase(string dbName)
        {
            EnsureConnected();
            _client.Database = dbName;
            _currentDb = dbName;
        }

        // === Нормализация идентификаторов (RFID/UID/EPC/инв.) ===
        private static string NormalizeId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace(" ", "").Replace("-", "").Replace(":", "");
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return s.ToUpperInvariant();
        }

        // === Вспомогательные ===
        private string GetMaskMrg()
        {
            try
            {
                return _client != null && _client.Settings != null
                    ? _client.Settings.Get<string>("Private", "MaskMrg", "09")
                    : "09";
            } catch { return "09"; }
        }

        // Пишем в Logs через общий Logger
        private static void LogIrbis(string msg)
        {
            try
            {
                Logger.Append("irbis.log",
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg);
            } catch { }
        }

        private MarcRecord FindOne(string expression)
        {
            EnsureConnected();
            LogIrbis("SEARCH DB=" + _client.Database + " EXPR=" + expression);
            var records = _client.SearchRead(expression);
            return (records != null && records.Length > 0) ? records[0] : null;
        }

        private MarcRecord ReadRecord(int mfn)
        {
            EnsureConnected();
            return _client.ReadRecord(mfn);
        }

        private bool WriteRecordSafe(MarcRecord record)
        {
            EnsureConnected();
            _client.WriteRecord(record, /*needLock*/ false, /*ifUpdate*/ true);
            return true;
        }

        private string FormatRecord(string format, int mfn)
        {
            EnsureConnected();
            return (_client.FormatRecord(format, mfn) ?? string.Empty).Replace("\r", "").Replace("\n", "");
        }

        private T WithDatabase<T>(string db, Func<T> action)
        {
            EnsureConnected();
            var saved = _client.Database;
            try { _client.Database = db; return action(); }
            finally { _client.Database = saved; }
        }

        // Быстрая проверка коннекта
        public bool TestConnection(out string error)
        {
            try
            {
                EnsureConnected();
                var db = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
                var ok = WithDatabase(db, () => { var _ = FormatRecord("@brief", 1); return true; });
                error = null;
                return ok;
            } catch (Exception ex)
            {
                error = ex.Message;
                LogIrbis("TEST FAILED: " + ex.Message);
                return false;
            }
        }

        // === API для UI ===

        /// <summary>Проверка карты читателя по UID. True — найден.</summary>
        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            uid = NormalizeId(uid);

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            string listRaw = ConfigurationManager.AppSettings["ExprReaderByUidList"];
            if (!string.IsNullOrWhiteSpace(listRaw))
            {
                var patterns = listRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
                foreach (var pat in patterns)
                {
                    string expr = string.Format(pat, uid);
                    var rec = FindOne(expr);
                    if (rec != null) { LastReaderMfn = rec.Mfn; return true; }
                }
                return false;
            }

            string fmt = ConfigurationManager.AppSettings["ExprReaderByUid"] ?? "\"RI={0}\"";
            var recSingle = FindOne(string.Format(fmt, uid));
            if (recSingle != null) { LastReaderMfn = recSingle.Mfn; return true; }

            return false;
        }

        /// <summary>
        /// Поиск книги по RFID-метке (910^h) в IBIS.
        /// По умолчанию используем IN= (рекомендация разработчика), но пробуем и H/HI/HIN/RF/RFID.
        /// </summary>
        public MarcRecord FindOneByBookRfid(string rfid)
        {
            rfid = NormalizeId(rfid);
            if (string.IsNullOrWhiteSpace(rfid)) return null;

            rfid = new string(rfid.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
            if (rfid.Length < 8) { LogIrbis("BAD RFID KEY: " + rfid); return null; }
            if (rfid.Length > 24) rfid = rfid.Substring(0, 24);

            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";

            // 0) если задано в конфиге — пробуем это первым
            var tryList = new System.Collections.Generic.List<string>();
            var cfg = ConfigurationManager.AppSettings["ExprBookByRfid"];
            if (!string.IsNullOrWhiteSpace(cfg)) tryList.Add(cfg);

            // 1) типовые варианты индекса на 910^h
            tryList.AddRange(new[] {
                "\"IN={0}\"",     // IBIS: поиск книги по метке (рекоменд.)
                "\"H={0}\"",
                "\"HI={0}\"",
                "\"HIN={0}\"",
                "\"RF={0}\"",
                "\"RFID={0}\""
            });

            MarcRecord found = null;
            foreach (var pat in tryList)
            {
                var expr = string.Format(pat, rfid);
                try
                {
                    var rec = WithDatabase(booksDb, delegate { return FindOne(expr); });
                    LogIrbis("TRY DB=" + booksDb + " EXPR=" + expr + " -> " + (rec != null ? ("MFN " + rec.Mfn) : "NULL"));
                    if (rec != null) { found = rec; break; }
                } catch (Exception ex)
                {
                    LogIrbis("SEARCH FAIL EXPR=" + expr + " ERR=" + ex.Message);
                }
            }

            if (found == null)
                LogIrbis("NOT FOUND in DB=" + booksDb + " for RFID=" + rfid);

            return found;
        }

        /// <summary>
        /// Универсальный поиск: если 24HEX — метка (по IN=), иначе можно задать другой шаблон.
        /// </summary>
        public MarcRecord FindOneByInvOrTag(string value)
        {
            value = NormalizeId(value);
            if (string.IsNullOrWhiteSpace(value)) return null;

            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            string exprInv = ConfigurationManager.AppSettings["ExprBookByInv"] ?? "\"IN={0}\"";
            string exprTag = ConfigurationManager.AppSettings["ExprBookByRfid"] ?? "\"IN={0}\"";

            bool looksLikeHex24 = value.Length == 24 && value.All(Uri.IsHexDigit);
            var expr = string.Format(looksLikeHex24 ? exprTag : exprInv, value);

            return WithDatabase(booksDb, delegate { return FindOne(expr); });
        }

        /// <summary>Сменить 910^a по RFID (910^h), ничего не добавляя.</summary>
        public bool UpdateBook910StatusByRfidStrict(MarcRecord record, string rfidKey, string newStatus, string _unused = null)
        {
            if (record == null || string.IsNullOrWhiteSpace(rfidKey)) return false;

            rfidKey = NormalizeId(rfidKey);

            var target = record.Fields
                .GetField("910")
                .FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == rfidKey);
            if (target == null) { LogIrbis("Update910 FAIL: 910^h=" + rfidKey + " not found"); return false; }

            var sfa = target.SubFields.FirstOrDefault(s => s.Code == 'a');
            var sfh = target.SubFields.FirstOrDefault(s => s.Code == 'h');
            if (sfa == null || sfh == null) { LogIrbis("Update910 FAIL: subfields a/h missing"); return false; }

            var before = Dump910Short(record, rfidKey);
            sfa.Text = newStatus ?? "";
            var ok = WriteRecordSafe(record);
            var after = Dump910Short(record, rfidKey);
            LogIrbis("Update910 " + (ok ? "OK" : "FAIL") + ": " + before + " -> " + after);
            return ok;
        }

        private static string Dump910Short(MarcRecord rec, string rfid)
        {
            if (rec == null) return "(rec=null)";
            var key = NormalizeId(rfid);
            var block = rec.Fields.GetField("910").FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == key);
            if (block == null) return "910 not found";
            var a = block.GetFirstSubFieldText('a') ?? "";
            var b = block.GetFirstSubFieldText('b') ?? "";
            var h = block.GetFirstSubFieldText('h') ?? "";
            return "MFN=" + rec.Mfn + " 910: a=" + a + " b=" + b + " h=" + h;
        }

        /// <summary>Добавить повторение поля 40 в RDR при выдаче.</summary>
        public bool AppendRdr40OnIssue(int readerMfn, MarcRecord bookRec, string rfidHex, string maskMrg, string login, string catalogDbName)
        {
            if (readerMfn <= 0 || bookRec == null || string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            var rdr = ReadRecord(readerMfn);
            if (rdr == null) return false;

            rfidHex = NormalizeId(rfidHex);

            var ex910 = bookRec.Fields.GetField("910")
                            .FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == rfidHex);

            string shelfmark = bookRec.FM("903") ?? "";
            string inv = ex910 != null ? (ex910.GetFirstSubFieldText('b') ?? "") : "";
            string placeK = ex910 != null ? (ex910.GetFirstSubFieldText('d') ?? "") : "";

            string bookDb = !string.IsNullOrEmpty(catalogDbName) ? catalogDbName
                           : (bookRec.Database ?? (ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS"));
            string brief = WithDatabase(bookDb, delegate { return FormatRecord("@brief", bookRec.Mfn); }) ?? "";

            string date = DateTime.Now.ToString("yyyyMMdd");
            string time = DateTime.Now.ToString("HHmmss");
            string dateDue = DateTime.Now.AddDays(30).ToString("yyyyMMdd");

            var f40 = new RecordField("40")
                .AddSubField('a', shelfmark)
                .AddSubField('b', inv)
                .AddSubField('c', brief)
                .AddSubField('k', placeK)
                .AddSubField('v', maskMrg ?? "")
                .AddSubField('d', date)
                .AddSubField('1', time)
                .AddSubField('e', dateDue)
                .AddSubField('f', "******")
                .AddSubField('g', bookDb)
                .AddSubField('h', rfidHex)
                .AddSubField('i', string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login);

            var added = false;
            try { rdr.Fields.Add(f40); added = true; } catch { }
            if (!added) return false;

            var ok = WriteRecordSafe(rdr);
            LogIrbis("AppendRdr40 MFN=" + readerMfn + " RFID=" + rfidHex + " ok=" + ok);
            return ok;
        }

        /// <summary>Закрыть поле 40 при возврате (ищем по H= в RDR).</summary>
        public bool CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
            rfidHex = NormalizeId(rfidHex);
            if (string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            string expr = string.Format(ConfigurationManager.AppSettings["ExprReaderByItemRfid"] ?? "\"H={0}\"", rfidHex);
            var rdr = FindOne(expr);
            if (rdr == null) return false;

            var f40 = rdr.Fields
                .GetField("40")
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText('h'), rfidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.GetFirstSubFieldText('f'), "******", StringComparison.OrdinalIgnoreCase));
            if (f40 == null) return false;

            // ^C — убрать brief
            f40.RemoveSubField('c');

            // ^R — место возврата (если включено)
            bool useR = (ConfigurationManager.AppSettings["UseSubfieldR_ReturnPlace"] ?? "false")
                .Equals("true", StringComparison.OrdinalIgnoreCase);
            if (useR)
            {
                var rVal = maskMrg ?? "";
                var sfr = f40.SubFields.FirstOrDefault(sf => sf.Code == 'r');
                if (sfr == null) f40.AddSubField('r', rVal); else sfr.Text = rVal;
            }

            // ^2 — время возврата
            var nowTime = DateTime.Now.ToString("HHmmss");
            var sf2 = f40.SubFields.FirstOrDefault(sf => sf.Code == '2');
            if (sf2 == null) f40.AddSubField('2', nowTime); else sf2.Text = nowTime;

            // ^F — фактическая дата возврата
            var nowDate = DateTime.Now.ToString("yyyyMMdd");
            var sff = f40.SubFields.FirstOrDefault(sf => sf.Code == 'f');
            if (sff == null) f40.AddSubField('f', nowDate); else sff.Text = nowDate;

            // ^I — ответственное лицо
            var iVal = string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login;
            var sfi = f40.SubFields.FirstOrDefault(sf => sf.Code == 'i');
            if (sfi == null) f40.AddSubField('i', iVal); else sfi.Text = iVal;

            var ok = WriteRecordSafe(rdr);
            LogIrbis("CompleteRdr40 RFID=" + rfidHex + " ok=" + ok);
            return ok;
        }

        /// <summary>Выдача: СНАЧАЛА 40 в RDR, потом 910^a=1.</summary>
        public string IssueByRfid(string bookRfid)
        {
            bookRfid = NormalizeId(bookRfid);
            if (LastReaderMfn <= 0) throw new InvalidOperationException("Сначала вызови ValidateCard() для читателя.");
            if (string.IsNullOrWhiteSpace(bookRfid)) throw new ArgumentNullException("bookRfid");

            var book = FindOneByBookRfid(bookRfid);
            if (book == null) throw new InvalidOperationException("Книга по RFID не найдена.");

            var maskMrg = GetMaskMrg();
            var dbName = book.Database ?? (ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS");

            // 1) главное действие — запись в RDR
            if (!AppendRdr40OnIssue(LastReaderMfn, book, bookRfid, maskMrg, CurrentLogin, dbName))
                throw new InvalidOperationException("Не удалось добавить поле 40 читателю.");

            // 2) смена статуса книги
            if (!UpdateBook910StatusByRfidStrict(book, bookRfid, "1"))
                throw new InvalidOperationException("Не удалось обновить статус книги (910^A=1).");

            return WithDatabase(dbName, delegate { return FormatRecord("@brief", book.Mfn); });
        }

        /// <summary>Возврат: СНАЧАЛА закрываем 40, потом 910^a=0.</summary>
        public string ReturnByRfid(string bookRfid)
        {
            bookRfid = NormalizeId(bookRfid);
            if (string.IsNullOrWhiteSpace(bookRfid)) throw new ArgumentNullException("bookRfid");

            // 1) закрываем поле 40 у читателя
            if (!CompleteRdr40OnReturn(bookRfid, GetMaskMrg(), CurrentLogin))
                throw new InvalidOperationException("Не удалось обновить поле 40 в записи читателя.");

            // 2) меняем статус в книге
            var book = FindOneByBookRfid(bookRfid);
            if (book == null) throw new InvalidOperationException("Книга по RFID не найдена.");

            if (!UpdateBook910StatusByRfidStrict(book, bookRfid, "0"))
                throw new InvalidOperationException("Не удалось обновить статус книги (910^A=0).");

            var dbName = book.Database ?? (ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS");
            return WithDatabase(dbName, delegate { return FormatRecord("@brief", book.Mfn); });
        }

        public void Dispose()
        {
            try { _client?.Disconnect(); } catch { }
            _client = null;
        }
    }
}