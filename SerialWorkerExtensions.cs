using System;

namespace LibraryTerminal
{
    internal static class SerialWorkerExtensions
    {
        public static void WriteLineSafe(this SerialWorker sw, string line)
        {
            try { sw?.WriteLine(line); } catch { }
        }
    }
}
