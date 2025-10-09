// SerialHelpers.cs
namespace LibraryTerminal
{
    internal static class SerialHelpers
    {
        public static void WriteLineSafe(SerialWorker worker, string line)
        {
            try { worker?.WriteLine(line); } catch { }
        }
    }
}