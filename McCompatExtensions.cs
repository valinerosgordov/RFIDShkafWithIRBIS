// McCompatExtensions.cs
using System.Collections.Generic;
using System.Linq;

using ManagedClient;

namespace LibraryTerminal
{
    internal static class McCompatExtensions
    {
        public static IEnumerable<RecordField> FMs(this IrbisRecord record, int tag)
        {
            if (record == null) return Enumerable.Empty<RecordField>();
            return record.Fields.GetField(tag.ToString());
        }

        public static string Get(this RecordField field, char code)
        {
            if (field == null) return null;
            return field.GetSubFieldText(code, 0);
        }
    }
}