using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
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

        // ★ Удобные свойства конфигурации
        private string BooksDb => ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";

        private string ReadersDb => ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
        private string BookBriefFormat => ConfigurationManager.AppSettings["BookBriefFormat"] ?? "@brief";
        public string ConnectionString => _lastConnectionString;

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

        // --- генератор вариантов UID (HEX, разделители, реверс, DEC и DEC с нулями)
        private static IEnumerable<string> MakeUidVariants(string uid)
        {
            var baseHex = NormalizeId(uid) ?? "";
            var hexOnly = new string(baseHex.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
            if (hexOnly.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(baseHex)) yield return baseHex;
                yield break;
            }

            // базовые HEX-варианты
            yield return hexOnly;                        // ABCDEF12
            yield return InsertEvery2(hexOnly, ":");     // AB:CD:EF:12
            yield return InsertEvery2(hexOnly, "-");     // AB-CD-EF-12

            // реверс по байтам
            var revHex = ReverseByByte(hexOnly);
            if (!string.Equals(revHex, hexOnly, StringComparison.Ordinal))
            {
                yield return revHex;
                yield return InsertEvery2(revHex, ":");
                yield return InsertEvery2(revHex, "-");
            }

            // десятичные представления
            if (ulong.TryParse(hexOnly, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valHex))
            {
                var dec = valHex.ToString(CultureInfo.InvariantCulture);
                yield return dec;
                if (dec.Length < 10) yield return dec.PadLeft(10, '0');

                if (ulong.TryParse(revHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valRev))
                {
                    var decRev = valRev.ToString(CultureInfo.InvariantCulture);
                    yield return decRev;
                    if (decRev.Length < 10) yield return decRev.PadLeft(10, '0');
                }
            }
        }

        private static string InsertEvery2(string hex, string sep)
        {
            var sb = new StringBuilder(hex.Length + hex.Length / 2);
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (i > 0) sb.Append(sep);
                int len = Math.Min(2, hex.Length - i);
                sb.Append(hex, i, len);
            }
            return sb.ToString();
        }

        private static string ReverseByByte(string hex)
        {
            var sb = new StringBuilder(hex.Length);
            for (int i = hex.Length; i > 0; i -= 2)
            {
                int start = Math.Max(0, i - 2);
                int len = Math.Min(2, i - start);
                sb.Append(hex, start, len);
            }
            return sb.ToString();
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
            var records = _client.SearchRead(expression);
            LogIrbis($"SEARCH DB={_client.Database} EXPR={expression} -> found={(records?.Length ?? 0)}");
            return (records != null && records.Length > 0) ? records[0] : null;
        }

        private MarcRecord ReadRecord(int mfn)
        {
            EnsureConnected();
            return _client.ReadRecord(mfn);
        }

        // ★ безопасная запись с логом ошибки
        private bool WriteRecordSafe(MarcRecord record)
        {
            try
            {
                EnsureConnected();
                _client.WriteRecord(record, /*needLock*/ false, /*ifUpdate*/ true);
                return true;
            } catch (Exception ex)
            {
                LogIrbis("WRITE FAIL MFN=" + record?.Mfn + " DB=" + _client?.Database + " ERR=" + ex.Message);
                return false;
            }
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

        // ★ единая обёртка для brief (используется в Issue/Return)
        private string FormatBrief(string fmt, string db, int mfn)
        {
            return WithDatabase(db, () => FormatRecord(fmt, mfn));
        }

        // Быстрая проверка коннекта
        public bool TestConnection(out string error)
        {
            try
            {
                EnsureConnected();
                var ok = WithDatabase(BooksDb, () => { var _ = FormatRecord("@brief", 1); return true; });
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

            string rdrDb = ReadersDb;
            UseDatabase(rdrDb);

            var patterns = new List<string>();
            var listRaw = ConfigurationManager.AppSettings["ExprReaderByUidList"];
            if (!string.IsNullOrWhiteSpace(listRaw))
                patterns.AddRange(listRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

            var single = ConfigurationManager.AppSettings["ExprReaderByUid"];
            if (!string.IsNullOrWhiteSpace(single))
                patterns.Add(single.Trim());

            if (patterns.Count == 0)
                patterns.Add("\"RI={0}\""); // дефолт

            foreach (var uidVariant in MakeUidVariants(uid))
            {
                foreach (var pat in patterns)
                {
                    var expr = string.Format(pat, uidVariant);
                    LogIrbis($"CARD TRY DB={rdrDb} PAT={pat} UID={uidVariant}");
                    var rec = FindOne(expr);
                    if (rec != null)
                    {
                        LastReaderMfn = rec.Mfn;
                        LogIrbis($"CARD OK: MFN={LastReaderMfn} via PAT={pat} UID={uidVariant}");
                        return true;
                    }
                }
            }

            LogIrbis("CARD NOT FOUND for UID=" + uid);
            return false;
        }

        /// <summary>Проверка по номеру читательского билета.</summary>
        public bool ValidateReaderByTicketNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var raw = value.Trim();
            var hexOnly = new string(raw.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
            if (hexOnly.Length >= 8 && hexOnly.Length <= 32 && hexOnly.All(Uri.IsHexDigit))
            {
                LogIrbis($"TICKET looks like UID HEX -> fallback to ValidateCard(), val={raw}");
                return ValidateCard(hexOnly);
            }

            string rdrDb = ReadersDb;
            UseDatabase(rdrDb);

            var patterns = new List<string>();
            var listRaw = ConfigurationManager.AppSettings["ExprReaderByTicketList"];
            if (!string.IsNullOrWhiteSpace(listRaw))
                patterns.AddRange(listRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

            var single = ConfigurationManager.AppSettings["ExprReaderByTicket"];
            if (!string.IsNullOrWhiteSpace(single))
                patterns.Add(single.Trim());

            if (patterns.Count == 0) patterns.Add("\"R={0}\"");

            var candidates = new List<string>();
            var digitsOnly = new string(raw.Where(char.IsDigit).ToArray());
            candidates.Add(raw);
            if (!string.IsNullOrEmpty(digitsOnly) && digitsOnly != raw) candidates.Add(digitsOnly);
            if (digitsOnly.StartsWith("0") && digitsOnly.Length > 1) candidates.Add(digitsOnly.TrimStart('0'));

            foreach (var val in candidates.Distinct())
            {
                foreach (var pat in patterns)
                {
                    var expr = string.Format(pat, val);
                    LogIrbis($"TICKET TRY DB={rdrDb} PAT={pat} VAL={val}");
                    var rec = FindOne(expr);
                    if (rec != null)
                    {
                        LastReaderMfn = rec.Mfn;
                        LogIrbis($"TICKET OK MFN={LastReaderMfn} via PAT={pat} VAL={val}");
                        return true;
                    }
                }
            }

            LogIrbis("TICKET NOT FOUND for value=" + raw);
            return false;
        }

        /// <summary>
        /// Поиск книги по RFID-метке (910^h) в IBIS. Пробуем полный EPC-96 и «хвосты».
        /// </summary>
        public MarcRecord FindOneByBookRfid(string rfid)
        {
            rfid = NormalizeId(rfid);
            if (string.IsNullOrWhiteSpace(rfid)) return null;

            rfid = new string(rfid.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
            if (rfid.Length < 8) { LogIrbis("BAD RFID KEY: " + rfid); return null; }
            if (rfid.Length > 24) rfid = rfid.Substring(0, 24);

            var keyVariants = new List<string> { rfid };
            if (rfid.Length >= 16) keyVariants.Add(rfid.Substring(rfid.Length - 16));
            if (rfid.Length >= 8) keyVariants.Add(rfid.Substring(rfid.Length - 8));

            string booksDb = BooksDb;

            var patterns = new List<string>();
            var cfgPat = ConfigurationManager.AppSettings["ExprBookByRfid"];
            if (!string.IsNullOrWhiteSpace(cfgPat)) patterns.Add(cfgPat);

            patterns.AddRange(new[] {
                "\"H={0}\"","\"HI={0}\"","\"HIN={0}\"","\"RF={0}\"","\"RFID={0}\"","\"IN={0}\""
            });

            MarcRecord found = null;
            foreach (var v in keyVariants)
            {
                foreach (var pat in patterns)
                {
                    var expr = string.Format(pat, v);
                    try
                    {
                        var rec = WithDatabase(booksDb, () => FindOne(expr));
                        LogIrbis($"TRY DB={booksDb} EXPR={expr} -> {(rec != null ? ("MFN " + rec.Mfn) : "NULL")}");
                        if (rec != null) { found = rec; break; }
                    } catch (Exception ex)
                    {
                        LogIrbis("SEARCH FAIL EXPR=" + expr + " ERR=" + ex.Message);
                    }
                }
                if (found != null) break;
            }

            if (found == null)
                LogIrbis("NOT FOUND in DB=" + booksDb + " for RFID=" + rfid);

            return found;
        }

        /// <summary>Универсальный поиск: если 24HEX — метка (по IN=), иначе по инвентарному.</summary>
        public MarcRecord FindOneByInvOrTag(string value)
        {
            value = NormalizeId(value);
            if (string.IsNullOrWhiteSpace(value)) return null;

            string booksDb = BooksDb;
            string exprInv = ConfigurationManager.AppSettings["ExprBookByInv"] ?? "\"IN={0}\"";
            string exprTag = ConfigurationManager.AppSettings["ExprBookByRfid"] ?? "\"IN={0}\"";

            bool looksLikeHex24 = value.Length == 24 && value.All(Uri.IsHexDigit);
            var expr = string.Format(looksLikeHex24 ? exprTag : exprInv, value);

            return WithDatabase(booksDb, delegate { return FindOne(expr); });
        }

        /// <summary>
        /// Обновить статус экземпляра в 910 по RFID (910^h): ^A = "0" | "1".
        /// Запись сохраняется в текущем клиенте, без дополнительных подключений.
        /// </summary>
        public bool UpdateBook910StatusByRfidStrict(IrbisRecord rec, string scannedTag, string newStatus)
        {
            if (rec == null) return false;
            var key = NormalizeTag(scannedTag);
            if (string.IsNullOrEmpty(key)) return false;

            // 1) Находим нужный повтор 910 по ^h (допускаем «хвостовое» совпадение)
            RecordField f910 = null;
            foreach (var f in rec.Fields)
            {
                if (f.Tag != "910") continue;
                var hVal = NormalizeTag(f.GetFirstSubFieldText('h'));
                if (string.IsNullOrEmpty(hVal)) continue;

                if (hVal == key || key.EndsWith(hVal) || hVal.EndsWith(key))
                {
                    f910 = f;
                    break;
                }
            }
            if (f910 == null) return false;

            // 2) Гарантируем наличие ^h
            var hSf = f910.SubFields.FirstOrDefault(sf => sf.Code == 'h');
            if (hSf == null) f910.AddSubField('h', key);
            else if (string.IsNullOrWhiteSpace(hSf.Text)) hSf.Text = key;

            // 3) Upsert статуса ^a (0/1)
            var aSf = f910.SubFields.FirstOrDefault(sf => sf.Code == 'a');
            if (aSf == null) f910.AddSubField('a', newStatus);
            else aSf.Text = newStatus;

            // 4) Сохраняем запись в IBIS
            var booksDb = rec.Database ?? BooksDb;
            return WithDatabase(booksDb, () => WriteRecordSafe(rec));
        }

        // ===== ХЕЛПЕРЫ =====

        private static string NormalizeTag(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace(" ", "").Replace("-", "").Replace(":", "");
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            return s.ToUpperInvariant();
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

            string rdrDb = ReadersDb;
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
                           : (bookRec.Database ?? BooksDb);
            string brief = WithDatabase(bookDb, delegate { return FormatRecord("@brief", bookRec.Mfn); }) ?? "";

            var now = DateTime.Now;
            string date = now.ToString("yyyyMMdd");
            string time = now.ToString("HHmmss");
            int loanDays = int.TryParse(ConfigurationManager.AppSettings["LoanDays"], out var dd) ? dd : 30;
            string dateDue = now.AddDays(loanDays).ToString("yyyyMMdd");

            var f40 = new RecordField("40")
                .AddSubField('A', shelfmark)                          // шифр (903)
                .AddSubField('B', inv)                                // инв. номер (910^B)
                .AddSubField('C', brief)                              // краткое описание
                .AddSubField('K', placeK)                             // место хранения (910^D)
                .AddSubField('V', maskMrg ?? "")                      // место выдачи (MaskMrg)
                .AddSubField('D', date)                               // дата выдачи
                .AddSubField('1', time)                               // время выдачи
                .AddSubField('E', dateDue)                            // дата предполагаемого возврата
                .AddSubField('F', "******")                           // пока не возвращена
                .AddSubField('G', bookDb)                             // БД каталога
                .AddSubField('H', rfidHex)                            // RFID метка книги
                .AddSubField('I', string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login) // оператор
                .AddSubField('Z', Guid.NewGuid().ToString("N"));      // идентификатор строки

            rdr.Fields.Add(f40);

            var ok = WriteRecordSafe(rdr);
            LogIrbis("AppendRdr40 MFN=" + readerMfn + " RFID=" + rfidHex + " ok=" + ok);
            return ok;
        }

        /// <summary>Закрыть поле 40 при возврате (ищем по HIN=rfid книги в RDR, как в инструкции).</summary>
        public bool CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
            rfidHex = NormalizeId(rfidHex);
            if (string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ReadersDb;
            UseDatabase(rdrDb);

            // По умолчанию — HIN={rfid}, см. инструкцию по возврату
            string expr = string.Format(
                ConfigurationManager.AppSettings["ExprReaderByItemRfid"] ?? "\"HIN={0}\"",
                rfidHex
            );

            var rdr = FindOne(expr);
            if (rdr == null) return false;

            var f40 = rdr.Fields
                .GetField("40")
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText('H'), rfidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.GetFirstSubFieldText('F'), "******", StringComparison.OrdinalIgnoreCase));
            if (f40 == null) return false;

            // ^C — удалить краткое описание
            f40.RemoveSubField('C');

            // ^R — место возврата
            var useR = true; // по умолчанию пишем R; можно сделать опцией
            if (useR)
            {
                var rVal = maskMrg ?? "";
                var sfr = f40.SubFields.FirstOrDefault(sf => char.ToUpperInvariant(sf.Code) == 'R');
                if (sfr == null) f40.AddSubField('R', rVal); else sfr.Text = rVal;
            }

            var now = DateTime.Now;

            // ^2 — время возврата
            var nowTime = now.ToString("HHmmss");
            var sf2 = f40.SubFields.FirstOrDefault(sf => sf.Code == '2');
            if (sf2 == null) f40.AddSubField('2', nowTime); else sf2.Text = nowTime;

            // ^F — фактическая дата возврата
            var nowDate = now.ToString("yyyyMMdd");
            var sff = f40.SubFields.FirstOrDefault(sf => char.ToUpperInvariant(sf.Code) == 'F');
            if (sff == null) f40.AddSubField('F', nowDate); else sff.Text = nowDate;

            // ^I — ответственное лицо (оператор)
            var iVal = string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login;
            var sfi = f40.SubFields.FirstOrDefault(sf => char.ToUpperInvariant(sf.Code) == 'I');
            if (sfi == null) f40.AddSubField('I', iVal); else sfi.Text = iVal;

            var ok = WriteRecordSafe(rdr);
            LogIrbis("CompleteRdr40 RFID=" + rfidHex + " ok=" + ok);
            return ok;
        }

        /// <summary>Выдача: СНАЧАЛА 40 в RDR, потом 910^A=1.</summary>
        public string IssueByRfid(string bookRfid)
        {
            bookRfid = NormalizeId(bookRfid);
            if (LastReaderMfn <= 0) throw new InvalidOperationException("Сначала вызови ValidateCard() для читателя.");
            if (string.IsNullOrWhiteSpace(bookRfid)) throw new ArgumentNullException(nameof(bookRfid));

            var book = FindOneByBookRfid(bookRfid);
            if (book == null) throw new InvalidOperationException("Книга по RFID не найдена.");

            var maskMrg = GetMaskMrg();
            var dbName = book.Database ?? BooksDb;

            // 1) запись в RDR (поле 40)
            if (!AppendRdr40OnIssue(LastReaderMfn, book, bookRfid, maskMrg, CurrentLogin, dbName))
                throw new InvalidOperationException("Не удалось добавить поле 40 читателю.");

            // 2) смена статуса книги (910^A=1)
            if (!UpdateBook910StatusByRfidStrict(book, bookRfid, "1"))
                throw new InvalidOperationException("Не удалось обновить статус книги (910^A=1).");

            // ★ единое форматирование brief (что ждёт MainForm)
            var brief = FormatBrief(BookBriefFormat, dbName, book.Mfn);
            return string.IsNullOrWhiteSpace(brief) ? "[без описания]" : brief.Trim();
        }

        /// <summary>Возврат: СНАЧАЛА закрываем 40, потом 910^A=0.</summary>
        public string ReturnByRfid(string bookRfid)
        {
            bookRfid = NormalizeId(bookRfid);
            if (string.IsNullOrWhiteSpace(bookRfid)) throw new ArgumentNullException(nameof(bookRfid));

            // 1) закрываем поле 40 у читателя
            if (!CompleteRdr40OnReturn(bookRfid, GetMaskMrg(), CurrentLogin))
                throw new InvalidOperationException("Не удалось обновить поле 40 в записи читателя.");

            // 2) меняем статус в книге
            var book = FindOneByBookRfid(bookRfid);
            if (book == null) throw new InvalidOperationException("Книга по RFID не найдена.");

            if (!UpdateBook910StatusByRfidStrict(book, bookRfid, "0"))
                throw new InvalidOperationException("Не удалось обновить статус книги (910^A=0).");

            var dbName = book.Database ?? BooksDb;

            // ★ единое форматирование brief (что ждёт MainForm)
            var brief = FormatBrief(BookBriefFormat, dbName, book.Mfn);
            return string.IsNullOrWhiteSpace(brief) ? "[без описания]" : brief.Trim();
        }

        public void Dispose()
        {
            try { _client?.Disconnect(); } catch { }
            _client = null;
        }
    }
}