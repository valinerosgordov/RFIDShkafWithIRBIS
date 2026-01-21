using System;
using LibraryTerminal.Core;

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
    public record EpcInfo(
        TagKind Kind,
        int LibraryCode,
        uint Serial,
        string RawHex
    );

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
        /// Если формат неверен или "шапка" не совпала — вернёт None.
        /// </summary>
        public static Option<EpcInfo> Parse(string epcHex)
        {
            if (string.IsNullOrWhiteSpace(epcHex) || epcHex.Length != 24) 
                return Option<EpcInfo>.None;

            var bytes = new byte[12];
            try
            {
                // Преобразуем 24 HEX-символа → 12 байт (без LINQ для производительности)
                for (var i = 0; i < 12; i++)
                {
                    bytes[i] = Convert.ToByte(epcHex.Substring(i * 2, 2), 16);
                }
            } 
            catch 
            { 
                return Option<EpcInfo>.None; 
            }

            // 1) Проверяем "шапку"
            for (var i = 0; i < 6; i++)
            {
                if (bytes[i] != Header[i]) 
                    return Option<EpcInfo>.None;
            }

            // 2) Читаем младшие 48 бит в ulong
            ulong low48 = 0;
            for (var i = 6; i < 12; i++)
            {
                low48 = (low48 << 8) | bytes[i];
            }

            // 3) Разбираем поля согласно протоколу
            var library = (int)((low48 >> 32) & 0xFFFF);
            var kindNibble = (int)((low48 >> 28) & 0xF);
            var serial = (uint)(low48 & 0x0FFFFFFF);

            var kind = kindNibble switch
            {
                0x0 => TagKind.Book,
                0xF => TagKind.Card,
                _ => TagKind.Unknown
            };

            return Option<EpcInfo>.Some(new EpcInfo(kind, library, serial, epcHex));
        }
    }
}