using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

