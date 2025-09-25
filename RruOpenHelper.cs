using System;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

static class RruOpenHelper
{
    // Поправь имя/StdCall/Ansi под свою DLL, если нужно
    [DllImport("RRU9816.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern int RRU9816_Open(string portName, int baudrate);

    private static string NormalizePort(string raw)
    {
        var p = (raw ?? "").Trim().ToUpperInvariant();
        var m = Regex.Match(p, @"^COM(\d+)$");
        if (m.Success)
        {
            int n;
            if (int.TryParse(m.Groups[1].Value, out n) && n > 9)
                p = @"\\.\\" + p;
        }
        return p;
    }

    private static void NudgeLines(string port, int baud)
    {
        SerialPort sp = null;
        try
        {
            sp = new SerialPort(port, baud, Parity.None, 8, StopBits.One);
            sp.Handshake = Handshake.None;
            sp.DtrEnable = true;
            sp.RtsEnable = true;
            sp.ReadTimeout = 250;
            sp.WriteTimeout = 250;
            sp.Open();
            Thread.Sleep(120);
        } catch
        {
            // не критично — просто идём дальше
        }
        finally
        {
            try { if (sp != null && sp.IsOpen) sp.Close(); } catch { }
            if (sp != null) sp.Dispose();
        }
    }

    public static int OpenLikeDemo(string rawPort, int preferredBaud, out int usedBaud)
    {
        string port = NormalizePort(rawPort);
        int[] tries = new int[] { preferredBaud, 115200, 57600, 38400, 19200 };
        int last = 48; // типичный "порт не открыт" в их API
        usedBaud = 0;

        for (int i = 0; i < tries.Length; i++)
        {
            int b = tries[i];
            NudgeLines(port, b);

            int rc = RRU9816_Open(port, b);
            if (rc == 0)
            {
                usedBaud = b;
                return 0;
            }
            last = rc;
            Thread.Sleep(120);
        }
        return last;
    }
}
