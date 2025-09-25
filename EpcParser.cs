using System;
using System.Linq;

namespace LibraryTerminal
{
    /// <summary>
    /// Тип метки: книга / читательская карта / неизвестно.
    /// Поле "Kind" в EPC-96 задаётся 4-битным нибблом (см. протокол).
    /// </summary>
    public enum TagKind
    { Book, Card, Unknown }

    /// <summary>
    /// Результат разбора EPC-96:
    ///  - Kind: тип метки (книга/билет)
    ///  - LibraryCode: код библиотеки/ЦБС (16 бит)
    ///  - Serial: серийный номер экземпляра (28 бит)
    ///  - RawHex: исходные 24 HEX-символа
    /// </summary>
    public sealed class EpcInfo
    {
        public TagKind Kind { get; set; }
        public int LibraryCode { get; set; }   // 0..65535
        public uint Serial { get; set; }        // 0..268435455
        public string RawHex { get; set; }        // 24 hex chars
    }

    /// <summary>
    /// Разбор EPC-96 согласно протоколу из ИРБИС:
    /// Старшие 48 бит ("шапка") фиксированы — по ним фильтруем "свои" метки.
    /// Младшие 48 бит раскладываются так:
    ///   [47..32]  LibraryCode (16 бит)
    ///   [31..28]  Kind (4 бита): 0 = книга, F = читательский билет
    ///   [27..0 ]  Serial (28 бит)
    /// </summary>
    public static class EpcParser
    {
        // Старшие 6 байт EPC (48 бит) — фиксированная "шапка" из документа.
        private static readonly byte[] Header = { 0x30, 0x4D, 0xB7, 0x5F, 0x19, 0x60 };

        /// <summary>
        /// Принимает EPC как строку из 24 HEX-символов, возвращает распарсенный объект.
        /// Если формат неверен или "шапка" не совпала — вернёт null.
        /// </summary>
        public static EpcInfo Parse(string epcHex)
        {
            if (string.IsNullOrWhiteSpace(epcHex) || epcHex.Length != 24) return null;

            byte[] bytes;
            try
            {
                // Преобразуем 24 HEX-символа → 12 байт
                bytes = Enumerable.Range(0, 12)
                    .Select(i => Convert.ToByte(epcHex.Substring(i * 2, 2), 16))
                    .ToArray();
            } catch { return null; }

            // 1) Проверяем "шапку"
            for (int i = 0; i < 6; i++)
                if (bytes[i] != Header[i]) return null;

            // 2) Читаем младшие 48 бит в ulong
            ulong low48 = 0;
            for (int i = 6; i < 12; i++)
                low48 = (low48 << 8) | bytes[i];

            // 3) Разбираем поля согласно протоколу
            int library = (int)((low48 >> 32) & 0xFFFF);
            int kindNibble = (int)((low48 >> 28) & 0xF);
            uint serial = (uint)(low48 & 0x0FFFFFFF);

            TagKind kind;
            if (kindNibble == 0x0) kind = TagKind.Book;
            else if (kindNibble == 0xF) kind = TagKind.Card;
            else kind = TagKind.Unknown;

            return new EpcInfo
            {
                Kind = kind,
                LibraryCode = library,
                Serial = serial,
                RawHex = epcHex
            };
        }
    }
}