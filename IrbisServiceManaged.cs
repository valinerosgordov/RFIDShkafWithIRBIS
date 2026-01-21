using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Text;
using LibraryTerminal.Core;

using ManagedClient;

using MarcRecord = ManagedClient.IrbisRecord;
using RecordField = ManagedClient.RecordField;

namespace LibraryTerminal
{
    public sealed class IrbisServiceManaged : IIrbisService, IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;
        private string _lastConnectionString;

        public string CurrentLogin { get; private set; }
        public int LastReaderMfn { get; private set; }

        private string BooksDb => ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";

        private string ReadersDb => ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
        private string BookBriefFormat => ConfigurationManager.AppSettings["BookBriefFormat"] ?? "@brief";
        public string ConnectionString => _lastConnectionString;

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

        internal static Option<string> NormalizeId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) 
                return Option<string>.None;
            
            var sb = new StringBuilder(s.Trim());
            sb.Replace(" ", "").Replace("-", "").Replace(":", "");
            
            var result = sb.ToString();
            if (result.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) 
                result = result.Substring(2);
            
            return Option<string>.Some(result.ToUpperInvariant());
        }

        private static IEnumerable<string> MakeUidVariants(string uid)
        {
            var normalized = NormalizeId(uid);
            if (!normalized.HasValue)
                yield break;

            var baseHex = normalized.Value;
            var hexOnly = new StringBuilder(baseHex.Length);
            foreach (var c in baseHex)
            {
                if (Uri.IsHexDigit(c))
                    hexOnly.Append(char.ToUpperInvariant(c));
            }
            
            var hexStr = hexOnly.ToString();
            if (hexStr.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(baseHex)) 
                    yield return baseHex;
                yield break;
            }

            yield return hexStr;
            yield return InsertEvery2(hexStr, ":");
            yield return InsertEvery2(hexStr, "-");
            var revHex = ReverseByByte(hexStr);
            if (!string.Equals(revHex, hexStr, StringComparison.Ordinal))
            {
                yield return revHex;
                yield return InsertEvery2(revHex, ":");
                yield return InsertEvery2(revHex, "-");
            }

            if (ulong.TryParse(hexStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valHex))
            {
                var dec = valHex.ToString(CultureInfo.InvariantCulture);
                yield return dec;
                if (dec.Length < 10) 
                    yield return dec.PadLeft(10, '0');

                if (ulong.TryParse(revHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valRev))
                {
                    var decRev = valRev.ToString(CultureInfo.InvariantCulture);
                    yield return decRev;
                    if (decRev.Length < 10) 
                        yield return decRev.PadLeft(10, '0');
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

        private Option<MarcRecord> FindOne(string expression)
        {
            EnsureConnected();
            var records = _client.SearchRead(expression);
            LogIrbis($"SEARCH DB={_client.Database} EXPR={expression} -> found={(records?.Length ?? 0)}");
            return (records != null && records.Length > 0) 
                ? Option<MarcRecord>.Some(records[0]) 
                : Option<MarcRecord>.None;
        }

        private MarcRecord ReadRecord(int mfn)
        {
            EnsureConnected();
            return _client.ReadRecord(mfn);
        }

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

        private string FormatBrief(string fmt, string db, int mfn)
        {
            return WithDatabase(db, () => FormatRecord(fmt, mfn));
        }

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

        /// <summary>Проверка карты читателя по UID. True — найден.</summary>
        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) 
                return false;

            var rdrDb = ReadersDb;
            UseDatabase(rdrDb);

            var patterns = new List<string>();
            var listRaw = ConfigurationManager.AppSettings["ExprReaderByUidList"];
            if (!string.IsNullOrWhiteSpace(listRaw))
            {
                var parts = listRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        patterns.Add(trimmed);
                }
            }

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
                    if (rec.HasValue)
                    {
                        LastReaderMfn = rec.Value.Mfn;
                        LogIrbis($"CARD OK: MFN={LastReaderMfn} via PAT={pat} UID={uidVariant}");
                        return true;
                    }
                }
            }

            LogIrbis("CARD NOT FOUND for UID=" + uid);
            return false;
        }

        /// <summary>
        /// Поиск книги по RFID-метке (910^h) в IBIS. Пробуем полный EPC-96 и «хвосты».
        /// </summary>
        public Option<MarcRecord> FindOneByBookRfid(string rfid)
        {
            var normalized = NormalizeId(rfid);
            if (!normalized.HasValue) 
                return Option<MarcRecord>.None;

            var sb = new StringBuilder(normalized.Value.Length);
            foreach (var c in normalized.Value)
            {
                if (Uri.IsHexDigit(c))
                    sb.Append(char.ToUpperInvariant(c));
            }
            
            var hexRfid = sb.ToString();
            if (hexRfid.Length < 8) 
            { 
                LogIrbis("BAD RFID KEY: " + hexRfid); 
                return Option<MarcRecord>.None; 
            }
            if (hexRfid.Length > 24) 
                hexRfid = hexRfid.Substring(0, 24);

            var keyVariants = new List<string> { hexRfid };
            if (hexRfid.Length >= 16) 
                keyVariants.Add(hexRfid.Substring(hexRfid.Length - 16));
            if (hexRfid.Length >= 8) 
                keyVariants.Add(hexRfid.Substring(hexRfid.Length - 8));

            var booksDb = BooksDb;

            var patterns = new List<string>();
            var cfgPat = ConfigurationManager.AppSettings["ExprBookByRfid"];
            if (!string.IsNullOrWhiteSpace(cfgPat)) 
                patterns.Add(cfgPat);

            patterns.AddRange(new[] {
                "\"H={0}\"","\"HI={0}\"","\"HIN={0}\"","\"RF={0}\"","\"RFID={0}\"","\"IN={0}\""
            });

            foreach (var v in keyVariants)
            {
                foreach (var pat in patterns)
                {
                    var expr = string.Format(pat, v);
                    try
                    {
                        var rec = WithDatabase(booksDb, () => FindOne(expr));
                        LogIrbis($"TRY DB={booksDb} EXPR={expr} -> {(rec.HasValue ? ("MFN " + rec.Value.Mfn) : "NONE")}");
                        if (rec.HasValue) 
                            return rec;
                    } 
                    catch (Exception ex)
                    {
                        LogIrbis("SEARCH FAIL EXPR=" + expr + " ERR=" + ex.Message);
                    }
                }
            }

            LogIrbis("NOT FOUND in DB=" + booksDb + " for RFID=" + rfid);
            return Option<MarcRecord>.None;
        }


        /// <summary>
        /// Обновить статус экземпляра в 910 по RFID (910^h): ^A = "0" | "1".
        /// Запись сохраняется в текущем клиенте, без дополнительных подключений.
        /// </summary>
        public Result<bool> UpdateBook910StatusByRfidStrict(IrbisRecord rec, string scannedTag, string newStatus)
        {
            if (rec == null) 
                return Result<bool>.Failure("Record is null");
            
            var normalizedKey = NormalizeTag(scannedTag);
            if (!normalizedKey.HasValue) 
                return Result<bool>.Failure("Invalid scanned tag");

            var key = normalizedKey.Value;

            RecordField f910 = null;
            foreach (var f in rec.Fields)
            {
                if (f.Tag != "910") 
                    continue;
                
                var hValOpt = NormalizeTag(f.GetFirstSubFieldText('h'));
                if (!hValOpt.HasValue) 
                    continue;

                var hVal = hValOpt.Value;
                if (hVal == key || key.EndsWith(hVal) || hVal.EndsWith(key))
                {
                    f910 = f;
                    break;
                }
            }
            
            if (f910 == null) 
                return Result<bool>.Failure("Field 910 not found for tag");

            RecordField.SubField hSf = null;
            foreach (var sf in f910.SubFields)
            {
                if (sf.Code == 'h')
                {
                    hSf = sf;
                    break;
                }
            }
            
            if (hSf == null) 
                f910.AddSubField('h', key);
            else if (string.IsNullOrWhiteSpace(hSf.Text)) 
                hSf.Text = key;

            RecordField.SubField aSf = null;
            foreach (var sf in f910.SubFields)
            {
                if (sf.Code == 'a')
                {
                    aSf = sf;
                    break;
                }
            }
            
            if (aSf == null) 
                f910.AddSubField('a', newStatus);
            else 
                aSf.Text = newStatus;

            var booksDb = rec.Database ?? BooksDb;
            var success = WithDatabase(booksDb, () => WriteRecordSafe(rec));
            return success 
                ? Result<bool>.Success(true) 
                : Result<bool>.Failure("Failed to write record");
        }

        private static Option<string> NormalizeTag(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) 
                return Option<string>.None;
            
            var sb = new StringBuilder(s.Trim());
            sb.Replace(" ", "").Replace("-", "").Replace(":", "");
            
            var result = sb.ToString();
            if (result.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(2);
            
            return Option<string>.Some(result.ToUpperInvariant());
        }

        private static string Dump910Short(MarcRecord rec, string rfid)
        {
            if (rec == null) 
                return "(rec=null)";
            
            var keyOpt = NormalizeId(rfid);
            if (!keyOpt.HasValue) 
                return "invalid key";
            
            var key = keyOpt.Value;
            RecordField block = null;
            foreach (var f in rec.Fields.GetField("910"))
            {
                var hOpt = NormalizeId(f.GetFirstSubFieldText('h'));
                if (hOpt.HasValue && hOpt.Value == key)
                {
                    block = f;
                    break;
                }
            }
            
            if (block == null) 
                return "910 not found";
            
            var a = block.GetFirstSubFieldText('a') ?? "";
            var b = block.GetFirstSubFieldText('b') ?? "";
            var h = block.GetFirstSubFieldText('h') ?? "";
            return "MFN=" + rec.Mfn + " 910: a=" + a + " b=" + b + " h=" + h;
        }

        /// <summary>Добавить повторение поля 40 в RDR при выдаче.</summary>
        public Result<bool> AppendRdr40OnIssue(int readerMfn, MarcRecord bookRec, string rfidHex, string maskMrg, string login, string catalogDbName)
        {
            if (readerMfn <= 0) 
                return Result<bool>.Failure("Invalid reader MFN");
            if (bookRec == null) 
                return Result<bool>.Failure("Book record is null");
            if (string.IsNullOrWhiteSpace(rfidHex)) 
                return Result<bool>.Failure("RFID is empty");

            var rdrDb = ReadersDb;
            UseDatabase(rdrDb);

            var rdr = ReadRecord(readerMfn);
            if (rdr == null) 
                return Result<bool>.Failure("Reader record not found");

            var normalizedRfid = NormalizeId(rfidHex);
            if (!normalizedRfid.HasValue) 
                return Result<bool>.Failure("Invalid RFID format");

            rfidHex = normalizedRfid.Value;

            RecordField ex910 = null;
            foreach (var f in bookRec.Fields.GetField("910"))
            {
                var hOpt = NormalizeId(f.GetFirstSubFieldText('h'));
                if (hOpt.HasValue && hOpt.Value == rfidHex)
                {
                    ex910 = f;
                    break;
                }
            }

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
            return ok 
                ? Result<bool>.Success(true) 
                : Result<bool>.Failure("Failed to write reader record");
        }

        /// <summary>Закрыть поле 40 при возврате (ищем по HIN=rfid книги в RDR, как в инструкции).</summary>
        public Result<bool> CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
            var normalizedRfid = NormalizeId(rfidHex);
            if (!normalizedRfid.HasValue) 
                return Result<bool>.Failure("Invalid RFID format");

            rfidHex = normalizedRfid.Value;

            var rdrDb = ReadersDb;
            UseDatabase(rdrDb);

            // По умолчанию — HIN={rfid}, см. инструкцию по возврату
            var expr = string.Format(
                ConfigurationManager.AppSettings["ExprReaderByItemRfid"] ?? "\"HIN={0}\"",
                rfidHex
            );

            var rdrOpt = FindOne(expr);
            if (!rdrOpt.HasValue) 
                return Result<bool>.Failure("Reader record not found by RFID");

            var rdr = rdrOpt.Value;

            RecordField f40 = null;
            foreach (var f in rdr.Fields.GetField("40"))
            {
                var h = f.GetFirstSubFieldText('H');
                var fVal = f.GetFirstSubFieldText('F');
                if (string.Equals(h, rfidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fVal, "******", StringComparison.OrdinalIgnoreCase))
                {
                    f40 = f;
                    break;
                }
            }
            
            if (f40 == null) 
                return Result<bool>.Failure("Field 40 not found for RFID");

            // ^C — удалить краткое описание
            f40.RemoveSubField('C');

            // ^R — место возврата
            var useR = true; // по умолчанию пишем R; можно сделать опцией
            if (useR)
            {
                var rVal = maskMrg ?? "";
                RecordField.SubField sfr = null;
                foreach (var sf in f40.SubFields)
                {
                    if (char.ToUpperInvariant(sf.Code) == 'R')
                    {
                        sfr = sf;
                        break;
                    }
                }
                if (sfr == null) 
                    f40.AddSubField('R', rVal); 
                else 
                    sfr.Text = rVal;
            }

            var now = DateTime.Now;
            var nowTime = now.ToString("HHmmss");
            RecordField.SubField sf2 = null;
            foreach (var sf in f40.SubFields)
            {
                if (sf.Code == '2')
                {
                    sf2 = sf;
                    break;
                }
            }
            if (sf2 == null) 
                f40.AddSubField('2', nowTime); 
            else 
                sf2.Text = nowTime;

            // ^F — фактическая дата возврата
            var nowDate = now.ToString("yyyyMMdd");
            RecordField.SubField sff = null;
            foreach (var sf in f40.SubFields)
            {
                if (char.ToUpperInvariant(sf.Code) == 'F')
                {
                    sff = sf;
                    break;
                }
            }
            if (sff == null) 
                f40.AddSubField('F', nowDate); 
            else 
                sff.Text = nowDate;

            var iVal = string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login;
            RecordField.SubField sfi = null;
            foreach (var sf in f40.SubFields)
            {
                if (char.ToUpperInvariant(sf.Code) == 'I')
                {
                    sfi = sf;
                    break;
                }
            }
            if (sfi == null) 
                f40.AddSubField('I', iVal); 
            else 
                sfi.Text = iVal;

            var ok = WriteRecordSafe(rdr);
            LogIrbis("CompleteRdr40 RFID=" + rfidHex + " ok=" + ok);
            return ok 
                ? Result<bool>.Success(true) 
                : Result<bool>.Failure("Failed to write reader record");
        }

        /// <summary>Выдача: СНАЧАЛА 40 в RDR, потом 910^A=1.</summary>
        public Result<string> IssueByRfid(string bookRfid)
        {
            var normalizedRfid = NormalizeId(bookRfid);
            if (!normalizedRfid.HasValue) 
                return Result<string>.Failure("Invalid RFID format");
            
            if (LastReaderMfn <= 0) 
                return Result<string>.Failure("Сначала вызови ValidateCard() для читателя.");

            bookRfid = normalizedRfid.Value;

            var bookOpt = FindOneByBookRfid(bookRfid);
            if (!bookOpt.HasValue) 
                return Result<string>.Failure("Книга по RFID не найдена.");

            var book = bookOpt.Value;
            var maskMrg = GetMaskMrg();
            var dbName = book.Database ?? BooksDb;

            var appendResult = AppendRdr40OnIssue(LastReaderMfn, book, bookRfid, maskMrg, CurrentLogin, dbName);
            if (appendResult.IsFailure)
                return Result<string>.Failure(appendResult.Error);

            var updateResult = UpdateBook910StatusByRfidStrict(book, bookRfid, "1");
            if (updateResult.IsFailure)
                return Result<string>.Failure(updateResult.Error);

            var brief = FormatBrief(BookBriefFormat, dbName, book.Mfn);
            return Result<string>.Success(string.IsNullOrWhiteSpace(brief) ? "[без описания]" : brief.Trim());
        }

        /// <summary>Возврат: СНАЧАЛА закрываем 40, потом 910^A=0.</summary>
        public Result<string> ReturnByRfid(string bookRfid)
        {
            var normalizedRfid = NormalizeId(bookRfid);
            if (!normalizedRfid.HasValue) 
                return Result<string>.Failure("Invalid RFID format");

            bookRfid = normalizedRfid.Value;

            var completeResult = CompleteRdr40OnReturn(bookRfid, GetMaskMrg(), CurrentLogin);
            if (completeResult.IsFailure)
                return Result<string>.Failure(completeResult.Error);

            var bookOpt = FindOneByBookRfid(bookRfid);
            if (!bookOpt.HasValue) 
                return Result<string>.Failure("Книга по RFID не найдена.");

            var book = bookOpt.Value;
            var updateResult = UpdateBook910StatusByRfidStrict(book, bookRfid, "0");
            if (updateResult.IsFailure)
                return Result<string>.Failure(updateResult.Error);

            var dbName = book.Database ?? BooksDb;
            var brief = FormatBrief(BookBriefFormat, dbName, book.Mfn);
            return Result<string>.Success(string.IsNullOrWhiteSpace(brief) ? "[без описания]" : brief.Trim());
        }

        public void Dispose()
        {
            try { _client?.Disconnect(); } catch { }
            _client = null;
        }
    }
}